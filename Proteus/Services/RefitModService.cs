using System;
using System.Collections.Generic;
using System.IO;
using CheapLoc;
using Dalamud.Plugin.Services;
using Penumbra.Api.Enums;
using Proteus.Interop;

namespace Proteus.Services;

/// <summary>
/// Makes the mod a refit of the GAME'S OWN gear goes into — one per item, which is what a garment with no mod behind
/// it has no other home for.
/// <para/>
/// It makes the folder and registers it, and stops there: the refit itself is written by
/// <see cref="BodyRetargetWriter"/>, exactly as it is written into somebody else's mod. That is the whole design —
/// the group, the file-less <c>Original</c> option, the record and the undo are one implementation and not two. In a
/// mod created here <c>Original</c> reads even better than it does elsewhere: picking it gives the player the game's
/// own coat back.
/// <para/>
/// It also makes the mod the game's gear is EDITED in — switches, the brush, a moved part — named after the item and
/// <see cref="EditQualifier"/> instead of a body, and seeded with the drawn model by <see cref="AddGameModel"/>.
/// </summary>
internal sealed class RefitModService(PenumbraBridge penumbra, CompositorService compositor, IPluginLog log)
{
    /// <param name="Root">The mod folder, for <see cref="BodyRetargetWriter"/>.</param>
    /// <param name="Dir">Its name under Penumbra's mods root, for the IPC calls that take one.</param>
    /// <param name="Made">The folder is new this call, rather than one found from before.</param>
    internal readonly record struct Result(bool Ok, string Root, string Dir, string Message, bool Made = false);

    /// <summary>
    /// What stands where a body name would in the name of the mod the game's gear is edited in: "Coat — Proteus edit".
    /// Not localised, because <see cref="Find"/> matches it — a translated one would make a second mod after a
    /// language change, with the edits stranded in the first.
    /// </summary>
    internal const string EditQualifier = "Proteus edit";

    /// <summary>
    /// Put a model of the game's own into <paramref name="root"/>, published at the path the game draws it from, so
    /// the mod draws exactly what the game did and every tool can then edit it as a mod's file. A model already
    /// there is kept — it may carry edits — and only a missing redirect is put back.
    /// <para/>
    /// The file sits at its game path inside the mod, which is how modders lay a mod out and cannot collide.
    /// Pure filesystem, so it is testable without the game.
    /// </summary>
    /// <returns>True when anything was written, so the caller knows Penumbra has to reload the mod.</returns>
    internal static bool AddGameModel(string root, string gamePath, byte[] bytes)
    {
        string rel = gamePath.Replace('\\', '/');
        string file = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
        bool changed = false;
        if (!File.Exists(file))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            PenumbraModMeta.AtomicWrite(file, bytes);
            changed = true;
        }

        // A fresh manifest has no default data at all, which reads as unknown; that is empty. An unreadable manifest
        // is not, and writing over it would drop whatever it held.
        var data = PenumbraModMeta.TryReadDefaultData(root)
                ?? (PenumbraModMeta.HasReadableManifest(root)
                        ? (new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), new List<object>())
                        : throw new InvalidDataException(string.Format(Loc.Localize("Refit.Error.RedirectsUnreadable.Fmt",
                            "Could not read the redirects of {0}."), root)));
        if (data.Files.TryGetValue(rel, out var at) && string.Equals(at.Replace('\\', '/'), rel, StringComparison.OrdinalIgnoreCase))
            return changed;

        data.Files[rel] = rel;
        PenumbraModMeta.WriteRedirects(root, Path.GetFileName(root), data.Files, manipulations: data.Manipulations);
        return true;
    }

    /// <summary>
    /// What the mod is called: the item, then the body it was refitted onto. Both halves matter — the same coat
    /// refitted onto two bodies is two mods, and one of them is not "the other one".
    /// </summary>
    internal static string NameFor(string itemName, string bodyName) => $"{itemName} — {bodyName}";

    /// <summary>The folder name that mod would take, or null when the name has nothing usable in it.</summary>
    internal static string? DirFor(string modName)
    {
        string? dir = ModCreationService.Sanitize(modName);
        return dir != null && !string.Equals(dir, SidecarDiscoveryService.ManagedModDir, StringComparison.OrdinalIgnoreCase)
            ? dir
            : null;
    }

    /// <summary>
    /// The root of the mod already holding this item's refits, or null. No IPC and no writing, so the panel may ask
    /// every time the body changes.
    /// <para/>
    /// A folder whose name matches is not enough: <see cref="ModCreationService.Sanitize"/> drops apostrophes and
    /// dashes, so two items can sanitise alike, and merging two garments into one mod would be silent and wrong. The
    /// manifest's own name is what decides — the NUMBERED one, because that is what <see cref="Ensure"/> wrote into
    /// the mod it had to number. Comparing against the bare name instead finds a numbered mod never, and then every
    /// save makes another: "(2)", "(3)", with the refits scattered behind them.
    /// </summary>
    internal static string? Find(string modsRoot, string itemName, string bodyName)
    {
        string want = NameFor(itemName, bodyName);
        for (int n = 1; n <= MaxNames; n++)
        {
            string name = Numbered(want, n);
            if (DirFor(name) is not { } dir) return null;
            string root = Path.Combine(modsRoot, dir);
            if (!Directory.Exists(root)) return null;                 // nothing beyond here either: they are made in order
            if (string.Equals(NameOf(root), name, StringComparison.Ordinal)) return root;
        }
        return null;
    }

    /// <summary>
    /// Find that mod or make it: the folder, a manifest, and the registration Penumbra needs before anything may be
    /// written into it as a group.
    /// <para/>
    /// Framework thread only — it is Penumbra IPC. Enabling is left to <see cref="Pump"/> across frames, the way
    /// <c>ModCreationService</c> does it, because <c>AddMod</c> is asynchronous and a settings write that lands while
    /// Penumbra is still building the mod is discarded.
    /// </summary>
    /// <param name="fill">
    /// Writes a NEW mod's contents before Penumbra is told about it, so it is loaded whole rather than reloaded half
    /// way through registering. Not called for a mod that was found; the caller fills that and reloads it.
    /// </param>
    /// <param name="description">The manifest's description; a refit's when null.</param>
    internal Result Ensure(string itemName, string bodyName, Action<string>? fill = null, string? description = null)
    {
        if (penumbra.GetModDirectory() is not { Length: > 0 } modsRoot)
            return new(false, "", "", Loc.Localize("Service.NoPenumbraDir", "Penumbra's mod directory isn't available."));

        if (Find(modsRoot, itemName, bodyName) is { } already)
            return new(true, already, Path.GetFileName(already), "");

        string want = NameFor(itemName, bodyName);

        // The first name whose folder is free. A taken one belongs to another item that sanitised the same way —
        // Find has already ruled out its being ours.
        string? dirName = null, modName = null;
        for (int n = 1; n <= MaxNames && dirName == null; n++)
        {
            string candidate = Numbered(want, n);
            if (DirFor(candidate) is not { } dir) break;
            if (Directory.Exists(Path.Combine(modsRoot, dir))) continue;
            dirName = dir;
            modName = candidate;
        }
        if (dirName == null || modName == null)
            return new(false, "", "", string.Format(Loc.Localize("Refit.Error.NoName.Fmt",
                "Couldn't find a free mod name for \"{0}\"."), want));

        string root = Path.Combine(modsRoot, dirName);
        try
        {
            Directory.CreateDirectory(root);
            WriteScaffold(root, modName, description ?? string.Format(Loc.Localize("Refit.Mod.Description.Fmt",
                "{0}, refitted by Proteus."), itemName));
            fill?.Invoke(root);
        }
        catch (Exception ex)
        {
            log.Error(ex, "[Proteus] refit mod write failed for {0}", dirName);
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { /* best effort */ }
            return new(false, "", "", string.Format(Loc.Localize("Create.Error.WriteFailed.Fmt",
                "Failed to write the mod: {0}"), ex.Message));
        }

        var ec = penumbra.AddModDirectory(dirName);
        if (ec != PenumbraApiEc.Success)
        {
            log.Warning("[Proteus] AddMod({0}) -> {1}", dirName, ec);
            // Roll the folder back so the name is free to retry.
            try { Directory.Delete(root, true); } catch { /* best effort */ }
            return new(false, "", "", string.Format(Loc.Localize("Service.RegisterFailed.Fmt",
                "Wrote the mod, but Penumbra couldn't register it ({0}). Rescan mods in Penumbra."), ec));
        }

        pending = new Pending(dirName, modName, Environment.TickCount64 + ActivateTimeoutMs);
        nextAttempt = 0;
        log.Information("[Proteus] made refit mod {0} for {1}", dirName, want);
        return new(true, root, dirName, "", Made: true);
    }

    /// <summary>
    /// The manifest and nothing else.
    /// <para/>
    /// It has to be written before any group goes in: <see cref="PenumbraModMeta"/> reads a folder without one as
    /// pre-v4 and refuses to write into it. Pure filesystem, so it is testable without the game.
    /// </summary>
    internal static void WriteScaffold(string root, string modName, string description)
        => PenumbraModMeta.AtomicWrite(Path.Combine(root, PenumbraModMeta.MetaFile),
                                       PenumbraModMeta.NewMetaJson(modName, "Proteus", description));

    /// <summary>
    /// Continue a registration across frames: enable the mod in the player's collection and say so once it takes.
    /// Null while Penumbra is still loading it. Harmless to call with nothing pending.
    /// </summary>
    internal string? Pump()
    {
        if (pending is not { } p) return null;

        long now = Environment.TickCount64;
        if (now < nextAttempt) return null;
        nextAttempt = now + AttemptIntervalMs;

        var collId = penumbra.GetPlayerCollectionId();
        if (collId == null)
        {
            // Not a reason to wait: no collection is a standing state, not a loading one.
            log.Warning("[Proteus] refit mod {0}: no player collection — enable it manually", p.DirName);
            return Done(p, enabled: false);
        }

        // Ask, then read back: a discarded write still reports Success.
        penumbra.SetModEnabled(collId.Value, p.DirName, true);
        if (penumbra.GetModSettings(collId.Value, p.DirName) is { Enabled: true }) return Done(p, enabled: true);

        if (now < p.Deadline) return null;   // still settling — ask again shortly

        log.Warning("[Proteus] refit mod {0}: Penumbra would not report it enabled within {1}ms",
            p.DirName, ActivateTimeoutMs);
        return Done(p, enabled: false);
    }

    private string Done(Pending p, bool enabled)
    {
        pending = null;
        if (enabled) compositor.TriggerRecomposite("refit-mod-created");
        log.Information("[Proteus] refit mod {0} enabled={1}", p.DirName, enabled);
        return enabled
            ? string.Format(Loc.Localize("Refit.Mod.Enabled.Fmt", "Made \"{0}\" and switched it on."), p.ModName)
            : string.Format(Loc.Localize("Refit.Mod.NotEnabled.Fmt",
                "Made \"{0}\", but couldn't switch it on — enable it in Penumbra."), p.ModName);
    }

    /// <summary>The mod's own name out of its manifest, or null when there is nothing readable there.</summary>
    private static string? NameOf(string root)
    {
        try
        {
            if (!PenumbraModMeta.HasReadableManifest(root)) return null;
            using var doc = System.Text.Json.JsonDocument.Parse(
                File.ReadAllText(Path.Combine(root, PenumbraModMeta.MetaFile)));
            return doc.RootElement.TryGetProperty("Name", out var name) ? name.GetString() : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>"Coat — Neolithe", then "Coat — Neolithe (2)": the first name is never numbered.</summary>
    private static string Numbered(string name, int n) => n <= 1 ? name : $"{name} ({n})";

    /// <summary>How many same-named items to allow before giving up rather than spinning.</summary>
    private const int MaxNames = 20;

    private record struct Pending(string DirName, string ModName, long Deadline);

    private Pending? pending;
    private long nextAttempt;

    /// <summary>How often to re-ask while waiting, so Penumbra IPC is not called every frame.</summary>
    private const long AttemptIntervalMs = 250;

    /// <summary>How long to keep asking before giving up and saying so.</summary>
    private const long ActivateTimeoutMs = 15_000;
}
