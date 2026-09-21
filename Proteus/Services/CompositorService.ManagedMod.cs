using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Penumbra.Api.Enums;

namespace Proteus.Services;

public partial class CompositorService
{
    // ── Managed mod helpers ──────────────────────────────────────────────────

    /// <summary>
    /// Whether this session has already settled the managed mod's manifest version (a one-shot on the composite path).
    /// </summary>
    private bool _managedModVersionChecked;

    private void EnsureManagedModExists()
    {
        // Keyed on the manifest, not the directory: without meta.json Penumbra doesn't register the mod. Safe to rewrite,
        // since every caller recomposites straight afterwards.
        var metaPath = Path.Combine(managedModDir, PenumbraModMeta.MetaFile);
        if (File.Exists(metaPath))
        {
            // Older builds stamped this folder as Penumbra v3, which PenumbraModMeta refuses to write into: migrate it.
            // Once per session; nothing puts a v3 manifest back.
            if (!_managedModVersionChecked)
            {
                _managedModVersionChecked = true;
                PenumbraModMeta.MigrateToCurrent(managedModDir);
            }
            return;
        }

        _managedModVersionChecked = true;   // whatever we write below is current by construction
        var repairing = Directory.Exists(managedModDir);
        if (repairing)
            log.Warning("[Proteus] Managed mod at \"{0}\" was missing its {1} — recreating it",
                managedModDir, PenumbraModMeta.MetaFile);

        WriteManagedModFiles();
        RegisterManagedMod(repairing ? "Repaired" : "Created");
    }

    /// <summary>
    /// The managed mod's folder and a fresh <see cref="PenumbraModMeta.MetaFile"/>. Destructive: the manifest holds the
    /// redirects and Penumbra's Identifier, so callers must gate on <see cref="PenumbraModMeta.HasReadableManifest"/>;
    /// <see cref="WriteManagedModJson"/> is the non-destructive way to touch a live folder.
    /// </summary>
    private void WriteManagedModMeta()
    {
        Directory.CreateDirectory(managedModDir);
        Directory.CreateDirectory(Path.Combine(managedModDir, "textures"));

        // Through AtomicWrite, never File.WriteAllText: a crash mid-write must not leave an unparseable manifest.
        PenumbraModMeta.AtomicWrite(
            Path.Combine(managedModDir, PenumbraModMeta.MetaFile),
            PenumbraModMeta.NewMetaJson(
                SidecarDiscoveryService.ManagedModDir, "Proteus",
                "Managed by the Proteus overlay compositor plugin."));
    }

    /// <summary>
    /// The manifest plus an empty redirect set, for a mod created from nothing. Only safe from
    /// <see cref="EnsureManagedModExists"/>, which is always followed by a republish.
    /// </summary>
    private void WriteManagedModFiles()
    {
        WriteManagedModMeta();
        WriteManagedModJson(new Dictionary<string, string>());
    }

    /// <summary>
    /// Whether Penumbra currently lists the managed mod. Null when the query failed, which must not be read as missing.
    /// Not called on the healthy path (whole mod list over IPC); see <see cref="CheckManagedModHealth"/>.
    /// </summary>
    private bool? IsListedByPenumbra()
    {
        var mods = penumbra.GetAllMods();
        if (mods == null) return null;
        return mods.Keys.Any(d => string.Equals(d, SidecarDiscoveryService.ManagedModDir, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Whether the "Penumbra took the mod but won't list it" repair has rewritten the manifest this session (one attempt).
    /// Set only where a rewrite happens, so an early false alarm cannot disarm a later real repair.
    /// </summary>
    private bool _manifestRewriteAttempted;

    /// <summary>
    /// Whether the "unlisted, but the manifest reads fine" notice has been logged this session. Separate from
    /// <see cref="_manifestRewriteAttempted"/> so silencing the message cannot silence the repair.
    /// </summary>
    private bool _manifestIntactNoticeLogged;

    /// <summary>
    /// The priority to assert whenever we (re)establish the managed mod's settings. Never below what
    /// <see cref="TryRaisePriorityAbove"/> achieved this session, since <see cref="_highestPriorityRaiseAttempted"/>
    /// would refuse to raise again.
    /// </summary>
    private int WantedManagedModPriority
        => Math.Max(config.ManagedModPriority, _highestPriorityRaiseAttempted);

    /// <summary>
    /// AddMod the managed directory, then enable it at our priority in the player's collection. Error codes are logged:
    /// if AddMod fails nothing downstream can work.
    /// </summary>
    private void RegisterManagedMod(string verb)
    {
        var ec = penumbra.AddModDirectory(SidecarDiscoveryService.ManagedModDir);
        log.Information("[Proteus] AddMod({0}) -> {1}", managedModDir, ec);

        if (ec is not (PenumbraApiEc.Success or PenumbraApiEc.NothingChanged))
        {
            log.Warning("[Proteus] Penumbra refused to add the managed mod ({0}). It is usually because "
                      + "\"{1}\" is not inside Penumbra's mod root — overlays cannot apply until it is.",
                ec, managedModDir);
        }
        else if (!_manifestRewriteAttempted && IsListedByPenumbra() == false)
        {
            // AddMod success means the call worked, not that the mod loaded: an unreadable manifest is taken and dropped.
            // The rewrite latch is tested first to avoid a mod-list query once the one attempt is spent. Only rewrite an
            // unreadable manifest: a readable one holds our redirects and Identifier, and replacing it unpublishes everything.
            if (PenumbraModMeta.HasReadableManifest(managedModDir))
            {
                // Does not set _manifestRewriteAttempted (see that field), keeping the repair armed.
                if (!_manifestIntactNoticeLogged)
                {
                    _manifestIntactNoticeLogged = true;
                    log.Warning("[Proteus] Penumbra accepted the managed mod but does not list it, and its "
                              + "\"{0}\" reads fine — so the manifest is not what is stopping the load. Check "
                              + "that \"{1}\" is inside Penumbra's mod root, then press Rediscover Mods in "
                              + "Penumbra's settings.", PenumbraModMeta.MetaFile, managedModDir);
                }
            }
            else
            {
                _manifestRewriteAttempted = true;
                log.Warning("[Proteus] Penumbra accepted the managed mod but does not list it — its \"{0}\" "
                          + "is unreadable. Rewriting it and retrying once.", PenumbraModMeta.MetaFile);

                // Safe: an unparseable manifest publishes nothing, so no live redirect set is lost.
                WriteManagedModMeta();
                ec = penumbra.AddModDirectory(SidecarDiscoveryService.ManagedModDir);
                log.Information("[Proteus] AddMod retry -> {0}", ec);

                if (IsListedByPenumbra() == false)
                    log.Warning("[Proteus] Penumbra still does not list the managed mod after its \"{0}\" was "
                              + "rewritten — overlays cannot apply. Check that \"{1}\" is inside Penumbra's mod "
                              + "root, then press Rediscover Mods in Penumbra's settings.",
                        PenumbraModMeta.MetaFile, managedModDir);
            }
        }

        // Log which collection the new mod was enabled in and where it landed: the first things to check when textures don't show.
        var coll = penumbra.GetPlayerCollection();
        if (!coll.HasValue)
        {
            log.Warning("[Proteus] {0} managed mod at \"{1}\", but the player's collection could not be "
                      + "determined — it has not been enabled anywhere. Overlays will not apply until it is.",
                verb, managedModDir);
            return;
        }

        var (collId, collName) = coll.Value;
        var priority = WantedManagedModPriority;

        // Read back the enable result: an unregistered mod answers ModMissing, and claiming "enabled" would mislead.
        var enabled = penumbra.SetModEnabled(collId, SidecarDiscoveryService.ManagedModDir, true);
        if (enabled is not (PenumbraApiEc.Success or PenumbraApiEc.NothingChanged))
        {
            log.Warning("[Proteus] {0} managed mod at \"{1}\", but Penumbra would not enable it in "
                      + "collection \"{2}\" ({3}) — overlays will not apply.",
                verb, managedModDir, collName, enabled);
            return;
        }

        penumbra.SetModPriority(collId, SidecarDiscoveryService.ManagedModDir, priority);
        log.Information("[Proteus] {0} managed mod at \"{1}\", enabled in collection \"{2}\" ({3}) at priority {4}",
            verb, managedModDir, collName, collId, priority);
    }

    private void CheckManagedModHealth(List<OverlayEntry> overlayEntries)
    {
        var collId = penumbra.GetPlayerCollectionId();
        if (!collId.HasValue) return;

        var settings = penumbra.GetModSettings(collId.Value, SidecarDiscoveryService.ManagedModDir);
        if (settings == null)
        {
            // Two failures land here: Penumbra doesn't have the mod at all (nothing to enable; re-add it), or it has no settings
            // in this collection (enable it). Null settings is the cheap symptom that justifies the whole-list query.
            log.Warning("[Proteus] Managed mod has no settings in the player collection — repairing");
            if (IsListedByPenumbra() == false)
            {
                log.Warning("[Proteus] Penumbra does not list the managed mod at \"{0}\" — registering it",
                    managedModDir);
                RegisterManagedMod("Re-registered");   // enables and sets priority itself
                return;
            }

            var ec = penumbra.SetModEnabled(collId.Value, SidecarDiscoveryService.ManagedModDir, true);
            if (ec is not (PenumbraApiEc.Success or PenumbraApiEc.NothingChanged))
                log.Warning("[Proteus] Managed mod could not be enabled in the player collection ({0}) "
                          + "— composited textures will not apply", ec);
            else
            {
                penumbra.SetModPriority(collId.Value, SidecarDiscoveryService.ManagedModDir, WantedManagedModPriority);
                log.Information("[Proteus] managed mod had no settings — re-enabled at priority {0}", WantedManagedModPriority);
            }
            return;
        }

        if (!settings.Value.Enabled)
        {
            log.Warning("[Proteus] Managed mod is disabled in player collection — enabling");
            penumbra.SetModEnabled(collId.Value, SidecarDiscoveryService.ManagedModDir, true);
        }

        if (overlayEntries.Count > 0)
        {
            int managedPriority = settings.Value.Priority;
            int maxOverlayPriority = overlayEntries.Max(e => e.Priority);
            if (maxOverlayPriority >= managedPriority)
                log.Warning("[Proteus] Managed mod priority ({0}) is not higher than overlay mod priority ({1}) — composited textures may be overridden",
                    managedPriority, maxOverlayPriority);
        }
    }

    /// <summary>
    /// Write the managed mod's redirects in whichever layout the installed Penumbra reads (meta.json <c>DefaultData</c>
    /// from FileVersion 4, default_mod.json before). <paramref name="manipulations"/> carries metadata edits, e.g. the
    /// EQDP entry a second-skin host needs to load the character's own race/gender model.
    /// </summary>
    private void WriteManagedModJson(IDictionary<string, string> redirects, IReadOnlyList<object>? manipulations = null)
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (gamePath, relPath) in redirects)
            files[gamePath] = relPath;

        lock (_manifestLock)
            PenumbraModMeta.WriteRedirects(
                managedModDir, SidecarDiscoveryService.ManagedModDir, files, swaps: null, manipulations: manipulations);
    }

    /// <summary>
    /// Serialises every write to the managed mod's manifest, and lets <see cref="PrimeUpstreamCache"/> hold a
    /// read-modify-write across its narrow-and-restore span so an overlapping publish is never overwritten.
    /// Reentrant per thread.
    /// </summary>
    private readonly object _manifestLock = new();

    /// <summary>
    /// The last disk path Penumbra resolved each game path to that was not our own output: the real upstream base.
    /// Used when a resolution points at the managed mod; falling back to SqPack fails for skin mods' invented paths.
    /// </summary>
    private readonly ConcurrentDictionary<string, string> _upstreamByGamePath =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True when this run's output is already at <paramref name="outPath"/>, so the write can be skipped: the name carries
    /// a <see cref="ContentTag"/> plus <see cref="OutputFormatVersion"/>. Skipping also keeps LastWriteTime, which sync
    /// plugins watch. The file is also checked for completeness, since anything damaging it later would otherwise be
    /// re-approved forever.
    /// </summary>
    private bool AlreadyWritten(string outPath)
    {
        bool complete;
        try
        {
            // One stat for the whole check: FileInfo caches Exists/Length, and IsCompleteTex reuses that snapshot.
            var fi = new FileInfo(outPath);
            if (!fi.Exists) return false;      // absent is the normal first-run case, not damage — no warning
            complete = TextureLoader.IsCompleteTex(fi);
        }
        catch { return false; }   // unreadable — rewrite rather than trust it

        if (complete) return true;

        log.Warning("[Proteus] output present but incomplete — rewriting: {0}", outPath);
        return false;
    }

    /// <summary>
    /// True when <paramref name="diskPath"/> is a file this plugin wrote into the managed mod, compared on canonical full
    /// paths. Undecidable input answers true: reading our own output as a base compounds silently.
    /// </summary>
    private bool IsOwnOutput(string? diskPath)
    {
        var diskFull    = TryCanonicalise(diskPath);
        var managedFull = TryCanonicalise(managedModDir);

        if (diskFull != null && managedFull != null)
            return IsUnderRoot(diskFull, managedFull);

        // Wouldn't canonicalise: a separator-normalised compare still catches the ordinary case.
        var d = diskPath?.Replace('/', Path.DirectorySeparatorChar);
        var m = managedModDir?.Replace('/', Path.DirectorySeparatorChar);
        if (!string.IsNullOrEmpty(d) && !string.IsNullOrEmpty(m))
            return IsUnderRoot(d, m);

        log.Warning("[Proteus] could not tell whether \"{0}\" is our own output — treating it as ours "
                  + "rather than risk compositing onto a previous composite", diskPath ?? "(null)");
        return true;
    }

    private static bool IsUnderRoot(string path, string root)
        => path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                           StringComparison.OrdinalIgnoreCase)
        || string.Equals(path.TrimEnd(Path.DirectorySeparatorChar),
                         root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

    private static string? TryCanonicalise(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return Path.GetFullPath(path); } catch { return null; }
    }

    /// <summary>
    /// Bump whenever the writer's output changes for identical inputs (.tex header layout, mip policy, BC7 encoder).
    /// Folded into every output filename, since everything downstream keys on the path.
    /// </summary>
    internal const int OutputFormatVersion = 1;

    /// <summary>
    /// Build the compressed copies of a freshly imported pack's own textures, so the first composite does not have to
    /// (see <see cref="PackTextureCompressionCache"/>). Only art the author left uncompressed is converted, and only
    /// while compression is switched on and this machine can afford it — the same gate the composite uses, asked once
    /// here rather than per texture. Import runs off the framework thread already, and nothing here touches the game.
    /// </summary>
    internal void PrewarmPackTextures(string? modRoot)
    {
        if (string.IsNullOrEmpty(modRoot) || !config.EnableCompression || !textureLoader.CompressionAffordable())
            return;

        // Only the textures the sidecar says its pieces can be served from — not every .tex in the folder. A gear
        // pack can carry hundreds that Penumbra serves directly and Proteus never republishes, and converting those
        // would spend minutes of encoding and a sidecar full of copies nothing ever reads.
        var declared = DeclaredPackTextures(modRoot);
        if (declared.Count == 0) return;

        int built = 0, refused = 0;
        foreach (var (gamePath, disk) in declared)
        {
            if (!TextureLoader.IsSyncRecompressible(disk)) continue;   // the author already compressed it
            if (PackTextureCompressionCache.TryEnsure(
                    modRoot, disk, SecondSkinService.IsIndexTexturePath(gamePath), textureLoader, log, out _))
                built++;
            else
                refused++;
        }

        if (built > 0 || refused > 0)
            log.Information("[Proteus] pack textures: compressed {0} of this pack's own texture(s) into its sidecar; "
                          + "{1} left as the author wrote them", built, refused);
    }

    /// <summary>
    /// Every texture the freshly written sidecar names, as (published game path, the file inside the pack). Read back
    /// from metadata.json rather than passed in, so this asks the same record the composite will ask. The game path is
    /// what decides index-ness, which is why the pair is carried rather than the file alone.
    /// </summary>
    private List<(string GamePath, string Disk)> DeclaredPackTextures(string modRoot)
    {
        var found = new List<(string, string)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (SidecarDiscoveryService.TryReadMetadata(modRoot) is not { } meta) return found;

        void Collect(ContentPiece? piece)
        {
            if (piece?.TextureOptions is not { Count: > 0 } options) return;
            foreach (var (gamePath, sources) in options)
                foreach (var source in sources)
                {
                    if (string.IsNullOrWhiteSpace(source.File)) continue;
                    var disk = Path.GetFullPath(Path.Combine(modRoot, source.File.Replace('/', Path.DirectorySeparatorChar)));
                    // A pack can name the same file under several options; convert it once.
                    if (!IsUnderDirectory(disk, modRoot) || !File.Exists(disk) || !seen.Add(disk)) continue;
                    found.Add((gamePath, disk));
                }
        }

        foreach (var piece in meta.Content ?? []) Collect(piece);
        foreach (var group in meta.ContentGroups ?? [])
            foreach (var option in group.Options ?? [])
                foreach (var piece in option.Pieces) Collect(piece);

        return found;
    }

    /// <summary>
    /// Whether <paramref name="path"/> sits inside <paramref name="directory"/>, compared as full paths. Also the
    /// guard that keeps a pack's <c>..</c> in a declared file name from reaching outside its own folder.
    /// </summary>
    private static bool IsUnderDirectory(string path, string directory)
    {
        try
        {
            var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}
