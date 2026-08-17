using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// Guards the localization data, which fails SILENTLY everywhere else: CheapLoc swallows a missing or
/// malformed locale file and falls back to English, so a translation that never loads looks exactly like
/// one that was never written. Nothing at runtime will tell you. These tests are the only thing that will.
/// </summary>
public sealed class LocalizationTests
{
    private const string Source = "en";

    private static readonly string[] Locales = ["en", "ja", "de", "fr", "zh", "ko", "es", "ru"];

    public static TheoryData<string> Translations
    {
        get
        {
            var d = new TheoryData<string>();
            foreach (var l in Locales.Where(l => l != Source)) d.Add(l);
            return d;
        }
    }

    /// <summary>Every locale including the English source — the format check applies to en.json too.</summary>
    public static TheoryData<string> AllLocales
    {
        get
        {
            var d = new TheoryData<string>();
            foreach (var l in Locales) d.Add(l);
            return d;
        }
    }

    private sealed class LocEntry
    {
        public string Message { get; set; } = "";
        public string Description { get; set; } = "";
    }

    private static System.Reflection.Assembly ProteusAssembly => typeof(Proteus.Plugin).Assembly;

    private static Dictionary<string, LocEntry> Load(string lang)
    {
        var name = $"Proteus.Localization.{lang}.json";
        using var s = ProteusAssembly.GetManifestResourceStream(name);
        Assert.True(s != null,
            $"{name} is not an embedded resource. Check the <EmbeddedResource Include=\"Localization\\*.json\" /> " +
            "item in Proteus.csproj — Dalamud.Localization resolves this exact manifest name, and a miss " +
            "degrades to English without any error at runtime.");

        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        return JsonSerializer.Deserialize<Dictionary<string, LocEntry>>(s!, opts)!;
    }

    /// <summary>{0}, {0,-8}, {0:0.##} — but not the {{0}} escape.</summary>
    private static readonly Regex PlaceholderRx = new(@"(?<!\{)\{(\d+)(?:,-?\d+)?(?::[^}]*)?\}", RegexOptions.Compiled);

    private static HashSet<int> Placeholders(string s)
    {
        var set = new HashSet<int>();
        foreach (Match m in PlaceholderRx.Matches(s))
            set.Add(int.Parse(m.Groups[1].Value));
        return set;
    }

    [Fact]
    public void EveryLocaleIsEmbeddedAndParses()
    {
        foreach (var lang in Locales)
            Assert.NotEmpty(Load(lang));
    }

    /// <summary>
    /// CheapLoc returns <c>"#" + key</c> — not the English fallback — when the calling assembly has never
    /// been set up. That is the difference between an unconfigured plugin showing English and showing
    /// "#Settings.General.Enabled.Label" all over its window, and it is silent either way.
    /// <see cref="LocalizationFallbackInit"/> performs the setup here; <c>LocSetup</c>'s constructor does
    /// it in the plugin, before any service or window exists.
    /// </summary>
    [Fact]
    public void UnsetLanguageFallsBackToEnglishNotToTheKey()
    {
        var probe = CheapLoc.Loc.Localize("Tab.Settings", "Settings", ProteusAssembly);
        Assert.Equal("Settings", probe);
        Assert.DoesNotContain("#", probe);
    }

    [Theory]
    [MemberData(nameof(Translations))]
    public void KeysMatchEnglish(string lang)
    {
        var en = Load(Source);
        var tr = Load(lang);

        var missing  = en.Keys.Except(tr.Keys).Order().ToList();
        var orphaned = tr.Keys.Except(en.Keys).Order().ToList();

        // Missing keys merely fall back to English, but silently — which is how a half-finished translation
        // ships. Orphans are worse: they are translations of a key that was renamed or deleted, so they
        // will never be shown again and hide the fact that something now has no translation at all.
        Assert.True(missing.Count == 0, $"[{lang}] missing {missing.Count} key(s):\n  " + string.Join("\n  ", missing));
        Assert.True(orphaned.Count == 0, $"[{lang}] has {orphaned.Count} key(s) not in {Source}.json:\n  " + string.Join("\n  ", orphaned));
    }

    [Theory]
    [MemberData(nameof(Translations))]
    public void PlaceholderIndicesMatchEnglish(string lang)
    {
        var en = Load(Source);
        var tr = Load(lang);
        var problems = new List<string>();

        foreach (var (key, source) in en)
        {
            if (!tr.TryGetValue(key, out var t)) continue;   // KeysMatchEnglish reports this

            var expected = Placeholders(source.Message);
            var actual   = Placeholders(t.Message);

            // A SET, not a sequence. Reordering {0} past {1} is exactly what a translator must be free to
            // do — German pushes the verb to the end and Japanese is subject-object-verb — so only the
            // presence of each index matters. Dropping one leaves a hole in the sentence; inventing one
            // throws FormatException inside the ImGui draw loop, on a user's machine, at the moment the
            // string is first shown.
            if (!expected.SetEquals(actual))
                problems.Add($"  {key}: en has {{{string.Join(",", expected.Order())}}}, {lang} has {{{string.Join(",", actual.Order())}}}");
        }

        Assert.True(problems.Count == 0, $"[{lang}] placeholder mismatch:\n" + string.Join("\n", problems));
    }

    /// <summary>
    /// Actually runs <c>string.Format</c> over every <c>.Fmt</c> value, which is the only check that
    /// catches a MALFORMED brace.
    /// <para/>
    /// <see cref="PlaceholderIndicesMatchEnglish"/> compares the placeholders it can parse, so a stray
    /// <c>{</c>, an unclosed <c>{0</c> or a named <c>{name}</c> yields nothing to compare, matches English
    /// vacuously, and sails through — then throws <c>FormatException</c> the first time the string is
    /// drawn, inside the ImGui draw loop, on a translator's users' machines rather than on theirs.
    /// Formatting it here for real is the whole guarantee the translator workflow rests on.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllLocales))]
    public void EveryFormatStringSurvivesStringFormat(string lang)
    {
        var en = Load(Source);
        var tr = Load(lang);
        var problems = new List<string>();

        foreach (var (key, entry) in tr)
        {
            if (!key.EndsWith(".Fmt", StringComparison.Ordinal)) continue;
            if (!en.TryGetValue(key, out var source)) continue;   // KeysMatchEnglish reports this

            // The English template decides how many arguments the call site passes.
            var indices = Placeholders(source.Message);
            var args = Enumerable.Range(0, indices.Count == 0 ? 1 : indices.Max() + 1)
                                 .Select(i => (object)$"<{i}>")
                                 .ToArray();

            var ex = Record.Exception(() => string.Format(entry.Message, args));
            if (ex != null)
                problems.Add($"  {key}: {ex.GetType().Name} — {ex.Message}\n    value: {entry.Message}");
        }

        Assert.True(problems.Count == 0,
            $"[{lang}] value(s) that string.Format cannot render — most likely an unbalanced or " +
            $"non-numeric brace:\n" + string.Join("\n", problems));
    }

    [Theory]
    [MemberData(nameof(Translations))]
    public void NoEmptyMessages(string lang)
    {
        var blank = Load(lang).Where(kv => string.IsNullOrWhiteSpace(kv.Value.Message)).Select(kv => kv.Key).Order().ToList();
        Assert.True(blank.Count == 0, $"[{lang}] blank message for:\n  " + string.Join("\n  ", blank));
    }

    /// <summary>
    /// The one that keeps <c>en.json</c> honest, and the reason there is no export command in the plugin:
    /// CheapLoc's own <c>ExportLocalizable</c> reads <c>Assembly.Location</c> and hands it to Mono.Cecil,
    /// and Dalamud loads plugins from a stream, so at runtime that path is empty and the call throws. Here
    /// the assembly is a real file, so the same IL walk works — and it runs in CI on every push instead of
    /// whenever someone remembers to invoke a tool.
    /// <para/>
    /// It also enforces the literal-argument rule that makes translation possible at all.
    /// </summary>
    [Fact]
    public void CodeKeysMatchEnglishJson()
    {
        using var asm = AssemblyDefinition.ReadAssembly(ProteusAssembly.Location);

        var found = new Dictionary<string, string>();
        var nonLiteral = new List<string>();

        foreach (var type in asm.MainModule.GetTypes())
        foreach (var method in type.Methods.Where(m => m.HasBody))
        foreach (var ins in method.Body.Instructions)
        {
            if (ins.OpCode != OpCodes.Call || ins.Operand is not MethodReference mr) continue;
            if (mr.DeclaringType.FullName != "CheapLoc.Loc" || mr.Name != "Localize") continue;

            var fallback = ins.Previous;
            var key      = fallback?.Previous;

            if (key?.OpCode != OpCodes.Ldstr || fallback?.OpCode != OpCodes.Ldstr)
            {
                nonLiteral.Add($"  {type.FullName}.{method.Name}");
                continue;
            }

            found[(string)key.Operand] = (string)fallback.Operand;
        }

        // CheapLoc reads the two instructions immediately before the call, so anything that is not a pair
        // of ldstr is invisible to the exporter and can never be translated — an interpolated $"..." most
        // of all. "a" + "b" is folded by the compiler into one ldstr and is fine.
        Assert.True(nonLiteral.Count == 0,
            "Loc.Localize called with a non-literal key or fallback. Use string.Format on a literal " +
            "template instead of an interpolated string:\n" + string.Join("\n", nonLiteral.Distinct()));

        var en = Load(Source);

        var undocumented = found.Keys.Except(en.Keys).Order().ToList();
        var stale        = en.Keys.Except(found.Keys).Order().ToList();

        // Printed as ready-to-paste JSON: this test is also how en.json gets written, so a failure should
        // hand over the fix rather than merely describe it.
        if (undocumented.Count > 0)
        {
            var block = string.Join(",\n", undocumented.Select(k =>
                $"  {JsonSerializer.Serialize(k)}: {{\n" +
                $"    \"message\": {JsonSerializer.Serialize(found[k])},\n" +
                $"    \"description\": \"\"\n  }}"));
            Assert.Fail($"{undocumented.Count} key(s) exist in code but not in Localization/en.json.\n" +
                        $"Paste into en.json:\n{block}");
        }

        Assert.True(stale.Count == 0,
            $"{stale.Count} key(s) in en.json are no longer used by any Loc.Localize call. Remove them " +
            $"from all 8 locale files:\n  " + string.Join("\n  ", stale));

        foreach (var (key, fallbackText) in found)
            Assert.True(en[key].Message == fallbackText,
                $"en.json disagrees with the fallback compiled into the code for \"{key}\".\n" +
                $"  code: {fallbackText}\n  json: {en[key].Message}");
    }

    /// <summary>
    /// The <c>.Fmt</c> suffix is load-bearing, not decorative: it is how a translator knows a string
    /// carries arguments that must survive into their language.
    /// </summary>
    [Fact]
    public void FormatKeysAreNamedFmt()
    {
        var problems = new List<string>();
        foreach (var (key, entry) in Load(Source))
        {
            var hasArgs = Placeholders(entry.Message).Count > 0;
            var saysFmt = key.EndsWith(".Fmt", StringComparison.Ordinal);

            if (hasArgs && !saysFmt) problems.Add($"  {key} takes arguments but is not named *.Fmt");
            if (!hasArgs && saysFmt) problems.Add($"  {key} is named *.Fmt but takes no arguments");
        }

        Assert.True(problems.Count == 0, "Key naming:\n" + string.Join("\n", problems));
    }
}
