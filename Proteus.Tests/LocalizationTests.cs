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

    /// <summary>
    /// The strings shoes and gloves keeping their own hands and feet added are translated, not copied: a copied English
    /// value passes every other check here, so a key can ship untranslated without a test noticing.
    /// </summary>
    [Theory]
    [MemberData(nameof(Translations))]
    [Trait("Plan", "AC-keep-hand-foot-skin-9")]
    public void KeepEnds_strings_are_translated_not_copied(string lang)
    {
        var en = Load(Source);
        var tr = Load(lang);
        string[] keys = ["AutoRefit.EndsKept", "Chat.AutoRefit.KeepsFeet.Fmt", "Chat.AutoRefit.KeepsHands.Fmt",
                         "Chat.AutoRefit.SkinOnly.Fmt"];
        foreach (string key in keys)
        {
            Assert.True(tr.ContainsKey(key), $"[{lang}] has no {key}");
            Assert.NotEqual(en[key].Message, tr[key].Message);
        }
    }

    /// <summary>What the automatic refit says about a garment on an accessory slot, and the Body refit section's intro
    /// that now names them, are translated, not copied.</summary>
    [Theory]
    [MemberData(nameof(Translations))]
    [Trait("Plan", "AC-jacket-refit-11")]
    public void Accessory_refit_strings_are_translated_not_copied(string lang)
    {
        var en = Load(Source);
        var tr = Load(lang);
        foreach (string key in new[]
                 {
                     "AutoRefit.Intro", "Chat.AutoRefit.Accessory.Jewellery.Fmt", "Chat.AutoRefit.Accessory.AuthorSizes.Fmt",
                     "Chat.AutoRefit.Accessory.Beneath.Fmt", "Chat.AutoRefit.Accessory.Shared.Fmt",
                 })
        {
            Assert.True(tr.ContainsKey(key), $"[{lang}] has no {key}");
            Assert.NotEqual(en[key].Message, tr[key].Message);
        }
    }

    /// <summary>The README's gear-refits-itself bullet names accessories that cover the body, in each language — and the
    /// Settings intro says it too.</summary>
    [Theory]
    [MemberData(nameof(AllLocales))]
    [Trait("Plan", "AC-jacket-refit-11")]
    public void Readme_and_intro_say_accessories_are_refitted(string lang)
    {
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "proteus.slnx"))) dir = dir.Parent;
        Assert.True(dir != null, $"Could not find proteus.slnx above {AppContext.BaseDirectory}.");
        string readme = System.IO.File.ReadAllText(System.IO.Path.Combine(
            dir!.FullName, lang == Source ? "README.md" : System.IO.Path.Combine("docs", $"README.{lang}.md")));

        string bullet = readme.Split('\n').Single(l => l.Contains("**" + ReadmeRefitItself[lang] + "**"));
        Assert.Contains(Necklace[lang], bullet);
        Assert.Contains(Necklace[lang], Load(lang)["AutoRefit.Intro"].Message);
    }

    /// <summary>The gear-refits-itself bullet's bold label in each README.</summary>
    private static readonly Dictionary<string, string> ReadmeRefitItself = new()
    {
        ["en"] = "Have gear refit itself as you put it on.", ["de"] = "Lass Ausrüstung sich beim Anlegen selbst anpassen.",
        ["es"] = "Haz que el equipo se ajuste solo al ponértelo.",
        ["fr"] = "Laissez l'équipement s'ajuster tout seul quand vous le portez.",
        ["ja"] = "装備を身に着けると、自動で体に合わせ直されます。", ["ko"] = "장비를 입는 순간 저절로 내 몸에 맞춰지게 하세요.",
        ["ru"] = "Пусть снаряжение подгоняется само, когда вы его надеваете.", ["zh"] = "让装备在穿上时自动改配。",
    };

    /// <summary>"Necklace", as each language's README bullet and intro name the slot a jacket is put on.</summary>
    private static readonly Dictionary<string, string> Necklace = new()
    {
        ["en"] = "necklace", ["de"] = "Halskette", ["es"] = "collar", ["fr"] = "collier", ["ja"] = "首飾り",
        ["ko"] = "목걸이", ["ru"] = "ожерелье", ["zh"] = "项链",
    };

    /// <summary>The navel tick is translated, not copied, and its tooltip keeps English's line breaks.</summary>
    [Theory]
    [MemberData(nameof(Translations))]
    [Trait("Plan", "AC-navel-bridge-1")]
    public void NavelBridge_strings_are_translated_not_copied(string lang)
    {
        var en = Load(Source);
        var tr = Load(lang);
        foreach (string key in new[] { "Colors.NavelBridge.Label", "Colors.NavelBridge.Tip" })
        {
            Assert.True(tr.ContainsKey(key), $"[{lang}] has no {key}");
            Assert.NotEqual(en[key].Message, tr[key].Message);
            Assert.Equal(en[key].Message.Count(c => c == '\n'), tr[key].Message.Count(c => c == '\n'));
        }
    }

    /// <summary>
    /// The words for a target option that draws underwear where the source has skin — the refusal, and the two Body size
    /// notes — are translated, not copied.
    /// </summary>
    [Theory]
    [MemberData(nameof(Translations))]
    [Trait("Plan", "AC-skinless-target-4")]
    [Trait("Plan", "EDGE-skinless-target-34")]
    public void NoSkinThere_is_translated_not_copied(string lang)
    {
        var en = Load(Source);
        var tr = Load(lang);
        foreach (string key in new[] { "Parts.Retarget.Refuse.NoSkinThere.Fmt", "Parts.Retarget.Smallclothes.AfterSave",
                                       "Parts.Retarget.Smallclothes.Fallback", "Parts.Retarget.Save.NoMaterialSet.Fmt",
                                       "Parts.Retarget.Save.NoMaterialName.Fmt" })
        {
            Assert.True(tr.ContainsKey(key), $"[{lang}] has no {key}");
            Assert.NotEqual(en[key].Message, tr[key].Message);
        }
    }

    /// <summary>
    /// The Keep shape release's words: the warning, and the tooltip's last line — under a key of its own, so a
    /// translation of the tooltip as it was before cannot stand in for it.
    /// </summary>
    [Theory]
    [MemberData(nameof(Translations))]
    [Trait("Plan", "AC-keep-shape-release-5")]
    [Trait("Plan", "AC-keep-shape-release-6")]
    public void KeepShapeRelease_strings_are_translated_not_copied(string lang)
    {
        var en = Load(Source);
        var tr = Load(lang);
        foreach (string key in new[] { "Parts.Retarget.ShapeReleased.Fmt", "Parts.Retarget.KeepShape.SinkTip" })
        {
            Assert.True(tr.ContainsKey(key), $"[{lang}] has no {key}");
            Assert.NotEqual(en[key].Message, tr[key].Message);
        }
    }

    /// <summary>The README's Keep shape bullet says it too, in each language's own words — the same sentence as the
    /// tooltip's last line.</summary>
    [Theory]
    [MemberData(nameof(AllLocales))]
    [Trait("Plan", "AC-keep-shape-release-6")]
    public void KeepShape_tip_and_readme_say_buried_pieces_bend(string lang)
    {
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "proteus.slnx"))) dir = dir.Parent;
        Assert.True(dir != null, $"Could not find proteus.slnx above {AppContext.BaseDirectory}.");
        string readme = System.IO.File.ReadAllText(System.IO.Path.Combine(
            dir!.FullName, lang == Source ? "README.md" : System.IO.Path.Combine("docs", $"README.{lang}.md")));

        string sentence = Load(lang)["Parts.Retarget.KeepShape.SinkTip"].Message;
        string bullet = readme.Split('\n').Single(l => l.Contains("**" + ReadmeKeepShape[lang] + "**"));
        Assert.Contains(sentence, bullet);
    }

    /// <summary>The size set's strings that say each size is cut from the author's nearest one are translated, not
    /// copied.</summary>
    [Theory]
    [MemberData(nameof(Translations))]
    [Trait("Plan", "AC-bulk-nearest-source-7")]
    public void SizeSet_source_strings_are_translated_not_copied(string lang)
    {
        var en = Load(Source);
        var tr = Load(lang);
        foreach (string key in new[]
                 {
                     "SizeSet.Intro", "SizeSet.Remake.Tip", "SizeSet.EndNoLeadSource.Fmt", "SizeSet.EndNoLeadSourceRemake.Fmt",
                 })
        {
            Assert.True(tr.ContainsKey(key), $"[{lang}] has no {key}");
            Assert.NotEqual(en[key].Message, tr[key].Message);
        }
    }

    /// <summary>
    /// No README still says the size set cuts from the author's first size, or that refitting from smaller to larger
    /// works best — the size nearest the one wanted does.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllLocales))]
    [Trait("Plan", "AC-bulk-nearest-source-7")]
    public void Readme_says_sizes_are_cut_from_the_nearest(string lang)
    {
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "proteus.slnx"))) dir = dir.Parent;
        Assert.True(dir != null, $"Could not find proteus.slnx above {AppContext.BaseDirectory}.");
        string readme = System.IO.File.ReadAllText(System.IO.Path.Combine(
            dir!.FullName, lang == Source ? "README.md" : System.IO.Path.Combine("docs", $"README.{lang}.md")));
        foreach (string old in ReadmeFirstSize[lang]) Assert.DoesNotContain(old, readme);
        // The by-hand tip, the size set's rule, and the "ran it before" sentence are there.
        foreach (string now in ReadmeNearestSize[lang]) Assert.Contains(now, readme);
        // The "ran it before" sentence names the remake checkbox's own label.
        Assert.Contains(Load(lang)["SizeSet.Remake"].Message, readme);
    }

    /// <summary>What each README says now: the by-hand tip, the size set's rule, and the sentence for earlier runs.</summary>
    private static readonly Dictionary<string, string[]> ReadmeNearestSize = new()
    {
        ["en"] = ["nearest the one you want", "the author's size nearest it", "If you ran it with an earlier version of Proteus"],
        ["de"] = ["die der gewünschten am nächsten ist", "aus der ihr nächsten Größe des Autors",
                  "Hast du es mit einer früheren Proteus-Version ausgeführt"],
        ["es"] = ["más cercana a la que quieres", "desde la talla del autor más cercana a ella",
                  "Si lo usaste con una versión anterior de Proteus"],
        ["fr"] = ["la plus proche de celle voulue", "depuis la taille de l'auteur la plus proche (",
                  "Si vous l'avez lancé avec une version antérieure de Proteus"],
        ["ja"] = ["目的のサイズに最も近いものから", "作者のサイズのうち最も近いものから合わせ直され", "以前のバージョンのProteusで実行した場合は"],
        ["ko"] = ["원하는 사이즈에 가장 가까운 제작자의 사이즈에서", "제작자의 사이즈 중 가장 가까운 것에서 리핏되고", "이전 버전의 Proteus로 실행했다면"],
        ["ru"] = ["Перешивайте с ближайшего к нужному размера автора", "с ближайшего к нему авторского размера",
                  "Если вы запускали это в более ранней версии Proteus"],
        ["zh"] = ["请从最接近目标尺寸的作者尺寸改配", "从作者最接近它的尺寸改配", "如果你曾用较早版本的 Proteus 运行过"],
    };

    /// <summary>What each README said before: the size set's old rule, and the old by-hand tip.</summary>
    private static readonly Dictionary<string, string[]> ReadmeFirstSize = new()
    {
        ["en"] = ["author's first size into every size", "smaller size to a larger one"],
        ["de"] = ["aus der ersten Größe des Autors in jede Größe", "Von einer kleineren auf eine größere Größe"],
        ["es"] = ["desde la primera talla del autor a cada talla", "de una talla menor a una mayor"],
        ["fr"] = ["depuis la première taille de l'auteur vers chaque taille", "d'une petite taille vers une plus grande"],
        ["ja"] = ["作者の最初のサイズからそれらのボディ", "小さいサイズから大きいサイズへ"],
        ["ko"] = ["제작자의 첫 번째 사이즈에서 그 바디들의", "작은 크기에서 큰 크기로"],
        ["ru"] = ["с первого авторского размера на каждый", "с меньшего размера на больший"],
        ["zh"] = ["从作者的第一个尺寸改配到这些身体", "从较小的尺寸改配到较大的尺寸"],
    };

    /// <summary>The part drawer's tooltip line is the one new string, and every language has its own words for it.</summary>
    [Theory]
    [MemberData(nameof(Translations))]
    [Trait("Plan", "AC-part-drawer-9")]
    public void PartDrawer_strings_are_translated_not_copied(string lang)
    {
        var en = Load(Source);
        var tr = Load(lang);
        const string key = "Parts.Drawer.Toggle.Tip";
        Assert.True(tr.ContainsKey(key), $"[{lang}] has no {key}");
        Assert.NotEqual(en[key].Message, tr[key].Message);
    }

    /// <summary>The part list is no longer a dropdown, and no README still says it is.</summary>
    [Theory]
    [MemberData(nameof(AllLocales))]
    [Trait("Plan", "AC-part-drawer-9")]
    public void Readme_says_part_list_not_dropdown(string lang)
    {
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "proteus.slnx"))) dir = dir.Parent;
        Assert.True(dir != null, $"Could not find proteus.slnx above {AppContext.BaseDirectory}.");
        string readme = System.IO.File.ReadAllText(System.IO.Path.Combine(
            dir!.FullName, lang == Source ? "README.md" : System.IO.Path.Combine("docs", $"README.{lang}.md")));

        foreach (string phrase in ReadmePartDropdown[lang])
            Assert.DoesNotContain(phrase, readme);
    }

    /// <summary>How each README used to describe the part list as a dropdown: the Studio layout line, then the lock bullet.</summary>
    private static readonly Dictionary<string, string[]> ReadmePartDropdown = new()
    {
        ["en"] = ["parts in a dropdown", "part dropdown"],
        ["ja"] = ["パーツのドロップダウン"],
        ["de"] = ["Auswahlliste ganz oben in den Einstellungen", "Teile-Auswahlliste"],
        ["es"] = ["lista desplegable en la parte superior de los ajustes", "lista desplegable de piezas"],
        ["fr"] = ["liste déroulante en haut des réglages", "liste déroulante des pièces"],
        ["ko"] = ["설정 맨 위의 드롭다운", "부위 드롭다운"],
        ["ru"] = ["выпадающем списке вверху настроек", "выпадающем списке частей"],
        ["zh"] = ["设置顶部的下拉列表", "部件下拉列表"],
    };

    private static readonly string[] PiercingsKeys =
    [
        "AutoRefit.Piercings.Label", "AutoRefit.Piercings.Tip", "Parts.Retarget.Piercings",
        "Parts.Retarget.Piercings.Tip", "SizeSet.Piercings", "SizeSet.Piercings.Tip",
    ];

    /// <summary>The refit's three piercings options are in every language in its own words, line for line.</summary>
    [Theory]
    [MemberData(nameof(Translations))]
    [Trait("Plan", "AC-refit-piercings-option-9")]
    public void RefitPiercings_strings_are_translated_not_copied(string lang)
    {
        // [AC-refit-piercings-option-9]
        var en = Load(Source);
        var tr = Load(lang);
        foreach (string key in PiercingsKeys)
        {
            Assert.True(tr.ContainsKey(key), $"[{lang}] has no {key}");
            Assert.NotEqual(en[key].Message, tr[key].Message);
            Assert.Equal(en[key].Message.Count(c => c == '\n'), tr[key].Message.Count(c => c == '\n'));
        }
    }

    /// <summary>Every README names the three piercings options as its own language's checkboxes do.</summary>
    [Theory]
    [MemberData(nameof(AllLocales))]
    [Trait("Plan", "AC-refit-piercings-option-9")]
    public void Readme_mentions_the_piercings_options(string lang)
    {
        // [AC-refit-piercings-option-9]
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "proteus.slnx"))) dir = dir.Parent;
        Assert.True(dir != null, $"Could not find proteus.slnx above {AppContext.BaseDirectory}.");
        string readme = System.IO.File.ReadAllText(System.IO.Path.Combine(
            dir!.FullName, lang == Source ? "README.md" : System.IO.Path.Combine("docs", $"README.{lang}.md")));

        var strings = Load(lang);
        foreach (string key in new[] { "AutoRefit.Piercings.Label", "Parts.Retarget.Piercings", "SizeSet.Piercings" })
            Assert.Contains("**" + strings[key].Message + "**", readme);
    }

    /// <summary>The Scale tool's three strings were re-keyed when it gained axis handles; every language has its own words.</summary>
    [Theory]
    [MemberData(nameof(Translations))]
    [Trait("Plan", "AC-axis-scale-6")]
    [Trait("Plan", "EDGE-axis-scale-15")]
    public void ScaleAxes_strings_are_translated_not_copied(string lang)
    {
        var en = Load(Source);
        var tr = Load(lang);
        foreach (string key in new[] { "Parts.Tool.ScaleAxes.Tip", "Parts.ScaleAxes.Help", "Parts.ScaleAxes.LiveHint" })
        {
            Assert.True(tr.ContainsKey(key), $"[{lang}] has no {key}");
            Assert.NotEqual(en[key].Message, tr[key].Message);
        }
    }

    /// <summary>Every README's Scale row describes the axis handles, and none still describes only the even drag.</summary>
    [Theory]
    [MemberData(nameof(AllLocales))]
    [Trait("Plan", "AC-axis-scale-6")]
    public void Readme_scale_row_describes_axis_handles(string lang)
    {
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "proteus.slnx"))) dir = dir.Parent;
        Assert.True(dir != null, $"Could not find proteus.slnx above {AppContext.BaseDirectory}.");
        string readme = System.IO.File.ReadAllText(System.IO.Path.Combine(
            dir!.FullName, lang == Source ? "README.md" : System.IO.Path.Combine("docs", $"README.{lang}.md")));

        var (old, now) = ReadmeScaleRow[lang];
        Assert.DoesNotContain(old, readme);
        Assert.Contains(now, readme);
    }

    /// <summary>Each README's Scale row: how it opened before the axis handles, and the handle phrase it carries now.</summary>
    private static readonly Dictionary<string, (string Old, string New)> ReadmeScaleRow = new()
    {
        ["en"] = ("Grows or shrinks a part about its centre: press", "drag a coloured handle to stretch it along that axis"),
        ["ja"] = ("パーツを中心のまわりに拡大・縮小します。押したまま", "色付きのハンドルをドラッグするとその軸方向に伸縮し"),
        ["de"] = ("Vergrößert oder verkleinert ein Teil um seine Mitte: Drücke", "Ziehe an einem farbigen Griff, um es entlang dieser Achse zu strecken"),
        ["es"] = ("Agranda o encoge una pieza alrededor de su centro: presiona", "arrastra un tirador de color para estirarla a lo largo de ese eje"),
        ["fr"] = ("Agrandit ou rétrécit une pièce autour de son centre : appuyez", "faites glisser une poignée colorée pour l'étirer le long de cet axe"),
        ["ko"] = ("부위를 중심을 기준으로 키우거나 줄입니다. 누른", "색깔 핸들을 끌면 그 축 방향으로 늘이거나 줄이고"),
        ["ru"] = ("Увеличивает или уменьшает деталь относительно её центра: нажмите", "тяните цветную ручку, чтобы растянуть её вдоль этой оси"),
        ["zh"] = ("让部件绕其中心放大或缩小：按住", "拖动彩色手柄沿该轴拉伸"),
    };

    /// <summary>The Keep shape bullet's bold label in each README.</summary>
    private static readonly Dictionary<string, string> ReadmeKeepShape = new()
    {
        ["en"] = "Keep shape", ["ja"] = "形を保つ", ["de"] = "Form behalten", ["fr"] = "Garder la forme",
        ["zh"] = "保持形状", ["ko"] = "형태 유지", ["es"] = "Mantener forma", ["ru"] = "Сохранить форму",
    };

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
    /// The FORMAT SPECIFIER on each placeholder has to survive translation, even though the order does not.
    /// <para/>
    /// <see cref="PlaceholderIndicesMatchEnglish"/> cannot see this: it reduces every placeholder to its
    /// index, so <c>{1}</c> and <c>{1:P0}</c> compare equal and a dropped specifier passes. It is not a
    /// cosmetic difference — <c>:P0</c> turns 0.95 into "95 %", and without it the sentence reads "glows
    /// across 0.95 of the body". Caught in the wild exactly that way, in seven languages at once.
    /// <para/>
    /// A specifier is the machine-readable half of a placeholder and is never a translator's to change,
    /// unlike the order, which is theirs entirely.
    /// </summary>
    [Theory]
    [MemberData(nameof(Translations))]
    public void PlaceholderFormatSpecifiersMatchEnglish(string lang)
    {
        static Dictionary<int, string> Specifiers(string s)
        {
            var map = new Dictionary<int, string>();
            foreach (Match m in PlaceholderRx.Matches(s))
                map[int.Parse(m.Groups[1].Value)] = m.Value;
            return map;
        }

        var en = Load(Source);
        var tr = Load(lang);
        var problems = new List<string>();

        foreach (var (key, source) in en)
        {
            if (!tr.TryGetValue(key, out var t)) continue;   // KeysMatchEnglish reports this

            // Index → the whole placeholder token, so ":P0" and ",-8" are both compared and neither is
            // silently dropped. Keyed by index rather than positional, because reordering is allowed.
            var expected = Specifiers(source.Message);
            var actual   = Specifiers(t.Message);

            foreach (var (index, token) in expected)
                if (actual.TryGetValue(index, out var got) && got != token)
                    problems.Add($"  {key}: en has \"{token}\", {lang} has \"{got}\"");
        }

        Assert.True(problems.Count == 0,
            $"[{lang}] placeholder format mismatch:\n" + string.Join("\n", problems));
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
            if (mr.DeclaringType.FullName != "Proteus.ProteusLoc" || mr.Name != "Localize") continue;

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
            "ProteusLoc.Localize called with a non-literal key or fallback. Use string.Format on a literal " +
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
            $"{stale.Count} key(s) in en.json are no longer used by any ProteusLoc.Localize call. Remove them " +
            $"from all 8 locale files:\n  " + string.Join("\n  ", stale));

        foreach (var (key, fallbackText) in found)
            Assert.True(en[key].Message == fallbackText,
                $"en.json disagrees with the fallback compiled into the code for \"{key}\".\n" +
                $"  code: {fallbackText}\n  json: {en[key].Message}");
    }

    /// <summary>
    /// CheapLoc's two-argument <c>Loc.Localize</c> finds the plugin's table by <c>Assembly.GetCallingAssembly()</c>:
    /// once the JIT inlined a lambda that called it into <c>System.Linq</c>, the calling assembly was Linq's and players
    /// read <c>"#HairPort.Refused.NotInMod"</c> while every test passed. Every lookup goes through
    /// <see cref="Proteus.ProteusLoc"/>, which passes the plugin's assembly; nothing else may call CheapLoc's.
    /// </summary>
    [Fact]
    public void OnlyProteusLocCallsCheapLoc()
    {
        using var asm = AssemblyDefinition.ReadAssembly(ProteusAssembly.Location);
        var direct = new List<string>();
        int wrapped = 0;
        foreach (var type in asm.MainModule.GetTypes())
        foreach (var method in type.Methods.Where(m => m.HasBody))
        foreach (var ins in method.Body.Instructions)
        {
            if (ins.OpCode != OpCodes.Call || ins.Operand is not MethodReference mr || mr.Name != "Localize") continue;
            if (mr.DeclaringType.FullName == "Proteus.ProteusLoc") wrapped++;
            else if (mr.DeclaringType.FullName == "CheapLoc.Loc" && type.FullName != "Proteus.ProteusLoc")
                direct.Add($"  {type.FullName}.{method.Name}");
        }
        Assert.True(direct.Count == 0,
            "CheapLoc.Loc.Localize called directly; call ProteusLoc.Localize instead:\n" + string.Join("\n", direct.Distinct()));
        Assert.True(wrapped > 1000, $"only {wrapped} calls to ProteusLoc.Localize were found: the scan no longer sees them");
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

    /// <summary>What hair porting says, translated rather than copied — and every key the code asks for is there.</summary>
    [Theory]
    [MemberData(nameof(Translations))]
    [Trait("Plan", "AC-hair-race-port-9")]
    public void HairPort_strings_are_translated_not_copied(string lang)
    {
        var en = Load(Source);
        var tr = Load(lang);
        var keys = en.Keys.Where(k => k.StartsWith("HairPort.", StringComparison.Ordinal)).Append("Chat.HairPort")
                     .Append("Chat.HairPortScan.Fmt").Append("Settings.Section.HairPort").ToList();
        Assert.True(keys.Count > 40, $"only {keys.Count} hair port keys in {Source}.json");
        foreach (string key in keys)
        {
            Assert.True(tr.ContainsKey(key), $"[{lang}] has no {key}");
            Assert.NotEqual(en[key].Message, tr[key].Message);
        }
    }

    /// <summary>What a body per part in Settings > Body refit added is translated, not copied; the strings it replaced
    /// are gone.</summary>
    [Theory]
    [MemberData(nameof(Translations))]
    [Trait("Plan", "AC-auto-refit-part-bodies-10")]
    public void PartBodies_strings_are_translated_not_copied(string lang)
    {
        var en = Load(Source);
        var tr = Load(lang);
        foreach (string key in new[]
                 {
                     "AutoRefit.Part", "AutoRefit.Size", "AutoRefit.PartsHint", "AutoRefit.Matched.Fmt",
                     "AutoRefit.MatchedWorn.Fmt", "AutoRefit.Match", "AutoRefit.SizesPending", "AutoRefit.Prefilled.Fmt",
                     "Chat.AutoRefit.MixedKept.Fmt", "Chat.AutoRefit.PartBodyMissing.Fmt",
                 })
        {
            Assert.True(tr.ContainsKey(key), $"[{lang}] has no {key}");
            Assert.NotEqual(en[key].Message, tr[key].Message);
        }
        foreach (string gone in new[] { "AutoRefit.ChestSize", "AutoRefit.LegsSize", "AutoRefit.HandsFeet.Fmt" })
            Assert.False(tr.ContainsKey(gone), $"[{lang}] still has {gone}");
    }

    /// <summary>
    /// What "Refit for" says is translated, not copied — every one of its keys, the refusals the bake for a picked race
    /// gives among them — and the README names it in that language's own words.
    /// </summary>
    [Theory]
    [MemberData(nameof(Translations))]
    [Trait("Plan", "AC-refit-other-race-1")]
    [Trait("Plan", "AC-refit-other-race-9")]
    [Trait("Plan", "AC-refit-race-down-9")]
    [Trait("Plan", "AC-vanilla-cross-sex-legs-7")]
    public void RefitOtherRace_strings_are_translated_not_copied(string lang) // [AC-vanilla-cross-sex-legs-7]
    {
        var en = Load(Source);
        var tr = Load(lang);
        var keys = en.Keys.Where(k => k.StartsWith("Parts.Retarget.Race.", StringComparison.Ordinal)).ToList();
        // 23, cross-sex-refit's five (OtherSex, CrossSexNote, CrossSexWears, CrossSexQuality, CrossSexHint),
        // cross-sex-tops-yas's four (CrossSexFolded, BonesMoved, NeedsSkeleton, RenameFailed) and
        // vanilla-cross-sex-legs's two (ReplacesGame, CharacterKeepsGame)
        Assert.Equal(34, keys.Count);
        foreach (string key in keys)
        {
            Assert.True(tr.ContainsKey(key), $"[{lang}] has no {key}");
            Assert.NotEqual(en[key].Message, tr[key].Message);
        }
    }

    [Theory]
    [MemberData(nameof(AllLocales))]
    [Trait("Plan", "AC-refit-other-race-1")]
    public void Readme_names_refit_for(string lang)
    {
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "proteus.slnx"))) dir = dir.Parent;
        Assert.True(dir != null, $"Could not find proteus.slnx above {AppContext.BaseDirectory}.");
        string readme = System.IO.File.ReadAllText(System.IO.Path.Combine(
            dir!.FullName, lang == Source ? "README.md" : System.IO.Path.Combine("docs", $"README.{lang}.md")));
        string label = Load(lang)["Parts.Retarget.Race.Label"].Message;
        string bullet = readme.Split('\n').Single(l => l.StartsWith("- **" + label + "**", StringComparison.Ordinal));
        Assert.Contains("M Highlander", bullet);
    }

    /// <summary>
    /// The README's Refit for bullet says it goes the other way too — a Highlander-only model made a Midlander's — in
    /// the race names the plugin itself shows; and the note saying a lower race's model is made is translated.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllLocales))]
    [Trait("Plan", "AC-refit-race-down-9")]
    public void Readme_says_refit_for_goes_down_too(string lang)
    {
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "proteus.slnx"))) dir = dir.Parent;
        Assert.True(dir != null, $"Could not find proteus.slnx above {AppContext.BaseDirectory}.");
        string readme = System.IO.File.ReadAllText(System.IO.Path.Combine(
            dir!.FullName, lang == Source ? "README.md" : System.IO.Path.Combine("docs", $"README.{lang}.md")));
        var loc = Load(lang);
        string bullet = readme.Split('\n').Single(l => l.StartsWith("- **" + loc["Parts.Retarget.Race.Label"].Message + "**",
                                                                    StringComparison.Ordinal));
        string mid = loc["Common.Race.Midlander"].Message, high = loc["Common.Race.Highlander"].Message;
        Assert.Contains(mid, bullet);
        Assert.Contains(high, bullet);
        Assert.True(bullet.IndexOf(high, StringComparison.Ordinal) < bullet.LastIndexOf(mid, StringComparison.Ordinal),
                    $"[{lang}] the bullet does not name {high} before {mid}");

        if (lang != Source)
            Assert.NotEqual(Load(Source)["Parts.Retarget.Lowered.Fmt"].Message, loc["Parts.Retarget.Lowered.Fmt"].Message);
    }

    /// <summary>The README's Body refit section says a body and a size are picked per part, in the words Settings uses.</summary>
    [Theory]
    [MemberData(nameof(AllLocales))]
    [Trait("Plan", "AC-auto-refit-part-bodies-10")]
    public void Readme_names_body_and_size_per_part(string lang)
    {
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "proteus.slnx"))) dir = dir.Parent;
        Assert.True(dir != null, $"Could not find proteus.slnx above {AppContext.BaseDirectory}.");
        string readme = System.IO.File.ReadAllText(System.IO.Path.Combine(
            dir!.FullName, lang == Source ? "README.md" : System.IO.Path.Combine("docs", $"README.{lang}.md")));
        var strings = Load(lang);
        string step = readme.Split('\n').Single(l => l.StartsWith("3. ", StringComparison.Ordinal)
                                                     && l.Contains("**" + strings["AutoRefit.Size"].Message + "**",
                                                                   StringComparison.Ordinal));
        Assert.Contains("**" + strings["AutoRefit.Body"].Message + "**", step);
        Assert.Contains("Rue", step);
    }

    private static readonly string[] MoveMirrorKeys =
    [
        "Parts.Mirror.Tip", "Parts.Move.Mirror.Paired.Fmt", "Parts.Move.Mirror.Held.Fmt",
        "Parts.Move.Mirror.None", "Parts.Move.Mirror.Centred",
    ];

    /// <summary>The mirror's tooltip was re-keyed when it came to Move, Rotate and Scale: the old key is gone from every
    /// language, and the new strings are each language's own words.</summary>
    [Theory]
    [MemberData(nameof(Translations))]
    [Trait("Plan", "AC-mirror-move-tools-9")]
    [Trait("Plan", "AC-mirror-move-tools-2")]
    public void MoveMirror_strings_are_translated_not_copied(string lang)
    {
        var en = Load(Source);
        var tr = Load(lang);
        foreach (string key in MoveMirrorKeys)
        {
            Assert.True(tr.ContainsKey(key), $"[{lang}] has no {key}");
            Assert.NotEqual(en[key].Message, tr[key].Message);
        }
        Assert.False(tr.ContainsKey("Parts.Brush.Mirror.Tip"), $"[{lang}] still has the old mirror tooltip");
        Assert.False(en.ContainsKey("Parts.Brush.Mirror.Tip"), "en still has the old mirror tooltip");
    }

    /// <summary>The mirror's tooltip names the three move tools as that language's tool buttons call them.</summary>
    [Theory]
    [MemberData(nameof(AllLocales))]
    [Trait("Plan", "AC-mirror-move-tools-2")]
    public void Mirror_tip_names_the_move_tools(string lang)
    {
        var strings = Load(lang);
        string tip = strings["Parts.Mirror.Tip"].Message;
        foreach (string tool in new[] { "Parts.Tool.Move", "Parts.Tool.Rotate", "Parts.Tool.Scale" })
            Assert.Contains(strings[tool].Message, tip);
    }

    /// <summary>Every README's Move, Rotate and Scale section has a Mirror bullet that says one undo takes back both
    /// sides, in the words the buttons use.</summary>
    [Theory]
    [MemberData(nameof(AllLocales))]
    [Trait("Plan", "AC-mirror-move-tools-9")]
    public void Readme_says_move_tools_mirror(string lang)
    {
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "proteus.slnx"))) dir = dir.Parent;
        Assert.True(dir != null, $"Could not find proteus.slnx above {AppContext.BaseDirectory}.");
        string readme = System.IO.File.ReadAllText(System.IO.Path.Combine(
            dir!.FullName, lang == Source ? "README.md" : System.IO.Path.Combine("docs", $"README.{lang}.md")));
        var strings = Load(lang);
        string label = "- **" + strings["Parts.Brush.Mirror"].Message + "**";
        string undo = "**" + strings["Parts.Move.Undo"].Message + "**";
        var bullets = readme.Split('\n').Where(l => l.StartsWith(label, StringComparison.Ordinal)).ToList();
        Assert.Equal(2, bullets.Count);                       // the brushes', and the move tools'
        Assert.Contains(bullets, b => b.Contains(undo, StringComparison.Ordinal));
    }

    /// <summary>The "Overlay on men" switch and the waiting message are in every language, in its own words.</summary>
    [Theory]
    [MemberData(nameof(Translations))]
    [Trait("Plan", "AC-womens-overlays-men-4")]
    [Trait("Plan", "AC-womens-overlays-men-5")]
    public void MenOverlay_strings_are_translated_not_copied(string lang) // [AC-womens-overlays-men-4] [AC-womens-overlays-men-5]
    {
        var en = Load(Source);
        var tr = Load(lang);
        foreach (string key in new[] { "Colors.Bodies.Men.Label", "Colors.Bodies.Men.Tip", "Mods.Inert.AwaitingMenMaps" })
        {
            Assert.True(tr.ContainsKey(key), $"[{lang}] has no {key}");
            Assert.NotEqual(en[key].Message, tr[key].Message);
            Assert.Equal(en[key].Message.Count(c => c == '\n'), tr[key].Message.Count(c => c == '\n'));
        }
        Assert.Contains("TBSE", tr["Colors.Bodies.Men.Tip"].Message);
    }

    /// <summary>Every README's Advanced table has an "Overlay on men" row, named as the checkbox is in its language.</summary>
    [Theory]
    [MemberData(nameof(AllLocales))]
    [Trait("Plan", "AC-womens-overlays-men-5")]
    public void Readme_has_overlay_on_men_row(string lang) // [AC-womens-overlays-men-5]
    {
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "proteus.slnx"))) dir = dir.Parent;
        Assert.True(dir != null, $"Could not find proteus.slnx above {AppContext.BaseDirectory}.");
        string readme = System.IO.File.ReadAllText(System.IO.Path.Combine(
            dir!.FullName, lang == Source ? "README.md" : System.IO.Path.Combine("docs", $"README.{lang}.md")));
        string label = Load(lang)["Colors.Bodies.Men.Label"].Message;
        Assert.Contains(readme.Split('\n'), l => l.StartsWith("| " + label + " |", StringComparison.Ordinal) && l.Contains("TBSE"));
    }

    /// <summary>The cross-sex refit's strings are in every language, in its own words, with the same placeholders.</summary>
    [Theory]
    [MemberData(nameof(Translations))]
    [Trait("Plan", "AC-cross-sex-refit-9")]
    [Trait("Plan", "AC-cross-sex-tops-yas-10")]
    [Trait("Plan", "AC-vanilla-cross-sex-legs-7")]
    public void Cross_sex_strings_exist_and_are_translated_in_every_locale(string lang) // [AC-cross-sex-refit-9] [AC-cross-sex-tops-yas-10] [AC-vanilla-cross-sex-legs-7]
    {
        var en = Load(Source);
        var tr = Load(lang);
        // Nothing is refused for folds any more (cross-sex-tops-yas): the strings that said so are gone.
        Assert.False(tr.ContainsKey("Parts.Retarget.Refuse.CrossSexQuality.Fmt"));
        Assert.False(tr.ContainsKey("HairPort.Refused.Folded.Fmt"));
        foreach (string key in new[]
                 {
                     "Parts.Retarget.Race.OtherSex.Fmt", "Parts.Retarget.Race.CrossSexNote.Fmt",
                     "Parts.Retarget.Race.CrossSexWears.Fmt", "Parts.Retarget.Race.CrossSexQuality.Fmt",
                     "Parts.Retarget.Race.CrossSexHint.Fmt", "Parts.Retarget.Refuse.CrossSexMadeFor.Fmt",
                     "Parts.Retarget.Refuse.CrossSexCoverage.Fmt", "Parts.Retarget.Refuse.CrossSexLayout.Fmt",
                     "Parts.Retarget.Refuse.MenMapsMissing", "Parts.Retarget.Race.Tip", "Parts.Retarget.NoBodiesForSex.Fmt",
                     "Parts.Retarget.Race.CrossSexFolded.Fmt", "Parts.Retarget.Race.BonesMoved.Fmt",
                     "Parts.Retarget.Race.NeedsSkeleton.Fmt", "Parts.Retarget.Race.RenameFailed.Fmt",
                     "HairPort.Folded.Fmt", "HairPort.Scan.State.FittedWarn.Fmt",
                     "Parts.Retarget.Race.ReplacesGame.Fmt", "Parts.Retarget.Race.CharacterKeepsGame.Fmt",
                 })
        {
            Assert.True(tr.ContainsKey(key), $"[{lang}] has no {key}");
            Assert.NotEqual(en[key].Message, tr[key].Message);
            for (int i = 0; i < 4; i++)
                Assert.Equal(en[key].Message.Contains("{" + i + "}"), tr[key].Message.Contains("{" + i + "}"));
        }
        // The hint and refusal name the dropdowns as this language labels them.
        Assert.Contains(tr["Parts.Retarget.Race.Label"].Message, tr["Parts.Retarget.Race.CrossSexHint.Fmt"].Message);
        Assert.Contains(tr["Parts.Retarget.FromBody"].Message, tr["Parts.Retarget.Refuse.CrossSexMadeFor.Fmt"].Message);
    }

    /// <summary>Every README describes the refit for the other sex under its own "Refit for", and none still says it never happens.</summary>
    [Theory]
    [MemberData(nameof(AllLocales))]
    [Trait("Plan", "AC-cross-sex-refit-9")]
    [Trait("Plan", "AC-cross-sex-tops-yas-10")]
    public void Readme_describes_cross_sex_refit(string lang) // [AC-cross-sex-refit-9] [AC-cross-sex-tops-yas-10]
    {
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "proteus.slnx"))) dir = dir.Parent;
        Assert.True(dir != null, $"Could not find proteus.slnx above {AppContext.BaseDirectory}.");
        string readme = System.IO.File.ReadAllText(System.IO.Path.Combine(
            dir!.FullName, lang == Source ? "README.md" : System.IO.Path.Combine("docs", $"README.{lang}.md")));
        var strings = Load(lang);
        string label = "**" + strings["Parts.Retarget.Race.Label"].Message + "**";
        var lines = readme.Split('\n');
        Assert.Contains(lines, l => l.StartsWith("- ", StringComparison.Ordinal) && l.Contains(label) && l.Contains("bibo↔TBSE"));
        // A garment rigged to YAS is refitted for men, its missing bones moved (cross-sex-tops-yas), under its own bullet.
        Assert.Contains(lines, l => l.StartsWith("- **", StringComparison.Ordinal) && l.Contains(label) && l.Contains("YAS")
                                    && l.Contains("IVCS") && !l.Contains("bibo↔TBSE"));
        Assert.DoesNotContain("never refitted onto each other", readme);
        if (lang == Source)
        {
            Assert.DoesNotContain("aren't offered for men", readme);
            Assert.DoesNotContain("refused for men", readme);
            Assert.DoesNotContain("too many folded faces", readme);
            Assert.Contains("never refused for folded faces", readme);
            Assert.Contains("partly inside out is still fitted", readme);
        }
    }

    /// <summary>The game's own model replaced, and the character who keeps it: one race, the option named as written.</summary>
    [Theory]
    [MemberData(nameof(AllLocales))]
    [Trait("Plan", "AC-vanilla-cross-sex-legs-5")]
    public void ReplacesGame_and_CharacterKeepsGame_format_with_a_race(string lang) // [AC-vanilla-cross-sex-legs-5]
    {
        var strings = Load(lang);
        foreach (string key in new[] { "Parts.Retarget.Race.ReplacesGame.Fmt", "Parts.Retarget.Race.CharacterKeepsGame.Fmt" })
        {
            string message = strings[key].Message;
            Assert.Contains("{0}", message);
            Assert.DoesNotContain("{1}", message);
            Assert.Contains("Midlander M", string.Format(message, "Midlander M"));
        }
        Assert.Contains("Original", strings["Parts.Retarget.Race.ReplacesGame.Fmt"].Message);
        if (lang == Source)
        {
            Assert.Equal("The game has its own {0} model of this item. Once saved, this refit replaces it for everyone you see "
                         + "wearing the game's {0} model while the new mod is on. Choose “Original” in that mod, or turn it off, "
                         + "to bring the game's back.", strings["Parts.Retarget.Race.ReplacesGame.Fmt"].Message);
            Assert.Equal("Your character is {0}, and the game has a {0} model of this item of its own: this refit won't show "
                         + "on your character.", strings["Parts.Retarget.Race.CharacterKeepsGame.Fmt"].Message);
        }
    }

    /// <summary>Every README's cross-sex bullet says the game's own women's legs are refitted for men from its body.</summary>
    [Theory]
    [MemberData(nameof(AllLocales))]
    [Trait("Plan", "AC-vanilla-cross-sex-legs-7")]
    public void Readme_says_game_womens_legs_refit_for_men(string lang) // [AC-vanilla-cross-sex-legs-7]
    {
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "proteus.slnx"))) dir = dir.Parent;
        Assert.True(dir != null, $"Could not find proteus.slnx above {AppContext.BaseDirectory}.");
        string readme = System.IO.File.ReadAllText(System.IO.Path.Combine(
            dir!.FullName, lang == Source ? "README.md" : System.IO.Path.Combine("docs", $"README.{lang}.md")));
        var strings = Load(lang);
        string label = "**" + strings["Parts.Retarget.Race.Label"].Message + "**";
        string vanilla = "**" + strings["Parts.Retarget.FromVanilla"].Message + "**";
        string bullet = readme.Split('\n').Single(l => l.StartsWith("- ", StringComparison.Ordinal) && l.Contains(label)
                                                       && l.Contains("bibo↔TBSE"));
        Assert.Contains(vanilla, bullet);
        if (lang == Source) Assert.Contains("women's legs only", bullet);
    }

    /// <summary>The size set's men's versions speak every language, in its own words, with the same placeholders.</summary>
    [Theory]
    [MemberData(nameof(Translations))]
    [Trait("Plan", "AC-sizeset-cross-sex-9")]
    public void Size_set_men_strings_exist_and_are_translated_in_every_locale(string lang) // [AC-sizeset-cross-sex-9]
    {
        var en = Load(Source);
        var tr = Load(lang);
        foreach (string key in new[]
                 {
                     "SizeSet.ForMen.Fmt", "SizeSet.ForMen.Tip.Fmt", "SizeSet.MensGroup", "SizeSet.CheckInGame",
                     "SizeSet.MenMapsMissing", "SizeSet.NoSourceForMen.Fmt", "SizeSet.MensGroupTaken.Fmt",
                     "SizeSet.EndsNotForMen", "SizeSet.OneVariant.Fmt", "SizeSet.NotMade", "SizeSet.RigUnreadable", "SizeSet.Intro",
                 })
        {
            Assert.True(tr.ContainsKey(key), $"[{lang}] has no {key}");
            Assert.NotEqual(en[key].Message, tr[key].Message);
            for (int i = 0; i < 4; i++)
                Assert.Equal(en[key].Message.Contains("{" + i + "}"), tr[key].Message.Contains("{" + i + "}"));
        }
        // Placeholders alone, which may read alike in two languages.
        Assert.True(tr.ContainsKey("SizeSet.Warning.Fmt"));
        foreach (string p in new[] { "{0}", "{1}", "{2}" }) Assert.Contains(p, tr["SizeSet.Warning.Fmt"].Message);
        Assert.Contains("TBSE-X", tr["SizeSet.Intro"].Message);
    }

    /// <summary>Every README describes the size set's men's versions, by the labels its language shows, and none still
    /// says a women's mod lists no men's bodies or that the size set never crosses the sexes.</summary>
    [Theory]
    [MemberData(nameof(AllLocales))]
    [Trait("Plan", "AC-sizeset-cross-sex-9")]
    public void Readme_describes_mens_versions_in_the_size_set(string lang) // [AC-sizeset-cross-sex-9]
    {
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "proteus.slnx"))) dir = dir.Parent;
        Assert.True(dir != null, $"Could not find proteus.slnx above {AppContext.BaseDirectory}.");
        string readme = System.IO.File.ReadAllText(System.IO.Path.Combine(
            dir!.FullName, lang == Source ? "README.md" : System.IO.Path.Combine("docs", $"README.{lang}.md")));
        var strings = Load(lang);
        string forMen = "**" + string.Format(strings["SizeSet.ForMen.Fmt"].Message, "TBSE") + "**";
        string group = "**" + strings["SizeSet.MensGroup"].Message + "**";
        string check = "**" + strings["SizeSet.CheckInGame"].Message.TrimEnd(':', '：', ' ') + "**";
        Assert.Contains(readme.Split('\n'), l => l.StartsWith("- ", StringComparison.Ordinal) && l.Contains(forMen)
                                                 && l.Contains(group) && l.Contains(check) && l.Contains("bibo↔TBSE"));
        if (lang == Source)
        {
            Assert.DoesNotContain("a women's mod doesn't list men's bodies", readme);
            Assert.DoesNotContain("the size set never cross the sexes", readme);
        }
    }

    /// <summary>The game's own gear is copied by its first change, not by opening it: every language says so in its
    /// own words, and nothing is left of the strings for copying on open.</summary>
    [Theory]
    [MemberData(nameof(Translations))]
    [Trait("Plan", "AC-vanilla-copy-on-edit-10")]
    public void VanillaCopy_strings_are_translated_not_copied(string lang)
    {
        var en = Load(Source);
        var tr = Load(lang);
        foreach (var key in new[]
                 {
                     "Parts.Vanilla.Worn.Tip", "Parts.Vanilla.CopyNote.Fmt", "Parts.Vanilla.Copied.Fmt",
                     "Parts.Vanilla.EditedCopyThere.Fmt", "Parts.Vanilla.ModSwitchedOff.Fmt",
                     "Parts.Vanilla.CopyRefused.Fmt", "Parts.Vanilla.NotRefittable", "Parts.Vanilla.CopyRegistering",
                 })
        {
            Assert.True(tr.ContainsKey(key), $"{lang} lacks {key}");
            Assert.NotEqual(en[key].Message, tr[key].Message);
        }
        foreach (var gone in new[] { "Parts.Vanilla.Editing.Fmt", "Parts.Vanilla.EditTool.Tip" })
        {
            Assert.False(en.ContainsKey(gone), $"en still has {gone}");
            Assert.False(tr.ContainsKey(gone), $"{lang} still has {gone}");
        }
        Assert.DoesNotContain("Opening it copies it", en["Parts.Vanilla.Worn.Tip"].Message);
    }

    /// <summary>Each README's vanilla-gear paragraph says it opens as it is and is copied by the first change, into a mod
    /// named "… — Proteus edit"; none still says opening it copies it.</summary>
    [Theory]
    [MemberData(nameof(AllLocales))]
    [Trait("Plan", "AC-vanilla-copy-on-edit-10")]
    public void Readme_says_vanilla_gear_is_copied_by_its_first_change(string lang)
    {
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "proteus.slnx"))) dir = dir.Parent;
        Assert.True(dir != null, $"Could not find proteus.slnx above {AppContext.BaseDirectory}.");
        string readme = System.IO.File.ReadAllText(System.IO.Path.Combine(
            dir!.FullName, lang == Source ? "README.md" : System.IO.Path.Combine("docs", $"README.{lang}.md")));
        string paragraph = readme.Split('\n').Single(l => l.Contains(ReadmeVanillaOpensAsIs[lang].Now));
        Assert.Contains("— Proteus edit*", paragraph);
        Assert.DoesNotContain(ReadmeVanillaOpensAsIs[lang].Old, readme);
    }

    /// <summary>What each README's vanilla-gear paragraph says now, and the phrase it used for copying on open.</summary>
    private static readonly Dictionary<string, (string Now, string Old)> ReadmeVanillaOpensAsIs = new()
    {
        ["en"] = ("nothing is saved until you change it", "so opening it copies it into a new mod"),
        ["de"] = ("nichts wird gespeichert, bis du etwas änderst", "also wird sie beim Öffnen in einen neuen Mod kopiert"),
        ["es"] = ("no se guarda nada hasta que lo cambias", "así que al abrirlo se copia en un mod nuevo"),
        ["fr"] = ("rien n'est enregistré tant que vous ne le modifiez pas", "donc l'ouvrir le copie dans un nouveau mod"),
        ["ja"] = ("変更するまで何も保存されません", "開くと新しいMODにコピーされます"),
        ["ko"] = ("바꾸기 전까지는 아무것도 저장되지 않습니다", "열면 새 모드로 복사됩니다"),
        ["ru"] = ("ничего не сохраняется, пока вы его не измените", "поэтому при открытии оно копируется в новый мод"),
        ["zh"] = ("在你改动之前不会保存任何内容", "因此打开它会把它复制进一个新模组"),
    };
}
