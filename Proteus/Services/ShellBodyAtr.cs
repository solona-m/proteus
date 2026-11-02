using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace Proteus.Services;

/// <summary>
/// The second skin's side of a body mod that picks its shapes by option (<see cref="BodyAtrSwitches"/>): NIRÆ ships
/// every chest in one chest model and draws the one its Chest option ticks. A shell cut from that model would lay cloth
/// over all of them, so the attributes the mod's current settings switch off are hidden from the shell, as the game
/// hides them from the body.
/// <para/>
/// Pure: the settings come in through <c>settingsOf</c>, so nothing here talks to Penumbra. One resolver
/// (<see cref="Resolve"/>) serves both the build (<see cref="ForPart"/>) and the change checks (<see cref="Moved"/>),
/// so the two can never judge the same settings differently. Everything that cannot be judged hides nothing: the shell
/// is then exactly what it was before this existed.
/// </summary>
internal static class ShellBodyAtr
{
    /// <summary>A mod's settings in the player's collection, as Penumbra reports them (temporary settings included).</summary>
    internal readonly record struct ModSettings(bool Enabled, IReadOnlyDictionary<string, List<string>> Options);

    /// <summary>
    /// One bare body part of a published shell whose body mod switches shapes by option.
    /// </summary>
    /// <param name="ModDir">The mod's folder name, as Penumbra knows it.</param>
    /// <param name="Part">"top", "dwn", "glv" or "sho".</param>
    /// <param name="Race">The drawn race as Penumbra numbers it, or null when unknown.</param>
    /// <param name="Key">The attributes it switched off, sorted and comma-joined; "" when none; <see cref="NoSay"/>
    /// when the build could not judge it.</param>
    internal sealed record Use(string ModDir, string ModRoot, string Part, ushort? Race, string Key);

    /// <summary>What a part's body mod says the shell must leave out.</summary>
    /// <param name="Off">The attributes to hide; null for none.</param>
    /// <param name="Use">The part to watch for a change; null when its mod switches no shapes (or it is not a bare part).</param>
    /// <param name="Readable">False only when the settings could not be read (or the read threw). Such an answer never
    /// counts as a change: a loading screen must not flip the shell to every chest and back.</param>
    /// <param name="Note">A line for the caller's log, if any.</param>
    /// <param name="Warning">Log <paramref name="Note"/> as a warning.</param>
    internal sealed record PartAtr(IReadOnlySet<string>? Off, Use? Use, bool Readable, string? Note, bool Warning = false);

    /// <summary>The key of a part the build had no say on: everything drawn, as before.</summary>
    internal const string NoSay = "?";

    private static readonly PartAtr NotWatched = new(null, null, true, null);

    /// <summary>
    /// The one resolver: the mod's settings, then <see cref="BodyAtrSwitches.Resolve"/> for the part's slot and race, then
    /// the guard against hiding every shape. Never throws.
    /// </summary>
    internal static PartAtr Resolve(string modRoot, string modDir, string part, ushort? race,
                                    Func<string, ModSettings?> settingsOf)
    {
        try
        {
            ModSettings? settings;
            try { settings = settingsOf(modDir); }
            catch (Exception) { settings = null; }

            Use NoSayUse() => new(modDir, modRoot, part, race, NoSay);
            if (settings is not { } s)
                return new PartAtr(null, NoSayUse(), false,
                    $"second skin part {part}: {modDir}'s settings could not be read — every shape its options switch is covered");
            if (!s.Enabled) return new PartAtr(null, NoSayUse(), true, null);

            var slot = BodyRetarget.ImcSlotName("_" + part);
            var manifest = BodyAtrSwitches.Manifest(modRoot);
            if (slot == null || manifest == null) return new PartAtr(null, NoSayUse(), true, null);

            var off = BodyAtrSwitches.Resolve(new BodyAtrSwitches.Query(manifest, slot, s.Options, race))?.Off;
            if (off is not { Count: > 0 })
                return new PartAtr(null, new Use(modDir, modRoot, part, race, ""), true, null);

            // Every shape of the slot's shape group off is no body anyone draws: the settings read is not what the game
            // draws (a temporary setting locked away from us, say). Cover everything rather than nothing.
            if (BodyAtrSwitches.ShapeGroup(manifest, slot) is { Attributes.Count: > 0 } axis
                && axis.Attributes.All(off.Contains))
                return new PartAtr(null, NoSayUse(), true,
                    $"second skin part {part}: {modDir}'s settings switch off every shape of its {axis.Group} group — "
                    + "not trusting that; every shape is covered", Warning: true);

            return new PartAtr(off, new Use(modDir, modRoot, part, race, Key(off)), true,
                $"second skin part {part}: {modDir} draws one of its shapes by its options — leaving out [{Key(off)}]");
        }
        catch (Exception ex)
        {
            return new PartAtr(null, new Use(modDir, modRoot, part, race, NoSay), false,
                $"second skin part {part}: {modDir}'s shapes could not be judged ({ex.Message}) — every shape covered");
        }
    }

    /// <summary>
    /// What the mod supplying this part says the shell must leave out. Only a bare part (no garment in the slot), whose
    /// file is known and lies inside <paramref name="modsRoot"/>, in a mod that switches shapes, is asked about at all;
    /// for anything else <paramref name="settingsOf"/> is never called. Never throws.
    /// </summary>
    /// <param name="sourceDisk">The mod file the part's model really comes from (never our own republished copy).</param>
    internal static PartAtr ForPart(string part, bool bare, string? sourceDisk, string? modsRoot, ushort? race,
                                    Func<string, ModSettings?> settingsOf)
    {
        try
        {
            if (!bare || string.IsNullOrEmpty(sourceDisk) || string.IsNullOrEmpty(modsRoot)) return NotWatched;
            if (CompositorService.ModFolderOf(sourceDisk, modsRoot) is not { } folder) return NotWatched;
            var modRoot = Path.Combine(modsRoot, folder);
            if (!BodyAtrSwitches.SwitchesShapes(modRoot)) return NotWatched;
            return Resolve(modRoot, folder, part, race, settingsOf);
        }
        catch (Exception)
        {
            return NotWatched;
        }
    }

    /// <summary>The variants the game hides, and the shapes the options switch off: both left out. <paramref name="live"/>
    /// itself when there is nothing to add, so a body without switches builds exactly as before.</summary>
    internal static HashSet<string>? Merge(HashSet<string>? live, IReadOnlySet<string>? off)
    {
        if (off is not { Count: > 0 }) return live;
        var merged = live != null ? new HashSet<string>(live, StringComparer.Ordinal) : new HashSet<string>(StringComparer.Ordinal);
        merged.UnionWith(off);
        return merged;
    }

    /// <summary>The attributes, sorted and comma-joined; "" for none.</summary>
    internal static string Key(IReadOnlySet<string>? off)
        => off is { Count: > 0 } ? string.Join(",", off.Order(StringComparer.Ordinal)) : "";

    /// <summary>A set of uses as one comparable string: what a published shell left out, part by part.</summary>
    internal static string Canonical(IEnumerable<Use> uses)
        => string.Join(";", uses.Select(u => $"{u.ModDir.ToLowerInvariant()}|{u.Part}|{u.Key}").Order(StringComparer.Ordinal));

    /// <summary>Whether a published shell was cut from a body this mod switches. An in-memory scan, safe on any thread.</summary>
    internal static bool Watches(IReadOnlyList<Use> uses, string modDir)
        => uses.Any(u => string.Equals(u.ModDir, modDir, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Do the recorded uses (of <paramref name="modDir"/>, or all of them when null) now resolve to anything else? Each
    /// distinct mod's settings are read once. A part whose settings cannot be read now keeps its recorded key and never
    /// counts. Never throws: a failure is "not moved".
    /// </summary>
    /// <param name="target">The uses with every readable fresh key in place: what a rebuild now would publish.</param>
    internal static bool Moved(IReadOnlyList<Use> uses, string? modDir, Func<string, ModSettings?> settingsOf,
                               out string why, out string target)
    {
        why = "";
        target = "";
        try
        {
            target = Canonical(uses);
            if (uses.Count == 0) return false;

            var memo = new Dictionary<string, ModSettings?>(StringComparer.OrdinalIgnoreCase);
            ModSettings? Memo(string dir)
            {
                if (memo.TryGetValue(dir, out var known)) return known;
                ModSettings? s;
                try { s = settingsOf(dir); }
                catch (Exception) { s = null; }
                return memo[dir] = s;
            }

            bool moved = false;
            var fresh = new List<Use>(uses.Count);
            var reasons = new List<string>();
            foreach (var u in uses)
            {
                if (modDir != null && !string.Equals(u.ModDir, modDir, StringComparison.OrdinalIgnoreCase))
                {
                    fresh.Add(u);
                    continue;
                }
                var now = Resolve(u.ModRoot, u.ModDir, u.Part, u.Race, Memo);
                if (!now.Readable || now.Use == null)
                {
                    reasons.Add($"{u.Part}: settings unreadable now, kept [{u.Key}]");
                    fresh.Add(u);
                    continue;
                }
                if (!string.Equals(now.Use.Key, u.Key, StringComparison.Ordinal))
                {
                    moved = true;
                    reasons.Add($"{u.Part}: [{u.Key}] -> [{now.Use.Key}]");
                }
                fresh.Add(u with { Key = now.Use.Key });
            }
            target = Canonical(fresh);
            why = string.Join("; ", reasons);
            return moved;
        }
        catch (Exception ex)
        {
            why = "the re-check failed: " + ex.Message;
            return false;
        }
    }

    // ── which mod an upstream copy came from, kept beside it across a restart ─────────────────────────────

    private const string SourceSuffix = ".src";

    /// <summary>
    /// Record beside a kept upstream copy which mod file it is, mod-relative. A source unknown or outside the mods root
    /// deletes any record, so it can never name a body the copy no longer is. May throw (I/O); the caller logs.
    /// </summary>
    internal static void WriteSource(string upstreamDisk, string? sourceDisk, string? modsRoot)
    {
        var record = upstreamDisk + SourceSuffix;
        string? rel = null;
        if (!string.IsNullOrEmpty(sourceDisk) && !string.IsNullOrEmpty(modsRoot)
            && CompositorService.ModFolderOf(sourceDisk, modsRoot) != null)
            rel = sourceDisk[modsRoot.Length..].TrimStart('/', '\\').Replace('\\', '/');
        if (string.IsNullOrEmpty(rel))
        {
            if (File.Exists(record)) File.Delete(record);
            return;
        }
        var bytes = Encoding.UTF8.GetBytes(rel);
        if (File.Exists(record) && File.ReadAllBytes(record).AsSpan().SequenceEqual(bytes)) return;
        File.WriteAllBytes(record, bytes);
    }

    /// <summary>
    /// The mod file a kept upstream copy came from, or null: no record, a record leaving the mods root, or a file that is
    /// gone or no longer the copy's length. Never throws.
    /// </summary>
    internal static string? ReadSource(string upstreamDisk, string? modsRoot, long expectedLength)
    {
        try
        {
            if (string.IsNullOrEmpty(modsRoot)) return null;
            var record = upstreamDisk + SourceSuffix;
            if (!File.Exists(record)) return null;
            var rel = File.ReadAllText(record, Encoding.UTF8).Trim();
            if (rel.Length == 0 || Path.IsPathRooted(rel)
                || rel.Split('/', '\\').Any(s => s == "..")) return null;
            var full = Path.Combine(modsRoot, rel.Replace('/', Path.DirectorySeparatorChar));
            var info = new FileInfo(full);
            return info.Exists && info.Length == expectedLength ? full : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ── one forced rebuild per target ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Bounds the forced rebuilds a shape change may cause. A target (a <see cref="Canonical"/> of what a rebuild should
    /// publish) is forced once; until a run started after that publishes, it is not forced again. A publish that draws
    /// something else, while the settings still say the target, means the build and the check disagree: warned once, and
    /// that target is never forced again — until a publish draws it after all. Without this, a redraw, Glamourer
    /// re-asserting its settings, and another forced rebuild could chase each other for ever. Thread-safe.
    /// </summary>
    internal sealed class ForceLatch
    {
        private readonly object gate = new();
        private long stamps;
        private string? target;
        private long armedStamp;
        private bool pending;
        private string? exhausted;

        /// <summary>A fresh, increasing stamp: a composite run takes one as it starts.</summary>
        internal long Stamp() => Interlocked.Increment(ref stamps);

        /// <summary>May a rebuild be forced for <paramref name="t"/>? True arms it.</summary>
        internal bool TryArm(string t)
        {
            lock (gate)
            {
                if (t == exhausted) return false;
                if (pending && t == target) return false;
                target = t;
                armedStamp = Stamp();
                pending = true;
                return true;
            }
        }

        /// <summary>A run published <paramref name="published"/>.</summary>
        /// <param name="freshTarget">What the settings say now; called only on a disagreement, outside the lock.</param>
        internal void OnPublished(string published, long runStamp, Func<string?> freshTarget, Action<string> warn,
                                  Action<string>? debug = null)
        {
            string armed;
            long armedAt;
            lock (gate)
            {
                if (exhausted != null && published == exhausted)
                {
                    exhausted = null;
                    debug?.Invoke($"shell caught up with [{published}] — it may be forced again");
                }
                if (target == null || runStamp < armedStamp) return;
                if (published == target)
                {
                    target = null;
                    pending = false;
                    return;
                }
                armed = target;
                armedAt = armedStamp;
            }

            string? fresh;
            try { fresh = freshTarget(); }
            catch (Exception) { fresh = null; }

            bool disagree;
            lock (gate)
            {
                if (target != armed || armedStamp != armedAt) return;   // armed again meanwhile: that arm stands
                disagree = fresh == armed;
                if (disagree) exhausted = armed;
                target = null;
                pending = false;
            }
            if (disagree)
                warn($"forced a rebuild for the body's switched shapes [{armed}] but the build drew [{published}]; the "
                     + "check and the build disagree — not forcing again for these settings");
            else
                debug?.Invoke($"the settings moved again since [{armed}] was forced — letting the next check decide");
        }

        /// <summary>A run ended; one that did not publish releases a pending target so it may be forced again.</summary>
        internal void OnRunEnded(long runStamp, bool published, Action<string>? debug = null)
        {
            lock (gate)
            {
                if (published || !pending || runStamp < armedStamp) return;
                pending = false;
            }
            debug?.Invoke("a forced run ended without publishing — the target may be forced again");
        }

        /// <summary>Has <paramref name="t"/> been given up on?</summary>
        internal bool IsExhausted(string t)
        {
            lock (gate) return t == exhausted;
        }
    }
}
