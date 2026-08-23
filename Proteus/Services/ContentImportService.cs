using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using CheapLoc;
using Dalamud.Plugin.Services;
using Penumbra.Api.Enums;
using Proteus.Interop;

namespace Proteus.Services;

/// <summary>
/// Imports a Penumbra <c>.pmp</c> that ships GEOMETRY as a Proteus content pack.
/// <para/>
/// The mod stays an ordinary Penumbra mod — Penumbra still owns whether it is on and which of its options
/// are selected, exactly as it does for an overlay pack. Two things change on the way in:
/// <list type="number">
/// <item>every <c>.mdl</c> redirect is removed from the manifest, and</item>
/// <item>a <c>Proteus/metadata.json</c> sidecar names those models instead.</item>
/// </list>
/// Both halves matter. Dropping the redirects is what stops two options in one multi-select group from
/// fighting over a single game path — which is the whole reason a pack like this cannot express "belly
/// button AND hip dermals" on its own — and it is also what stops a pack whose models are a stock mesh with
/// the vanilla geometry emptied out from deleting the wearer's body when Penumbra publishes one. Naming them
/// in the sidecar is what lets the compositor append their meshes into the carrier accessory instead, where
/// every selected option can be worn at once.
/// <para/>
/// Shaped like <see cref="OnionImportService"/> and for the same reason: <see cref="Inspect"/> is cheap and
/// runs on the frame that picked the file, <see cref="Prepare"/> copies tens of megabytes and must not,
/// and <see cref="Register"/> talks to Penumbra so it has to be back on the framework thread.
/// </summary>
public sealed class ContentImportService
{
    private readonly PenumbraBridge penumbra;
    private readonly CompositorService compositor;
    private readonly IPluginLog log;

    public ContentImportService(PenumbraBridge penumbra, CompositorService compositor, IPluginLog log)
    {
        this.penumbra = penumbra;
        this.compositor = compositor;
        this.log = log;
    }

    // ── preview ──────────────────────────────────────────────────────────────

    /// <summary>
    /// One model an option ships, and whether it can be appended.
    /// <para/>
    /// <paramref name="Bindings"/> maps each of the model's DRAWN material names to the pack file backing
    /// it. <paramref name="Unbound"/> lists the drawn material names the pack ships nothing for — a piece
    /// with any of those is reported rather than imported, because binding a mesh to a material its model
    /// does not name is a guess, and the wrong guess renders a metal piercing as skin.
    /// </summary>
    public sealed record PiecePlan(
        string GamePath,
        string Entry,
        IReadOnlyDictionary<string, string> Bindings,
        IReadOnlyList<string> Unbound,
        int Meshes,
        int Vertices,
        string? Problem)
    {
        public bool Import => Problem == null && Bindings.Count > 0;
    }

    /// <summary>One option of one group, with whatever geometry it ships.</summary>
    public sealed record OptionPlan(string Group, string Option, IReadOnlyList<PiecePlan> Pieces)
    {
        public bool Import => Pieces.Any(p => p.Import);
    }

    /// <summary>What an import would do, shown in the Import tab before anything is written.</summary>
    public sealed record ImportPreview(
        string SourcePath,
        PenumbraPackage.Contents Pack,
        IReadOnlyList<OptionPlan> Options,
        IReadOnlyList<string> Warnings)
    {
        public string Name => Pack.Name;
        public string Author => Pack.Author;
        public string? Description => string.IsNullOrWhiteSpace(Pack.Description) ? null : Pack.Description;
        public string? Website => string.IsNullOrWhiteSpace(Pack.Website) ? null : Pack.Website;

        /// <summary>Options carrying at least one appendable model.</summary>
        public int ImportableOptions => Options.Count(o => o.Import);

        public bool AnyImportable => ImportableOptions > 0;

        /// <summary>Every piece the pack ships, importable or not — the count the tab reports against.</summary>
        public int TotalPieces => Options.Sum(o => o.Pieces.Count);
    }

    /// <summary>
    /// Read the pack and work out which of its options carry geometry Proteus can append. Throws
    /// <see cref="InvalidDataException"/> when the file isn't a readable pack.
    /// </summary>
    public static ImportPreview Inspect(string pmpPath, IPluginLog? log = null)
    {
        var pack = PenumbraPackage.Read(pmpPath);
        var warnings = new List<string>();

        // Materials are matched across the WHOLE pack, not within the option that ships the model. Packs
        // routinely put their shared material in one always-on group and their meshes in another — the
        // sample piercings pack does exactly that — so an option-local search would find nothing and
        // reject every mesh in the pack.
        var materialsByLeaf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (gamePath, entry) in pack.AllFiles)
        {
            if (!gamePath.EndsWith(".mtrl", StringComparison.OrdinalIgnoreCase)) continue;
            materialsByLeaf.TryAdd(Path.GetFileName(gamePath), entry);
            materialsByLeaf.TryAdd(Path.GetFileName(entry), entry);
        }

        // Every model the pack redirects, read in ONE pass over the archive rather than one open per file:
        // this runs on the frame the user picked the pack, and a pack with dozens of mesh options would
        // otherwise re-read the zip's central directory dozens of times inside a single draw call.
        var modelEntries = pack.Groups.Where(g => IsSelectable(g.Type))
            .SelectMany(g => g.Options).SelectMany(o => o.Files)
            .Where(f => f.Key.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
            .Select(f => f.Value);
        var models = PenumbraPackage.ReadEntries(pack.Path, modelEntries);

        var options = new List<OptionPlan>();
        foreach (var group in pack.Groups)
        {
            if (!IsSelectable(group.Type))
            {
                warnings.Add(string.Format(Loc.Localize("ContentImport.Warn.GroupType.Fmt",
                    "Group \"{0}\" is a {1} group, which Proteus can't place — its options are left alone."),
                    group.Name, group.Type));
                continue;
            }

            foreach (var option in group.Options)
            {
                var pieces = new List<PiecePlan>();
                foreach (var (gamePath, entry) in option.Files)
                {
                    if (!gamePath.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase)) continue;
                    models.TryGetValue(entry, out var bytes);
                    pieces.Add(PlanPiece(gamePath, entry, bytes, materialsByLeaf, log));
                }
                if (pieces.Count > 0)
                    options.Add(new OptionPlan(group.Name, option.Name, pieces));
            }
        }

        if (pack.DefaultFiles.Keys.Any(k => k.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase)))
            warnings.Add(Loc.Localize("ContentImport.Warn.DefaultModel",
                "This pack redirects a model outside any option. That model is imported as an always-on "
              + "piece — it will be appended whenever the mod is enabled."));

        if (options.Count == 0)
            warnings.Add(Loc.Localize("ContentImport.Warn.NoModels",
                "No option in this pack redirects a model, so there is no geometry for Proteus to append. "
              + "Install it in Penumbra instead."));

        return new ImportPreview(pmpPath, pack, options, warnings);
    }

    /// <summary>Only Single and Multi have selectable options a sidecar group can mirror.</summary>
    private static bool IsSelectable(string type)
        => string.Equals(type, "Single", StringComparison.OrdinalIgnoreCase)
        || string.Equals(type, "Multi", StringComparison.OrdinalIgnoreCase);

    private static PiecePlan PlanPiece(
        string gamePath, string entry, byte[]? model,
        IReadOnlyDictionary<string, string> materialsByLeaf, IPluginLog? log)
    {
        var bindings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var unbound = new List<string>();

        PiecePlan Unreadable(string why)
            => new(gamePath, entry, bindings, unbound, 0, 0,
                   string.Format(Loc.Localize("ContentImport.Problem.Unreadable.Fmt",
                       "not a readable model ({0})"), why));

        // The manifest names a file the archive doesn't carry. Rare, but a pack edited by hand can say it.
        if (model == null) return Unreadable(entry);

        List<string> declared;
        try
        {
            declared = SecondSkinWriter.MaterialNames(model);
        }
        catch (Exception ex)
        {
            log?.Warning(ex, "[Proteus] content import: {0} is not a readable model", entry);
            return Unreadable(ex.Message);
        }

        var used = SecondSkinService.UsedMaterialNames(model, declared);
        if (used.Count == 0)
            return new PiecePlan(gamePath, entry, bindings, unbound, 0, 0,
                Loc.Localize("ContentImport.Problem.NoGeometry", "no geometry — every mesh in it is empty"));

        int meshes = 0, vertices = 0;
        foreach (var name in used)
        {
            var leaf = name.TrimStart('/');
            if (SecondSkinWriter.TryReadLod0Geometry(model, out var pos, out _, out var tri,
                    SecondSkinWriter.KeepByLeaf(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { leaf })))
            {
                vertices += pos.Length / 3;
                if (tri.Length > 0) meshes++;
            }

            if (materialsByLeaf.TryGetValue(leaf, out var mtrlEntry))
                bindings[leaf] = mtrlEntry;
            else
                unbound.Add(leaf);
        }

        string? problem = null;
        if (bindings.Count == 0)
            problem = string.Format(Loc.Localize("ContentImport.Problem.Unbound.Fmt",
                "its mesh names {0}, which this pack does not ship. Rebind the mesh to one of the pack's own "
              + "materials and re-export."), string.Join(", ", unbound));

        return new PiecePlan(gamePath, entry, bindings, unbound, meshes, vertices, problem);
    }

    // ── write ────────────────────────────────────────────────────────────────

    /// <summary>
    /// A mod written to disk by <see cref="Prepare"/> and waiting for <see cref="Register"/>, or the reason
    /// nothing was written.
    /// </summary>
    public sealed record PreparedImport(
        bool Ok, string Message, string? DirName, ImportPreview? Preview, int Pieces, int Skipped);

    /// <summary>
    /// Unpack the mod and write its manifests. Safe off the framework thread; nothing is left behind when
    /// it fails. The result must be handed to <see cref="Register"/> to become a live Penumbra mod.
    /// </summary>
    public PreparedImport Prepare(ImportPreview preview, string modName, string author)
    {
        modName = (modName ?? "").Trim();
        author = (author ?? "").Trim();

        PreparedImport Fail(string why) => new(false, why, null, null, 0, 0);

        if (string.IsNullOrWhiteSpace(modName))
            // Same sentence, same situation as the Onion import's — one key, one translation.
            return Fail(Loc.Localize("Import.NeedName", "Enter a mod name."));
        if (!preview.AnyImportable)
            return Fail(Loc.Localize("ContentImport.Fail.NothingUsable", "Nothing in this pack can be imported."));
        if (!File.Exists(preview.SourcePath))
            return Fail(string.Format(Loc.Localize("ContentImport.Fail.Gone.Fmt",
                "The pack is no longer there: {0}"), preview.SourcePath));

        var dirName = ModCreationService.Sanitize(modName);
        if (dirName == null)
            return Fail(Loc.Localize("ContentImport.Fail.BadName",
                "That mod name has no usable characters — use letters or numbers."));
        if (string.Equals(dirName, SidecarDiscoveryService.ManagedModDir, StringComparison.OrdinalIgnoreCase))
            return Fail(Loc.Localize("ContentImport.Fail.Reserved",
                "\"Proteus\" is reserved — choose a different mod name."));

        var modsRoot = penumbra.GetModDirectory();
        if (string.IsNullOrEmpty(modsRoot))
            return Fail(Loc.Localize("ContentImport.Fail.NoModDir", "Penumbra's mod directory isn't available."));

        var root = Path.Combine(modsRoot, dirName);
        if (Directory.Exists(root))
            return Fail(string.Format(Loc.Localize("ContentImport.Fail.Exists.Fmt",
                "A mod folder named \"{0}\" already exists."), dirName));

        try
        {
            WriteMod(root, modName, author, preview);
        }
        catch (Exception ex)
        {
            log.Error(ex, "[Proteus] content import failed for {0}", dirName);
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { /* best effort */ }
            return Fail(string.Format(Loc.Localize("ContentImport.Fail.Write.Fmt",
                "Failed to write the mod: {0}"), ex.Message));
        }

        int pieces = preview.Options.Sum(o => o.Pieces.Count(p => p.Import));
        return new(true, "", dirName, preview, pieces, preview.TotalPieces - pieces);
    }

    /// <summary>
    /// Unpack the archive, strip the model redirects from the manifests and write the Proteus sidecar.
    /// Pure filesystem work, no IPC, so it can be exercised offline against a temp directory.
    /// </summary>
    internal static void WriteMod(string root, string modName, string author, ImportPreview preview)
    {
        Directory.CreateDirectory(root);

        // The pack's own layout is preserved verbatim: its manifest names files by that layout, and the
        // sidecar written below names the models the same way, so nothing has to be rewritten to point
        // somewhere else.
        using (var zip = ZipFile.OpenRead(preview.SourcePath))
            foreach (var e in zip.Entries)
            {
                if (e.FullName.EndsWith('/')) continue;
                var rel = PenumbraPackage.Normalize(e.FullName).Replace('/', Path.DirectorySeparatorChar);
                var dest = Path.Combine(root, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                e.ExtractToFile(dest, overwrite: true);
            }

        StripModelRedirects(root, preview.Pack);

        var metadata = BuildSidecar(preview, modName, author);
        var metaJson = JsonSerializer.Serialize(metadata, ProteusJson.MetadataWrite);
        var sidecarDir = Path.Combine(root, SidecarDiscoveryService.SidecarSubdir);
        Directory.CreateDirectory(sidecarDir);
        PenumbraModMeta.AtomicWrite(Path.Combine(sidecarDir, "metadata.json"), metaJson);
    }

    /// <summary>
    /// Remove every <c>.mdl</c> redirect from the copied manifests, in whichever layout the pack uses.
    /// The files themselves stay — the sidecar names them — so this only changes who publishes them: not
    /// Penumbra (which can only ever pick ONE option per game path, and would replace the wearer's body
    /// with a mesh that has had its vanilla geometry emptied out) but Proteus, which appends them.
    /// </summary>
    private static void StripModelRedirects(string root, PenumbraPackage.Contents pack)
    {
        if (pack.FileVersion >= PenumbraModMeta.SingleFileVersion)
        {
            EditJson(Path.Combine(root, PenumbraModMeta.MetaFile), manifest =>
            {
                if (manifest["DefaultData"] is JsonObject dd) StripFiles(dd);
                if (manifest["Groups"] is JsonArray groups)
                    foreach (var g in groups)
                        if (g is JsonObject go)
                            StripGroup(go);
            });
            return;
        }

        EditJson(Path.Combine(root, PenumbraModMeta.LegacyDefaultMod), StripFiles);
        foreach (var group in pack.Groups)
            if (group.Entry != null)
                EditJson(Path.Combine(root, group.Entry.Replace('/', Path.DirectorySeparatorChar)), StripGroup);
    }

    private static void StripGroup(JsonObject group)
    {
        if (group["Options"] is JsonArray opts)
            foreach (var o in opts)
                if (o is JsonObject oo) StripFiles(oo);
        // A Combining group's redirects hang off Containers rather than Options. Nothing here imports one,
        // but a pack can mix kinds, and leaving a model redirect behind in one would put the two publishers
        // back in conflict.
        if (group["Containers"] is JsonArray containers)
            foreach (var c in containers)
                if (c is JsonObject co) StripFiles(co);
    }

    private static void StripFiles(JsonObject owner)
    {
        if (owner["Files"] is not JsonObject files) return;
        var doomed = files.Where(p => p.Key.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Key).ToList();
        foreach (var k in doomed) files.Remove(k);
    }

    private static void EditJson(string path, Action<JsonObject> edit)
    {
        if (!File.Exists(path)) return;
        JsonNode? node;
        try { node = JsonNode.Parse(File.ReadAllText(path)); }
        catch (JsonException) { return; }
        if (node is not JsonObject root) return;
        edit(root);
        PenumbraModMeta.AtomicWrite(path, root.ToJsonString(ProteusJson.MetadataWrite));
    }

    /// <summary>The Proteus sidecar mirroring the pack's groups, with one piece per importable model.</summary>
    internal static ProteusMetadata BuildSidecar(ImportPreview preview, string modName, string author)
    {
        var metadata = new ProteusMetadata
        {
            Name = modName,
            Author = author,
        };

        // Always-on models, if the pack redirects one outside every option.
        foreach (var (gamePath, entry) in preview.Pack.DefaultFiles)
        {
            if (!gamePath.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase)) continue;
            var plan = preview.Options.SelectMany(o => o.Pieces)
                .FirstOrDefault(p => string.Equals(p.Entry, entry, StringComparison.OrdinalIgnoreCase));
            if (plan is { Import: true })
                (metadata.Content ??= new()).Add(PieceOf(plan));
        }

        foreach (var byGroup in preview.Options.Where(o => o.Import).GroupBy(o => o.Group, StringComparer.Ordinal))
        {
            var group = new ContentOptionGroup { PenumbraGroupName = byGroup.Key };
            foreach (var opt in byGroup)
            {
                var pieces = opt.Pieces.Where(p => p.Import).Select(PieceOf).ToList();
                if (pieces.Count == 0) continue;
                group.Options.Add(new ContentOption { Name = opt.Option, Pieces = pieces });
            }
            if (group.Options.Count > 0)
                (metadata.ContentGroups ??= new()).Add(group);
        }

        return metadata;
    }

    private static ContentPiece PieceOf(PiecePlan plan)
    {
        var piece = new ContentPiece
        {
            Model = plan.Entry,
            Surface = ShellSurfaceKind.Body,
        };
        foreach (var (leaf, entry) in plan.Bindings)
            piece.Materials[leaf] = entry;
        return piece;
    }

    // ── register ─────────────────────────────────────────────────────────────

    /// <summary>The outcome of a registration. Warning is a success that still needs the user to act.</summary>
    public readonly record struct ImportResult(bool Ok, bool Warning, string Message);

    /// <summary>
    /// Register a <see cref="Prepare"/>d mod with Penumbra, enable it, open Penumbra to it and recomposite.
    /// Must run on the framework thread.
    /// </summary>
    /// <param name="quiet">
    /// Register and nothing else — no Penumbra window, no recomposite. For the teardown path, where the
    /// point is only that a folder already written into Penumbra's directory isn't orphaned; a recomposite
    /// there would wake into half-disposed services. Same reasoning as <see cref="OnionImportService"/>.
    /// </param>
    public ImportResult Register(PreparedImport prepared, bool quiet = false)
    {
        if (!prepared.Ok || prepared.DirName == null || prepared.Preview == null)
            return new(false, false, prepared.Message);

        var dirName = prepared.DirName;

        var ec = penumbra.AddModDirectory(dirName);
        if (ec != PenumbraApiEc.Success)
        {
            log.Warning("[Proteus] AddMod({0}) -> {1}", dirName, ec);
            var modsRoot = penumbra.GetModDirectory();
            if (!string.IsNullOrEmpty(modsRoot))
                try { Directory.Delete(Path.Combine(modsRoot, dirName), true); } catch { /* best effort */ }
            return new(false, false, string.Format(Loc.Localize("Service.RegisterFailed.Fmt",
                "Wrote the mod, but Penumbra couldn't register it ({0}). Rescan mods in Penumbra."), ec));
        }

        var collId = penumbra.GetPlayerCollectionId();
        if (collId.HasValue)
            penumbra.SetModEnabled(collId.Value, dirName, true);
        else
            log.Warning("[Proteus] imported {0}: no player collection — enable it manually", dirName);

        if (!quiet)
        {
            penumbra.OpenToMod(dirName);
            compositor.TriggerRecomposite("content-imported");
        }

        log.Information("[Proteus] imported content pack {0} -> {1} ({2} piece(s), {3} skipped){4}",
            Path.GetFileName(prepared.Preview.SourcePath), dirName, prepared.Pieces, prepared.Skipped,
            quiet ? " [quiet: plugin unloading]" : "");

        var tail = prepared.Skipped > 0
            ? string.Format(Loc.Localize("ContentImport.Result.SkippedTail.Fmt", " (skipped: {0})"), prepared.Skipped)
            : "";
        return new(true, false, string.Format(Loc.Localize("ContentImport.Result.Ok.Fmt",
            "Imported \"{0}\" — pieces: {1}{2}. Enabled it and opened it in Penumbra. Its options are chosen "
          + "in Penumbra, and every one you select is worn at once."),
            dirName, prepared.Pieces, tail));
    }
}
