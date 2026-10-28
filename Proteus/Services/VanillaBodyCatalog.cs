using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using CheapLoc;

namespace Proteus.Services;

/// <summary>
/// The game's own body, as a <see cref="BodySizeCatalog"/> the refit cannot tell from a body mod's.
/// <para/>
/// Gear the game ships is fitted to this body, so it is the "made for" side of every refit of a vanilla item — the
/// half that has no mod folder to be read out of. Exactly one size per slot: the game ships one body per race, which
/// is the whole reason its gear needs refitting at all.
/// <para/>
/// The models are EXTRACTED to a folder rather than handed about as bytes because everything downstream speaks file
/// paths — <see cref="BodySizeMatch"/>'s cache is keyed by path, and the detect, validate and plan passes each open
/// their own copy on a worker thread. A path is the smaller thing to provide, and it keeps the game's data reader on
/// the one thread that may touch it.
/// </summary>
internal static class VanillaBodyCatalog
{
    /// <summary>
    /// What stands where a body mod's folder name goes. Two colons cannot begin a Windows folder name, so this can
    /// never collide with a real mod directory, and code that looks a mod up by it fails loudly rather than reading
    /// some other mod.
    /// </summary>
    internal const string Key = "::vanilla";

    /// <summary>The group every option is filed under, so the picker can say where these came from.</summary>
    internal static string GroupName => ProteusLoc.Localize("Parts.Retarget.Size.VanillaGroup", "Vanilla");

    /// <summary>
    /// Where the extracts live. Deliberately NOT under Penumbra's mods root: these stand for what the game draws with
    /// nothing on top, and a folder there could be redirected, indexed, or picked up as a mod.
    /// </summary>
    /// <remarks>Settable for the tests alone, which keep their own: the game's plugin clears this folder as it loads, and
    /// a test run beside a running game would find its extracts gone halfway.</remarks>
    internal static string Dir { get; set; } = Path.Combine(Path.GetTempPath(), "proteus-vanilla-body");

    private static readonly object Gate = new();

    /// <summary>
    /// Throw last session's extracts away. Called once at start-up.
    /// <para/>
    /// This IS the invalidation: a game patch cannot happen while the plugin is loaded, so extracting once per session
    /// is exactly right, and needs no version key that could be got wrong. A folder left behind by a crash is
    /// overwritten rather than trusted.
    /// </summary>
    internal static void CleanUp()
    {
        lock (Gate)
        {
            extracted.Clear();
            emptyFor.Clear();
            models.Clear();
            try
            {
                if (Directory.Exists(Dir)) Directory.Delete(Dir, recursive: true);
            }
            catch (IOException)
            {
                // Someone has a file open. The extract below overwrites what it needs anyway.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>The bytes of every model extracted this session, by slot: what <see cref="IsGameModel"/> knows the game's
    /// body by.</summary>
    private static readonly List<(string Slot, byte[] Bytes)> models = [];

    /// <summary>
    /// Whether <paramref name="bytes"/> is, byte for byte, a model of the game's own body for <paramref name="slot"/>
    /// extracted this session. By content, not by path: a body mod's file can sit anywhere, and only the game's chest
    /// has no skin under its bra (see <c>BodyRetarget.SmallclothesOverHoles</c>).
    /// </summary>
    internal static bool IsGameModel(byte[] bytes, string slot)
    {
        lock (Gate)
            foreach (var (s, b) in models)
                if (s == slot && b.AsSpan().SequenceEqual(bytes)) return true;
        return false;
    }

    /// <summary>How many distinct models of the game's body for <paramref name="slot"/> were extracted this session — for
    /// the tests.</summary>
    internal static int GameModelCount(string slot)
    {
        lock (Gate) return models.Count(m => m.Slot == slot);
    }

    /// <summary>
    /// Race code to the slots already written out this session, so the game is read once per file. Only bodies are
    /// kept here: a body, once read, is the game's for whoever asks, since every reader of the game reads the same bytes.
    /// </summary>
    private static readonly Dictionary<string, List<BodyOption>> extracted = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Race code to the readers that found no body for it this session, each of which is not asked again. "No body" is
    /// believed only of the reader that said so: a test's stub that reads nothing must not hide the body from the game's
    /// own reader asking after it in the same process. A reader is its target and method, as <see cref="Delegate.Equals"/>
    /// compares them (none is multicast), and the target is held weakly, so a reader's game data is not kept alive here.
    /// </summary>
    private static readonly Dictionary<string, List<(WeakReference<object>? Target, MethodInfo Method)>> emptyFor =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether <paramref name="read"/> is one of <paramref name="readers"/>, forgetting those whose target is gone. A static
    /// reader has no target to lose and is kept.
    /// </summary>
    private static bool SaidNoBody(List<(WeakReference<object>? Target, MethodInfo Method)> readers, Func<string, byte[]?> read)
    {
        bool found = false;
        for (int i = readers.Count - 1; i >= 0; i--)
        {
            var (target, method) = readers[i];
            object? alive = null;
            if (target is not null && !target.TryGetTarget(out alive))
            {
                readers.RemoveAt(i);
                continue;
            }
            if (method == read.Method && ReferenceEquals(alive, read.Target)) found = true;
        }
        return found;
    }

    /// <summary>
    /// The game's body for one race code (<c>"0201"</c>), extracting it on first use.
    /// </summary>
    /// <param name="race">The body's race code, as <see cref="BodySizeCatalog.RaceOf"/> reads it off a game path.</param>
    /// <param name="read">
    /// Reads a game path out of the game's own data — <c>TextureLoader.LoadRawFile(null, path)</c>. Pass one long-lived
    /// reader, a field or a method group over a long-lived object: a wrapper whose closure is new on every call is a new
    /// reader every time, and for a race with no body of its own it reads the game again on every call — every frame,
    /// for the Studio's checks. A reader is known by its target object and method: a closure that later reads other data
    /// is still the same reader, and a method group over a struct, boxed afresh, is a new one each time.
    /// </param>
    /// <param name="drawn">
    /// The body the game DRAWS for the race, following its fallback chain when it ships none of its own (see
    /// <see cref="Chain"/>) — what a "made for" list wants. Off, only the race's own body: what the racial route and the
    /// automatic refit ask, since "this race has no body" is how they know to bend a racial piece through a shared one.
    /// </param>
    /// <returns>
    /// A catalog over the extract folder, empty when the game has no body for that race. An empty one answers
    /// <see cref="BodySizeCatalog.IsBody"/> false, which is what the picker checks. A body any reader extracted is
    /// returned to every reader; "no body" only to the readers that found none.
    /// </returns>
    internal static BodySizeCatalog Read(string race, Func<string, byte[]?> read, bool drawn = false)
    {
        lock (Gate)
        {
            string key = drawn ? race + "~drawn" : race;
            if (extracted.TryGetValue(key, out var options)) return new BodySizeCatalog(Dir, options);
            if (emptyFor.TryGetValue(key, out var readers) && SaidNoBody(readers, read)) return new BodySizeCatalog(Dir, []);

            options = Extract(race, read, drawn);
            if (options.Count > 0)
            {
                extracted[key] = options;
                emptyFor.Remove(key);
            }
            else
            {
                if (readers is null) emptyFor[key] = readers = [];
                readers.Add((read.Target is { } target ? new WeakReference<object>(target) : null, read.Method));
            }
            return new BodySizeCatalog(Dir, options);
        }
    }

    /// <summary>The game path of one slot of one race's body — the same path a body mod replaces.</summary>
    internal static string GamePathOf(string race, string slot)
        => $"chara/equipment/e0000/model/c{race}e0000{slot}.mdl";

    private static List<BodyOption> Extract(string race, Func<string, byte[]?> read, bool drawn)
    {
        var options = new List<BodyOption>();
        foreach (string slot in Slots)
        {
            // Filed under the race asked for whatever model it came from: that is the body the game draws for it, and a
            // fallback across sexes (a Lalafell woman's hands are the man's) must still pass the picker's race rule. The
            // file name can be shared by both modes: where the race has its own model, the chain's first answer is it.
            string gamePath = GamePathOf(race, slot);
            if (FirstInChain(drawn ? Chain(race) : [race], slot, read) is not { } bytes) continue;

            string rel = $"c{race}e0000{slot}.mdl";
            string full = Path.Combine(Dir, rel);
            try
            {
                Directory.CreateDirectory(Dir);
                PenumbraModMeta.AtomicWrite(full, bytes);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            options.Add(new BodyOption(GroupName, Section: "", Name: SizeName, rel, gamePath, slot));
            if (!models.Exists(m => m.Slot == slot && m.Bytes.AsSpan().SequenceEqual(bytes))) models.Add((slot, bytes));
        }
        return options;
    }

    /// <summary>
    /// The model the game draws one slot of a race's body with: its own, else the first race it falls back to that has
    /// one. Most races ship no body of their own — Miqo'te, Au Ra, Elezen, Viera, Hrothgar and the Highlander and
    /// Roegadyn women all draw a Midlander's or a Roegadyn man's — so asking for the race's own file alone left the
    /// "made for" list empty for them.
    /// </summary>
    private static byte[]? FirstInChain(IEnumerable<string> chain, string slot, Func<string, byte[]?> read)
    {
        foreach (string code in chain)
        {
            byte[]? bytes;
            try
            {
                bytes = read(GamePathOf(code, slot));
            }
            catch (Exception)
            {
                // The game's data reader throws for a path it does not have as readily as it returns null.
                continue;
            }
            if (bytes is { Length: > 0 }) return bytes;
        }
        return null;
    }

    /// <summary>
    /// A race and the races its models fall back to, in order, ending at the Midlander man. The game's own table, as
    /// Penumbra.GameData's <c>GenderRace.Fallback</c> has it.
    /// </summary>
    internal static IEnumerable<string> Chain(string race)
    {
        string code = race;
        for (int step = 0; step < 8; step++)
        {
            yield return code;
            if (code == "0101" || !int.TryParse(code, out int n)) yield break;
            code = n switch
            {
                104 or 201 => "0101",
                904 or 1501 => "0901",
                1104 or 1201 or 1204 => "1101",
                1304 or 1404 => "1301",
                _ when n % 10 == 4 => "0104",
                _ when n / 100 % 2 == 0 => "0201",
                _ => "0101",
            };
        }
    }

    /// <summary>What the one size is called. A body mod's sizes are named by its author; vanilla has just the one.</summary>
    private static string SizeName => ProteusLoc.Localize("Parts.Retarget.Size.VanillaBody", "The vanilla body");

    private static readonly string[] Slots = ["_top", "_dwn", "_glv", "_sho"];
}
