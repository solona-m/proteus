using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Proteus.Interop;

namespace Proteus.Services;

/// <summary>
/// Moves Proteus's own per-mod state from one Penumbra mod folder to another when the mod is renamed there
/// (Penumbra's <c>ModMoved</c>). Pure: no I/O, no logging, no IPC — the owners lock and save around it, and
/// <see cref="ModMoveService"/> decides the order.
/// <para/>
/// Old keys are matched case-insensitively everywhere, even in dictionaries that compare ordinally after
/// deserialisation. Collections are changed in place, so their comparers survive. Where the new folder already has an
/// entry it is a leftover of a deleted mod (Penumbra refuses to move onto a folder that is not empty): the moved mod's
/// value wins, and sets take the union.
/// </summary>
internal static class ModMoveMigration
{
    public static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>A selection on the moved mod follows it; any other selection stays where it is.</summary>
    public static string? Follow(string? current, string oldDir, string newDir) => Same(current, oldDir) ? newDir : current;

    /// <summary>Move the entry keyed by <paramref name="oldDir"/> to <paramref name="newDir"/>.</summary>
    public static bool RekeyKey<T>(IDictionary<string, T> d, string oldDir, string newDir)
    {
        var olds = d.Keys.Where(k => Same(k, oldDir)).ToList();
        if (olds.Count == 0) return false;

        var value = d[olds[0]];
        foreach (var k in olds) d.Remove(k);
        Put(d, newDir, value);
        return true;
    }

    /// <summary>Move <paramref name="oldDir"/> in a set to <paramref name="newDir"/>.</summary>
    public static bool RekeySet(ISet<string> s, string oldDir, string newDir)
    {
        var olds = s.Where(k => Same(k, oldDir)).ToList();
        if (olds.Count == 0) return false;

        foreach (var k in olds) s.Remove(k);
        foreach (var k in s.Where(k => Same(k, newDir)).ToList()) s.Remove(k);
        s.Add(newDir);
        return true;
    }

    /// <summary>
    /// Move every key of the form <c>oldDir + sep + rest</c>. The separator is part of the match, so "Bone" never
    /// catches "Boney".
    /// </summary>
    public static int RekeyPrefix<T>(IDictionary<string, T> d, string oldDir, string newDir, char sep)
    {
        var olds = d.Keys.Where(k => HasPrefix(k, oldDir, sep)).ToList();
        foreach (var k in olds)
        {
            var value = d[k];
            d.Remove(k);
            Put(d, newDir + sep + k[(oldDir.Length + 1)..], value);
        }
        return olds.Count;
    }

    /// <summary><see cref="RekeyPrefix{T}"/> for a set.</summary>
    public static int RekeyPrefixSet(ISet<string> s, string oldDir, string newDir, char sep)
    {
        var olds = s.Where(k => HasPrefix(k, oldDir, sep)).ToList();
        foreach (var k in olds)
        {
            s.Remove(k);
            var moved = newDir + sep + k[(oldDir.Length + 1)..];
            foreach (var other in s.Where(x => Same(x, moved)).ToList()) s.Remove(other);
            s.Add(moved);
        }
        return olds.Count;
    }

    /// <summary>
    /// Move every item of the form <c>rest + sep + oldDir</c>. With <paramref name="lower"/> the new folder is written
    /// lower-case, as <c>AutoRefitDecisions.Worn.Key</c> writes it.
    /// </summary>
    public static int RekeySuffixSet(ISet<string> s, string oldDir, string newDir, char sep, bool lower)
    {
        var tail = sep + oldDir;
        var olds = s.Where(k => k.Length > tail.Length && k.EndsWith(tail, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var k in olds)
        {
            s.Remove(k);
            var moved = k[..^oldDir.Length] + (lower ? newDir.ToLowerInvariant() : newDir);
            foreach (var other in s.Where(x => Same(x, moved)).ToList()) s.Remove(other);
            s.Add(moved);
        }
        return olds.Count;
    }

    private static bool HasPrefix(string key, string dir, char sep)
        => key.Length > dir.Length && key[dir.Length] == sep && key.StartsWith(dir, StringComparison.OrdinalIgnoreCase);

    /// <summary>Set <paramref name="key"/>, first dropping a differently-cased twin an ordinal dictionary would keep.</summary>
    private static void Put<T>(IDictionary<string, T> d, string key, T value)
    {
        foreach (var k in d.Keys.Where(k => Same(k, key) && k != key).ToList()) d.Remove(k);
        d[key] = value;
    }

    /// <summary>
    /// The settings Proteus keeps per mod folder: the AO/Skindenting choice (and the legacy opt-out), "overlay onto the
    /// vanilla body", "overlay on men", both stack orders, the body-mod classification, and the auto refit's garment-side bookkeeping in
    /// every collection. Not the auto refit's body folders: existing refits name the old folder in their records, and
    /// carrying the choice over would have them remade. The caller holds <see cref="Proteus.Configuration.Gate"/> and saves.
    /// </summary>
    /// <returns>How many entries moved.</returns>
    public static int Configuration(Configuration c, string oldDir, string newDir)
    {
        int n = 0;
        n += RekeyKey(c.AmbientOcclusionOverrides, oldDir, newDir) ? 1 : 0;
        n += RekeySet(c.AmbientOcclusionDisabledMods, oldDir, newDir) ? 1 : 0;
        n += RekeyKey(c.SiblingSynthesis, oldDir, newDir) ? 1 : 0;
        n += RekeySet(c.MenOverlayOffMods, oldDir, newDir) ? 1 : 0;
        n += RekeyKey(c.OverlayModStackOrder, oldDir, newDir) ? 1 : 0;
        n += RekeyPrefix(c.OverlayStackOrder, oldDir, newDir, '\u0000');
        n += RekeyKey(c.KnownBodyMods, oldDir, newDir) ? 1 : 0;

        foreach (var pref in c.AutoRefitByCollection.Values)
        {
            n += RekeyPrefixSet(pref.SwitchedOn, oldDir, newDir, '|');
            n += RekeyPrefix(pref.AuthorSwitchedOn, oldDir, newDir, '|');
            n += RekeyPrefix(pref.Before, oldDir, newDir, '|');
            n += RekeyPrefix(pref.HeldBefore, oldDir, newDir, '|');
            n += RekeySet(pref.InheritedBefore, oldDir, newDir) ? 1 : 0;
            n += RekeySuffixSet(pref.Declined, oldDir, newDir, '|', lower: true);
        }
        return n;
    }

    /// <summary>
    /// The wearer's saved presets and the pin. A pinned author preset with no id of its own was pinned by an id derived
    /// from the folder (<see cref="PresetService.PackId"/>), so that pin is derived again for the new folder; with
    /// <paramref name="packPresets"/> unknown it is left as it is rather than pruned.
    /// </summary>
    public static bool Presets(ModPresetStore store, string oldDir, string newDir, IReadOnlyList<ModPreset>? packPresets)
    {
        bool changed = RekeyKey(store.Presets, oldDir, newDir);
        if (!RekeyKey(store.Applied, oldDir, newDir)) return changed;

        var pinned = store.Applied[newDir];
        if (packPresets?.FirstOrDefault(p => p.Id == Guid.Empty && PresetService.PackId(oldDir, p) == pinned) is { } pack)
            store.Applied[newDir] = PresetService.PackId(newDir, pack);
        return true;
    }

    /// <summary>
    /// Every design binding's record of the mod, in its Proteus-mods snapshot and its whole-character snapshot. A stale
    /// entry already naming the new folder in the same list is dropped first.
    /// </summary>
    /// <returns>How many records were rewritten.</returns>
    public static int Bindings(DesignBindingStore store, string oldDir, string newDir)
    {
        int n = 0;
        foreach (var binding in store.Bindings.Values)
        {
            n += Rewrite(binding.Mods, m => m.ModDirectory, (m, d) => m.ModDirectory = d, oldDir, newDir);
            n += Rewrite(binding.CharacterMods, m => m.ModDirectory, (m, d) => m.ModDirectory = d, oldDir, newDir);
        }
        return n;
    }

    private static int Rewrite<T>(List<T> list, Func<T, string> dirOf, Action<T, string> setDir, string oldDir, string newDir)
    {
        if (!list.Any(m => Same(dirOf(m), oldDir))) return 0;
        list.RemoveAll(m => Same(dirOf(m), newDir) && !Same(dirOf(m), oldDir));
        int n = 0;
        foreach (var m in list.Where(m => Same(dirOf(m), oldDir)))
        {
            setDir(m, newDir);
            n++;
        }
        return n;
    }

    /// <summary>A mod the design holds temporarily: the record moves, and names the new folder.</summary>
    public static bool MoveHeld(Dictionary<string, PenumbraModSetting> held, string oldDir, string newDir)
    {
        if (!RekeyKey(held, oldDir, newDir)) return false;
        held[newDir].ModDirectory = newDir;
        return true;
    }

    /// <summary>
    /// Hat compat's undone hairstyle (<c>ModRoot|Rel</c>), its root rebuilt the way <see cref="HatCompatService.InMods"/>
    /// builds one, so the comparison against the next examination's identity holds.
    /// </summary>
    public static string? HatIdentity(string? identity, string modsRoot, string oldDir, string newDir)
    {
        if (identity == null || string.IsNullOrEmpty(modsRoot)) return identity;
        string oldRoot, newRoot;
        try
        {
            oldRoot = HatCompatService.ModRootOf(modsRoot, oldDir);
            newRoot = HatCompatService.ModRootOf(modsRoot, newDir);
        }
        catch (ArgumentException) { return identity; }
        catch (NotSupportedException) { return identity; }

        var prefix = oldRoot + "|";
        return identity.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? newRoot + "|" + identity[prefix.Length..]
            : identity;
    }

    /// <summary>
    /// The auto refit's last look at what is worn, with the moved mod's slots naming the new folder: otherwise the next
    /// look sees every renamed garment as newly put on and refits it again.
    /// </summary>
    public static Dictionary<string, AutoRefitDecisions.Worn>? MoveWorn(
        IReadOnlyDictionary<string, AutoRefitDecisions.Worn>? lastSeen, string oldDir, string newDir)
    {
        if (lastSeen == null) return null;
        var moved = new Dictionary<string, AutoRefitDecisions.Worn>(StringComparer.Ordinal);
        foreach (var (slot, worn) in lastSeen)
            moved[slot] = Same(worn.ModDir, oldDir) ? worn with { ModDir = newDir } : worn;
        return moved;
    }

    /// <summary>A file under the moved mod's folder, pointed into the new folder; any other path as it was.</summary>
    public static string RemapPathFolder(string path, string modsRoot, string oldDir, string newDir)
    {
        if (!Same(CompositorService.ModFolderOf(path, modsRoot), oldDir)) return path;
        var rel = path[modsRoot.Length..].TrimStart('/', '\\');
        var cut = rel.IndexOfAny(['/', '\\']);
        return cut < 0 ? Path.Combine(modsRoot, newDir) : Path.Combine(modsRoot, newDir, rel[(cut + 1)..]);
    }

    /// <summary>What kind of move a <c>ModMoved</c> is.</summary>
    public enum MoveKind
    {
        /// <summary>Nothing to do: a name missing, or the same folder.</summary>
        Ignore,

        /// <summary>A mod renamed: its state follows it.</summary>
        Normal,

        /// <summary>Proteus's own output mod renamed.</summary>
        ManagedRenamed,

        /// <summary>A mod renamed onto Proteus's output folder.</summary>
        OntoManaged,

        /// <summary>The new folder is no longer installed by the time the move is handled: deleted since.</summary>
        Gone,
    }

    /// <param name="installed">Penumbra's mod list as it is now, or null when it could not be read (then not checked).</param>
    public static MoveKind Classify(string? oldDir, string? newDir, IReadOnlyDictionary<string, string>? installed)
    {
        if (string.IsNullOrWhiteSpace(oldDir) || string.IsNullOrWhiteSpace(newDir)
            || string.Equals(oldDir, newDir, StringComparison.Ordinal))
            return MoveKind.Ignore;
        if (Same(oldDir, SidecarDiscoveryService.ManagedModDir)) return MoveKind.ManagedRenamed;
        if (Same(newDir, SidecarDiscoveryService.ManagedModDir)) return MoveKind.OntoManaged;
        if (installed != null && !installed.Keys.Any(k => Same(k, newDir))) return MoveKind.Gone;
        return MoveKind.Normal;
    }
}
