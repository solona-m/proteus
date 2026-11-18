using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Proteus.Services.Matcap;

/// <summary>Where a matcap comes from: one of the user's images, or an option of an installed library's slot.</summary>
public sealed record MatcapChoice(MatcapImage? Image, MatcapLibrary? Library, string? Option)
{
    public static MatcapChoice OfImage(MatcapImage image) => new(image, null, null);
    public static MatcapChoice OfLibrary(MatcapLibrary library, string option) => new(null, library, option);
    public bool IsLibrary => Library != null;
}

/// <summary>
/// The matcap tool's entry points, shared by the Studio panel and the QA harness so both do exactly the same thing.
/// <see cref="Prepare"/> is the heavy half (split, bake, build) and runs on a worker against the files as they are;
/// <see cref="MatcapWriter"/> then places it under the write locks. Selecting a library option is Penumbra IPC and is the
/// caller's, on the framework thread (<see cref="MatcapSelection"/>).
/// </summary>
internal static class MatcapStudio
{
    /// <summary>What <see cref="Prepare"/> reads from outside the mod, injected so it runs without the game.</summary>
    /// <param name="ReadGameFile">A game file by path (the template, the IMC, an unredirected material), or null.</param>
    /// <param name="Loader">Decodes and encodes textures.</param>
    /// <param name="Compress">Bake own images as BC7.</param>
    /// <param name="TempDir">Where prepared textures wait until they are placed.</param>
    /// <param name="WornFolder">The material folder the worn variant uses (gear), when known.</param>
    /// <param name="RowOnly">1–16 puts the matcap only on the texels of that colour-table row pair (a matcap copy of the
    /// row's triangles under the part, and a cut copy of the part's own material: <see cref="MatcapRowCut"/>); 0 is the
    /// whole-part matcap.</param>
    /// <param name="RowSub">Which sub-row of <paramref name="RowOnly"/> (Dawntrail index maps only).</param>
    /// <param name="Affordable">Test seam for the BC7 budget guard (<see cref="TextureLoader.CompressionAffordable"/>).</param>
    public sealed record Environment(Func<string, byte[]?> ReadGameFile, TextureLoader Loader, bool Compress, string TempDir,
                                     int? WornFolder = null, int RowOnly = 0, SubRowPick RowSub = SubRowPick.Both,
                                     Func<bool>? Affordable = null);

    /// <summary>How far a row matcap's copy sits UNDER the surface it copies, in metres (negative: inward).</summary>
    internal const float RowOverlayPush = -0.0006f;

    /// <summary>The ticked parts mix parts with and without a matcap (Apply's and Prepare's refusal).</summary>
    internal const string AlreadyMatcapText =
        "Tick either parts without a matcap, to add one, or parts that have one, to change it — not both.";

    internal const string NoEntryText = "That matcap is not in this mod's record.";

    /// <summary>{0} the matcaps not changed, each with why.</summary>
    internal const string ChangePartialText = "Not changed: {0}.";

    /// <summary>Test seam: runs before each material's change in <see cref="ChangeAll"/>, with its name.</summary>
    [ThreadStatic] internal static Action<string>? BeforeChange;

    /// <summary>A prepared apply, or why not.</summary>
    public sealed record Prepared(MatcapWriter.ApplyRequest? Request, string Code, string Message,
                                  IReadOnlyList<string> SiblingsSkipped, bool AlphaTested, bool Lod0Cut)
    {
        public bool Ok => Request != null;
        public static Prepared Refuse(string code, string message) => new(null, code, message, [], false, false);

        /// <summary>Row matcap: an alpha-tested part has holes inside the row's triangles, which will show the matcap.</summary>
        public bool RowHoles { get; init; }
        /// <summary>Row matcap: the cut meshes' vertex alpha was raised to 1.</summary>
        public bool CutAlphaSet { get; init; }
    }

    public const string CodeRowNotGear = "row-not-gear", CodeRowOneMaterial = "row-one-material",
                        CodeRowMixedThreshold = "row-mixed-threshold", CodeRowGameGear = "row-game-gear",
                        CodeRowNothingLeft = "row-nothing-left";


    /// <summary>Prepared textures an earlier apply left behind (placed, or never used): deleted after an hour.</summary>
    internal static void SweepTemp(string dir)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*.tex"))
                try { if (DateTime.UtcNow - File.GetLastWriteTimeUtc(f) > TimeSpan.FromHours(1)) File.Delete(f); } catch { }
        }
        catch { }
    }

    /// <summary>
    /// Prepare a matcap on <paramref name="chosen"/> — parts read from <paramref name="modelBytes"/>, the file at
    /// <paramref name="modelRel"/> as it is now — and on every sibling file at the same game path whose parts line up.
    /// </summary>
    /// <param name="withSiblings">False for a preview: only the open model is split.</param>
    public static Prepared Prepare(string modRoot, string modelRel, string modelGamePath, byte[] modelBytes,
                                   IReadOnlyList<ModelPart> chosen, MatcapChoice choice, Environment env, bool withSiblings = true)
    {
        if (MatcapPlan.TargetOf(modelGamePath, out _) is not { } target)
            return Prepared.Refuse("not-target", ProteusLoc.Localize("Matcap.Refused.NotTarget",
                "Matcaps go on gear, accessories, hair, tails, Viera ears and Au Ra horns (on the face); this model is none of those."));
        if (MatcapPlan.RefusedRegenerated(modRoot, modelRel) is { } regen)
            return Prepared.Refuse("regenerated-model", string.Format(ProteusLoc.Localize("Matcap.Refused.Regenerated.Fmt",
                "This model is made by Proteus from another one ({0}), and remaking it would lose the matcap. Put the matcap on the model it was made from, then remake it."), regen));
        if (chosen.Count == 0)
            return env.RowOnly > 0
                ? Prepared.Refuse(CodeRowNothingLeft, ProteusLoc.Localize("Matcap.Refused.RowNothingLeft",
                    "Every part with this material already has a matcap."))
                : Prepared.Refuse("unknown-part", ProteusLoc.Localize("Parts.Matcap.NeedParts", "Tick the parts that should take the matcap."));
        if (chosen.Any(MatcapPlan.IsSkin)) return Prepared.Refuse("skin-part", ProteusLoc.Localize("Matcap.Refused.Skin", "Skin can't take a matcap."));
        // A row matcap cuts the part's own Dawntrail gear material: gear only, one material at a time.
        if (env.RowOnly > 0)
        {
            if (target.Kind != MatcapPlan.Kind.Gear)
                return Prepared.Refuse(CodeRowNotGear, ProteusLoc.Localize("Matcap.Refused.RowNotGear",
                    "A matcap on one colour row works on gear and accessories only."));
            if (chosen.Select(p => p.Material).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
                return Prepared.Refuse(CodeRowOneMaterial, ProteusLoc.Localize("Matcap.Refused.RowOneMaterial",
                    "A matcap on one colour row works on one material at a time. Tick parts of a single material."));
        }
        // A change of an existing matcap goes through ChangeAll; a new apply never splits a matcap part again.
        if (chosen.Any(MatcapPlan.IsOnMatcap))
            return Prepared.Refuse("already-matcap", ProteusLoc.Localize("Matcap.Refused.AlreadyMatcap", AlreadyMatcapText));

        // A face takes a matcap on its horns only (Au Ra horns are face submeshes tagged atr_hrn).
        if (target.Kind == MatcapPlan.Kind.Face)
        {
            var read = ModelPartReader.Read(modelBytes);
            var attrs = read?.AttributeNames ?? [];
            if (read == null || !MatcapPlan.HasHorns(read.Parts, attrs))
                return Prepared.Refuse("no-horns", ProteusLoc.Localize("Matcap.Refused.NoHorns",
                    "This face has no horns. On a face, only the horns can take a matcap."));
            if (chosen.Any(p => !MatcapPlan.IsHorn(p, attrs)))
                return Prepared.Refuse("not-horn", ProteusLoc.Localize("Matcap.Refused.NotHorn",
                    "On a face, only the horns can take a matcap."));
        }

        var record = MatcapRecord.Read(modRoot);
        if (record.Unreadable)
            return Prepared.Refuse(MatcapWriter.CodeUnreadable, ProteusLoc.Localize("Matcap.Refused.RecordUnreadable",
                "Proteus's record of this mod's matcaps (Proteus/matcaps.json) could not be read, so nothing was changed."));
        var token = record.Token.Length > 0 ? record.Token : MatcapPlan.TokenFor(Path.GetFileName(modRoot.TrimEnd('\\', '/')));
        var redirects = PenumbraModMeta.ReadAllRedirects(modRoot);

        // ── siblings: every file at this game path whose parts line up ──
        // A refit's or hair port's matcap stays on that file alone, and an author's file never spreads one to them: a
        // remake writes them again from the author's file, so tying them into one apply group would leave Undo stuck.
        bool onRegenerated = MatcapPlan.Regenerated(modRoot, modelRel) is MatcapPlan.RefitKind or MatcapPlan.HairPortKind;
        var models = new List<(string Rel, byte[] Bytes, IReadOnlyList<ModelPart> Parts)> { (modelRel, modelBytes, chosen) };
        var skipped = new List<string>();
        foreach (var sib in withSiblings ? HatCompatService.SiblingFiles(modRoot, modelGamePath, modelRel) : [])
        {
            if (PenumbraModMeta.SameFile(sib, modelRel)) continue;
            if (onRegenerated || MatcapPlan.Regenerated(modRoot, sib) != null) { skipped.Add(sib); continue; }
            byte[] bytes;
            try { bytes = File.ReadAllBytes(MatcapWriter.Full(modRoot, sib)); } catch { skipped.Add(sib); continue; }
            var parts = ModelPartReader.Read(bytes)?.Parts;
            if (parts == null || !MatcapPlan.LinesUp(chosen, parts)) { skipped.Add(sib); continue; }
            models.Add((sib, bytes, chosen.Select(c => parts.First(p => PartClaims.LineUp(c, p))).ToList()));
        }

        // ── one matcap material per source material among the ticked parts ──
        // Each keeps its own normal map and alpha threshold: parts drawn by two materials (an Au Ra face's two horn
        // materials, a garment's two cloths) would otherwise sample one of them through the other's UVs.
        var sources = chosen.Select(p => p.Material).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        int room;
        try { room = ModelMaterialWriter.MaterialRoom(modelBytes); }
        catch (Exception ex)
        {
            return Prepared.Refuse(ModelMaterialWriter.CodeLayout,
                string.Format(ProteusLoc.Localize("Matcap.Refused.ModelLayout.Fmt", "This model can't be split: {0}."), ex.Message));
        }
        // A row matcap adds two materials: the matcap and the cut copy of the part's own.
        int need = sources.Count + (env.RowOnly > 0 ? 1 : 0);
        if (room < need)
            return Prepared.Refuse(ModelMaterialWriter.CodeMaterialCap, string.Format(ProteusLoc.Localize("Matcap.Refused.MaterialCap.Fmt",
                "This model can't take another material: {0}."), $"{need} needed, {room} free of {SecondSkinWriter.MaxMaterials}"));

        var taken = new List<string>();
        foreach (var (_, bytes, _) in models)
            try { taken.AddRange(SecondSkinWriter.MaterialNames(bytes)); } catch { }
        taken.AddRange(record.Materials.Select(m => m.Name));
        taken.AddRange(redirects.Select(r => r.GamePath));
        // Files already in our folder (kept by an undo because they were edited by hand) are never written over.
        var filesDir = MatcapWriter.Full(modRoot, MatcapWriter.FilesFolder);
        if (Directory.Exists(filesDir))
            foreach (var f in Directory.EnumerateFiles(filesDir))
                taken.Add(Path.GetFileNameWithoutExtension(f) + ".mtrl");
        // Where the materials will be published (from every candidate model: a superset of what splits, harmless for the
        // collision check), and every game path a material already holds: a new name must not resolve onto one, or two
        // models reading one folder (hair ports of races sharing a hair folder) would share one redirect.
        var publishedAll = redirects.Where(r => models.Any(m => PenumbraModMeta.SameFile(r.File, m.Rel)))
                                    .Select(r => r.GamePath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        IEnumerable<int>? folders = null;
        if (target.Kind == MatcapPlan.Kind.Gear)
        {
            var item = target.Item!.Value;
            folders = MatcapPlan.GearFolders(item, ImcEntrySource.GameEntries(env.ReadGameFile, item.SetTag, item.SlotLabel),
                                             MatcapPlan.ModImcEntries(modRoot, item), redirects.Select(r => r.GamePath), env.WornFolder);
        }
        static string Norm(string path) => path.Replace('\\', '/').ToLowerInvariant();
        var usedPaths = new HashSet<string>(redirects.Select(r => Norm(r.GamePath))
                                                     .Concat(record.Materials.SelectMany(m => m.GamePaths).Select(Norm)), StringComparer.Ordinal);
        var names = new List<string>();
        foreach (var s in sources)
        {
            var n = MatcapPlan.NewName(s, token, taken,
                c => MatcapPlan.MaterialGamePaths(target, c, publishedAll, folders).Any(gp => usedPaths.Contains(Norm(gp))));
            names.Add(n);
            taken.Add(n);
            foreach (var gp in MatcapPlan.MaterialGamePaths(target, n, publishedAll, folders)) usedPaths.Add(Norm(gp));
        }

        // ── a row matcap: the cut copy's name, and its cut planned before any split ──
        string? cutName = null;
        MatcapRowCut.Plan? rowCut = null;
        Directory.CreateDirectory(env.TempDir);
        if (env.RowOnly > 0)
        {
            cutName = MatcapPlan.NewCutName(sources[0], token, taken,
                c => MatcapPlan.MaterialGamePaths(target, c, publishedAll, folders).Any(gp => usedPaths.Contains(Norm(gp))));
            taken.Add(cutName);
            foreach (var gp in MatcapPlan.MaterialGamePaths(target, cutName, publishedAll, folders)) usedPaths.Add(Norm(gp));
            var (plan, refused) = MatcapRowCut.Make(new MatcapRowCut.Inputs(modRoot, target, sources[0],
                (folders ?? [env.WornFolder ?? 1]).ToList(), env.WornFolder ?? 1, env, env.RowOnly, env.RowSub, token, cutName,
                Preview: !withSiblings, Affordable: env.Affordable));
            if (refused != null) return Prepared.Refuse(refused.Code, refused.Message);
            rowCut = plan!;
        }

        // ── the splits, one source material at a time, on each model's running bytes ──
        var running = models.Select(m => (m.Rel, Original: m.Bytes, Current: m.Bytes, m.Parts, Cut: false, Ok: true)).ToList();
        var meshesOf = names.ToDictionary(n => n, _ => new List<(string Rel, int Source, int Drawn)>(), StringComparer.Ordinal);
        var cutMeshes = new List<(string Rel, int Source, int Drawn)>();
        bool rowHoles = false, cutAlphaSet = false;
        for (int si = 0; si < sources.Count; si++)
        {
            for (int mi = 0; mi < running.Count; mi++)
            {
                var m = running[mi];
                if (!m.Ok) continue;
                // The parts of this source, as the model's current bytes read them (an earlier split appends meshes
                // only, so every part still lines up).
                var current = ModelPartReader.Read(m.Current)?.Parts ?? [];
                var mine = m.Parts.Where(p => string.Equals(p.Material, sources[si], StringComparison.OrdinalIgnoreCase))
                                  .Select(p => current.FirstOrDefault(c => PartClaims.LineUp(p, c))).ToList();
                try
                {
                    if (mine.Any(p => p == null)) throw new ModelMaterialWriter.Refused(ModelMaterialWriter.CodeUnknownPart, "a part moved during the split");
                    if (rowCut == null)
                    {
                        var r = ModelMaterialWriter.MoveToMaterial(m.Current, mine!, names[si]);
                        meshesOf[names[si]].AddRange(r.Meshes.Select(x => (m.Rel, x.Source, x.Drawn)));
                        running[mi] = m with { Current = r.Model, Cut = m.Cut || r.Lod0Cut };
                        continue;
                    }

                    // Row: only the triangles touching the row (in any bake), picked on each call's own bytes.
                    var keep = RowTriangles(m.Current, mine!, rowCut, out bool holes);
                    if (keep.Count == 0)
                    {
                        if (mi != 0) { skipped.Add(m.Rel); running[mi] = m with { Ok = false }; continue; }
                        return Prepared.Refuse(MatcapRowCut.CodeEmpty, string.Format(ProteusLoc.Localize("Matcap.Refused.RowEmpty.Fmt",
                            "No pixel of this part uses row {0}."), MatcapRowCut.RowLabel(env.RowOnly, env.RowSub)));
                    }
                    rowHoles |= holes;
                    var copy = ModelMaterialWriter.MoveToMaterial(m.Current, mine!, names[si], RowOverlayPush, keep.Contains);

                    var afterCopy = ModelPartReader.Read(copy.Model)?.Parts ?? [];
                    var mine2 = mine!.Select(p => afterCopy.FirstOrDefault(c => PartClaims.LineUp(p!, c))).ToList();
                    if (mine2.Any(p => p == null)) throw new ModelMaterialWriter.Refused(ModelMaterialWriter.CodeUnknownPart, "a part moved during the split");
                    var keep2 = RowTriangles(copy.Model, mine2!, rowCut, out _);

                    // Vertex alpha, decided over every cut copy: all threshold-0 → raise it; mixed → refuse when it would cut.
                    float alphaMin = ModelMaterialWriter.VertexAlphaMin(copy.Model, mine2!, keep2.Contains);
                    bool alphaOne = false;
                    if (alphaMin < 0.5f && rowCut.AllThresholdZero) alphaOne = true;
                    else if (alphaMin < 0.5f && rowCut.AnyThresholdZero)
                        return Prepared.Refuse(CodeRowMixedThreshold, ProteusLoc.Localize("Matcap.Refused.RowMixedThreshold",
                            "This part's options don't agree on how it is cut out, and its vertex colours would cut it. A row matcap can't fit both."));
                    var moved = ModelMaterialWriter.MoveToMaterial(copy.Model, mine2!, cutName!, 0f, keep2.Contains, alphaOne);
                    cutAlphaSet |= alphaOne;
                    meshesOf[names[si]].AddRange(copy.Meshes.Select(x => (m.Rel, x.Source, x.Drawn)));
                    cutMeshes.AddRange(moved.Meshes.Select(x => (m.Rel, x.Source, x.Drawn)));
                    running[mi] = m with { Current = moved.Model, Cut = m.Cut || copy.Lod0Cut || moved.Lod0Cut };
                }
                catch (ModelMaterialWriter.Refused ex)
                {
                    if (mi != 0) { skipped.Add(m.Rel); running[mi] = m with { Ok = false }; continue; }
                    return Prepared.Refuse(ex.Code, ex.Code == ModelMaterialWriter.CodeMaterialCap
                        ? string.Format(ProteusLoc.Localize("Matcap.Refused.MaterialCap.Fmt", "This model can't take another material: {0}."), ex.Message)
                        : ex.Code == ModelMaterialWriter.CodeVertexAlpha
                        ? ProteusLoc.Localize("Matcap.Refused.RowVertexAlpha", "This part's vertex colours would cut the row matcap, and they can't be changed.")
                        : string.Format(ProteusLoc.Localize("Matcap.Refused.ModelLayout.Fmt", "This model can't be split: {0}."), ex.Message));
                }
            }
        }
        var okModels = running.Where(m => m.Ok).ToList();
        var edits = okModels.Select(m => new MatcapWriter.ModelEdit(m.Rel, modelGamePath, m.Original, m.Current, m.Cut)).ToList();
        bool cut = okModels.Any(m => m.Cut);

        // ── where the materials are published ──
        var publishedAt = redirects.Where(r => okModels.Any(m => PenumbraModMeta.SameFile(r.File, m.Rel)))
                                   .Select(r => r.GamePath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        // ── the shared textures ──
        Directory.CreateDirectory(env.TempDir);
        SweepTemp(env.TempDir);
        string stamp = Guid.NewGuid().ToString("N")[..8];
        string? bakedImage = null;
        if (choice.Image is { } image)
        {
            bakedImage = Path.Combine(env.TempDir, $"matcap_{stamp}.tex");
            var baked = MatcapImages.Bake(env.Loader, image.Path, bakedImage, env.Compress);
            if (baked.Refusal != null)
                return Prepared.Refuse(baked.Refusal == "not-square" ? "not-square" : "image-unreadable", baked.Refusal == "not-square"
                    ? string.Format(ProteusLoc.Localize("Matcap.Refused.NotSquare.Fmt", "{0} is not square. A matcap is a picture of a sphere filling a square."), image.Name)
                    : string.Format(ProteusLoc.Localize("Matcap.Refused.ImageUnreadable.Fmt", "{0} could not be read as an image."), image.Name));
        }
        else if (choice.Library is { } lib0 && choice.Option is { } option0)
        {
            if (!lib0.Options.Any(o => o.Name == option0))
                return Prepared.Refuse("unknown-option", string.Format(ProteusLoc.Localize("Matcap.Refused.UnknownOption.Fmt", "{0} has no option called {1}."), lib0.Label, option0));
        }
        else return Prepared.Refuse("unknown-image", ProteusLoc.Localize("Parts.Matcap.NeedMatcap", "Pick a matcap first."));

        var maskTemp = Path.Combine(env.TempDir, $"mask_{stamp}.tex");
        if (!env.Loader.WriteTex(MatcapMaterial.Solid(MatcapMaterial.WhiteRgba), 16, 16, maskTemp))
            return Prepared.Refuse("failed", ProteusLoc.Localize("Matcap.Refused.Failed.Fmt", "The matcap could not be written ({0}). Nothing was changed.").Replace("{0}", "mask"));
        var template = env.ReadGameFile(MatcapMaterial.VanillaTemplatePath);
        if (template == null)
            return Prepared.Refuse("no-template", ProteusLoc.Localize("Matcap.Refused.NoTemplate", "The game's reflection material could not be read, so no matcap material could be made."));

        // ── each material: its own normal, alpha threshold, texture and game paths ──
        string? flatTemp = null;
        bool alphaTested = false;
        var plans = new List<MatcapWriter.MaterialPlan>();
        for (int si = 0; si < sources.Count; si++)
        {
            string name = names[si];
            var sourceMtrl = SourceMaterial(modRoot, target, sources[si], redirects, env, modelRel);
            var sh = sourceMtrl != null ? MatcapMaterial.ReadHeader(sourceMtrl) : null;
            var normal = sh?.TextureOf(MatcapMaterial.SamplerNormal);
            float? alpha = sh?.AlphaThreshold;
            alphaTested |= alpha is > 0f || string.Equals(sh?.Shader, "hair.shpk", StringComparison.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(normal))
            {
                if (flatTemp == null)
                {
                    flatTemp = Path.Combine(env.TempDir, $"flatnormal_{stamp}.tex");
                    if (!env.Loader.WriteTex(MatcapMaterial.Solid(MatcapMaterial.FlatNormalRgba), 16, 16, flatTemp))
                        return Prepared.Refuse("failed", ProteusLoc.Localize("Matcap.Refused.Failed.Fmt",
                            "The matcap could not be written ({0}). Nothing was changed.").Replace("{0}", "flat normal"));
                }
                normal = MatcapWriter.FlatNormalGamePath(token);
            }

            var source = new MatcapRecord.SourceRef();
            string matcapPath;
            string? texTemp = null;
            if (choice.Image is { } img)
            {
                matcapPath = MatcapWriter.ImageTexGamePath(token, name);
                texTemp = bakedImage;
                source.Kind = MatcapRecord.SourceRef.KindImage;
                source.Image = img.Name;
                source.ImageSha8 = HairPortPlan.Sha8(File.ReadAllBytes(img.Path));
                source.Tex = new MatcapRecord.FileRef { File = MatcapWriter.ImageTexFile(name), GamePath = matcapPath };
            }
            else
            {
                var lib = choice.Library!;
                matcapPath = lib.Texture;
                source.Kind = MatcapRecord.SourceRef.KindLibrary;
                source.Library = new MatcapRecord.LibraryRef
                {
                    Mod = lib.Mod, Name = lib.Name, Website = lib.Website, Group = lib.Group, Option = choice.Option!, Texture = lib.Texture,
                };
            }

            var gamePaths = MatcapPlan.MaterialGamePaths(target, name, publishedAt, folders);
            if (gamePaths.Count == 0)
                return Prepared.Refuse("no-name", ProteusLoc.Localize("Matcap.Refused.NoName", "Proteus could not tell where the game reads this model's materials."));

            byte[] mtrl;
            try { mtrl = MatcapMaterial.Build(template, normal!, MatcapWriter.MaskGamePath(token), matcapPath, alpha); }
            catch (InvalidDataException ex)
            {
                return Prepared.Refuse("no-template", string.Format(ProteusLoc.Localize("Matcap.Refused.Failed.Fmt", "The matcap could not be written ({0}). Nothing was changed."), ex.Message));
            }
            // A row matcap's matcap copy sits under the part, whole (characterreflection never cuts in its colour passes);
            // the part's own material is cut instead (MatcapRowCut), so the matcap shows through on the row.
            plans.Add(new MatcapWriter.MaterialPlan(name, mtrl, gamePaths, normal!, alpha, source, texTemp,
                                                    meshesOf[name].Where(x => okModels.Any(m => m.Rel == x.Rel)).ToList())
                      {
                          Row = rowCut == null ? null : new MatcapWriter.RowPlan(env.RowOnly, env.RowSub, cutName!, rowCut,
                              cutMeshes.Where(x => okModels.Any(m => m.Rel == x.Rel)).ToList(), cutAlphaSet),
                      });
        }

        var req = new MatcapWriter.ApplyRequest(token, plans, edits, maskTemp, flatTemp);
        return new Prepared(req, "", "", skipped, alphaTested, cut) { RowHoles = rowHoles, CutAlphaSet = cutAlphaSet };
    }

    /// <summary>
    /// The row triangles of <paramref name="parts"/>, read from <paramref name="mdl"/> as given: those whose UV footprint
    /// touches the row in any of the cut's bakes. <paramref name="holes"/>: an alpha-tested bake has an off-row cut-out
    /// inside one of them (it will show the matcap beneath).
    /// </summary>
    private static HashSet<ModelMaterialWriter.TriangleKey> RowTriangles(byte[] mdl, IReadOnlyList<ModelPart> parts,
                                                                       MatcapRowCut.Plan plan, out bool holes)
    {
        var keep = new HashSet<ModelMaterialWriter.TriangleKey>();
        holes = false;
        foreach (var t in ModelMaterialWriter.TriangleUvs(mdl, parts))
        {
            if (!MatcapRowCut.Touches(t, plan.Bakes)) continue;
            keep.Add(t.Key);
            if (!holes && MatcapRowCut.HoleInside(t, plan.Bakes)) holes = true;
        }
        return keep;
    }

    /// <summary>A texture the mod draws at <paramref name="gamePath"/> (its default data first), else the game's, decoded.</summary>
    private static (byte[] Rgba, int W, int H)? LoadTex(string modRoot, List<PenumbraModMeta.Redirect> redirects, Environment env,
                                                        string? gamePath)
    {
        if (string.IsNullOrEmpty(gamePath)) return null;
        var gp = gamePath.Replace('\\', '/');
        byte[]? bytes = null;
        foreach (var r in redirects.Where(r => string.Equals(r.GamePath.Replace('\\', '/'), gp, StringComparison.OrdinalIgnoreCase))
                                   .OrderBy(r => r.Source.Length == 0 ? 0 : 1))
        {
            try { var full = MatcapWriter.Full(modRoot, r.File); if (File.Exists(full)) { bytes = File.ReadAllBytes(full); break; } }
            catch { }
        }
        bytes ??= env.ReadGameFile(gp);
        if (bytes == null) return null;
        var t = env.Loader.LoadTexBytesAsRgba(bytes, gp);
        return t is { } x ? (x.rgba, x.width, x.height) : null;
    }

    /// <summary>What the Matcaps tool's row picker shows for one material: its rows, the rows (and the one sub-row,
    /// when all agree) its index map selects, null when unknown, and the leaf the game loads it by.</summary>
    public sealed record RowInfo(List<ColorTableRowPreset> Rows, HashSet<int>? UsedRows, string? UsedSubRow, string Leaf);

    /// <summary>
    /// <see cref="RowInfo"/> for <paramref name="materialName"/> as the model at <paramref name="modelRel"/> draws it, or
    /// null when the material has no colour table. Reads files and decodes the index map: call off the framework thread.
    /// </summary>
    public static RowInfo? ReadRowInfo(string modRoot, string modelRel, string modelGamePath, string materialName, Environment env)
    {
        if (MatcapPlan.TargetOf(modelGamePath, out _) is not { } target) return null;
        var redirects = PenumbraModMeta.ReadAllRedirects(modRoot);
        if (SourceMaterial(modRoot, target, materialName, redirects, env, modelRel) is not { } mtrl) return null;
        if (GearMaterialWriter.ReadRows(mtrl) is not { } rows) return null;
        var leaf = materialName.Replace('\\', '/');
        leaf = leaf[(leaf.LastIndexOf('/') + 1)..];
        HashSet<int>? used = null;
        string? sub = null;
        if (MatcapMaterial.ReadHeader(mtrl) is { } sh)
        {
            var idPath = sh.TextureOf(ShaderUpgradePlan.SamplerIndex);
            if (!string.IsNullOrEmpty(idPath))
            {
                if (LoadTex(modRoot, redirects, env, idPath) is { } id)
                    (used, sub) = UsedRows(id.Rgba, p => p[0], p => p[1] > 127);
            }
            else if (LoadTex(modRoot, redirects, env, sh.TextureOf(MatcapMaterial.SamplerNormal)) is { } n)
                // No index map: the normal's alpha picks the row (legacy materials), one column only.
                (used, _) = UsedRows(n.Rgba, p => p[3], null);
        }
        return new RowInfo(rows, used, sub, leaf);
    }

    /// <summary>A row (or sub-row column) needs this share of the texels to count as used.</summary>
    internal const double RowUseShare = 0.005;

    /// <summary>
    /// The rows a row map selects, keeping only rows with at least <see cref="RowUseShare"/> of the texels: block
    /// compression blends neighbouring rows' values along every edge between them, which reads as a sprinkle of rows
    /// nothing was painted with (a jacket of rows 1 and 16 shows 8 and 12 at 0.1–0.2%). The sub-row is "A" or "B"
    /// when the used rows' texels all but agree on one column, else null. Null rows when nothing qualifies.
    /// </summary>
    internal static (HashSet<int>? Rows, string? SubRow) UsedRows(byte[] rgba, Func<ReadOnlySpan<byte>, byte> rowByte,
                                                                  Func<ReadOnlySpan<byte>, bool>? isA)
    {
        var counts = new long[17];
        var aCounts = new long[17];
        long total = 0;
        for (int i = 0; i + 3 < rgba.Length; i += 4)
        {
            var px = rgba.AsSpan(i, 4);
            int row = ContentIndexTexture.RowOf(rowByte(px));
            counts[row]++;
            if (isA != null && isA(px)) aCounts[row]++;
            total++;
        }
        if (total == 0) return (null, null);
        long need = (long)Math.Ceiling(total * RowUseShare);
        var rows = new HashSet<int>();
        long inRows = 0, aInRows = 0;
        for (int r = 1; r <= 16; r++)
            if (counts[r] >= need) { rows.Add(r); inRows += counts[r]; aInRows += aCounts[r]; }
        if (rows.Count == 0) return (null, null);
        string? sub = null;
        if (isA != null)
        {
            if (aInRows <= inRows * RowUseShare) sub = "B";
            else if (inRows - aInRows <= inRows * RowUseShare) sub = "A";
        }
        return (rows, sub);
    }

    /// <summary>
    /// Whether parts drawn with material <paramref name="name"/> are alpha-tested (a threshold above 0, or the hair
    /// shader): their cut-outs follow the normal map's alpha under a matcap. The rule Prepare warns by.
    /// </summary>
    internal static bool IsAlphaTested(string modRoot, MatcapPlan.Target target, string name,
                                       List<PenumbraModMeta.Redirect> redirects, Environment env, string? modelRel = null)
    {
        var sh = SourceMaterial(modRoot, target, name, redirects, env, modelRel) is { } m ? MatcapMaterial.ReadHeader(m) : null;
        return sh?.AlphaThreshold is > 0f || string.Equals(sh?.Shader, "hair.shpk", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The parts' current material bytes: the mod's own file at a path the model reads it from; else the mod's file whose
    /// game path ends in its name; else the game's at the first path the model would read it from. The exact path comes
    /// first: a hair port of a race sharing a hair folder reads the author's file at another race's path, where only the
    /// game's own material has the port's name.
    /// </summary>
    private static byte[]? SourceMaterial(string modRoot, MatcapPlan.Target target, string name,
                                          List<PenumbraModMeta.Redirect> redirects, Environment env, string? modelRel = null)
    {
        // At a path the model reads: the model's own option first (options often ship their own material there), then
        // the default data, then any option.
        var own = modelRel == null ? new HashSet<string>(StringComparer.Ordinal)
            : redirects.Where(r => PenumbraModMeta.SameFile(r.File, modelRel)).Select(r => r.Source).ToHashSet(StringComparer.Ordinal);
        int Rank(PenumbraModMeta.Redirect r) => own.Contains(r.Source) ? 0 : r.Source.Length == 0 ? 1 : 2;
        IEnumerable<int>? readFolders = target.Kind == MatcapPlan.Kind.Gear ? [env.WornFolder ?? 1] : null;
        foreach (var gp in MatcapPlan.MaterialGamePaths(target, name, [], readFolders))
            foreach (var r in redirects.Where(r => string.Equals(r.GamePath.Replace('\\', '/'), gp, StringComparison.OrdinalIgnoreCase))
                                       .OrderBy(Rank))
            {
                try
                {
                    var full = MatcapWriter.Full(modRoot, r.File);
                    if (File.Exists(full)) return File.ReadAllBytes(full);
                }
                catch { }
            }
        var file = name.Replace('\\', '/');
        var tail = file.StartsWith('/') ? file : "/" + file[(file.LastIndexOf('/') + 1)..];
        foreach (var r in redirects)
        {
            var gp = r.GamePath.Replace('\\', '/');
            if (!gp.EndsWith(tail, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var full = MatcapWriter.Full(modRoot, r.File);
                if (File.Exists(full)) return File.ReadAllBytes(full);
            }
            catch { }
        }
        foreach (var gp in MatcapPlan.MaterialGamePaths(target, name, [], readFolders))
            if (env.ReadGameFile(gp) is { } bytes) return bytes;
        return null;
    }

    /// <summary>
    /// Prepare a change of an existing matcap to <paramref name="choice"/>: for an image, the new bake; for a library,
    /// the material rebuilt when the new slot reads another path. Same kind only (<see cref="MatcapWriter.Change"/>).
    /// </summary>
    /// <param name="bakedTex">An image already baked (by <see cref="ChangeAll"/>, once for every material): used as is.</param>
    public static (MatcapRecord.SourceRef? Source, byte[]? Material, string? TexTemp, string Code, string Message) PrepareChange(
        string modRoot, string materialName, MatcapChoice choice, Environment env, string? bakedTex = null)
    {
        var record = MatcapRecord.Read(modRoot);
        var entry = record.Materials.FirstOrDefault(m => string.Equals(m.Name, materialName, StringComparison.OrdinalIgnoreCase));
        if (entry == null) return (null, null, null, MatcapWriter.CodeNothing, ProteusLoc.Localize("Matcap.Refused.NoEntry", NoEntryText));
        if (entry.Source.IsLibrary != choice.IsLibrary)
            return (null, null, null, MatcapWriter.CodeCrossKind, ProteusLoc.Localize("Parts.Matcap.ChangeKindTip",
                "A matcap from your images can only change to another image, and a library's to another library option. Undo it to switch kinds."));

        Directory.CreateDirectory(env.TempDir);
        if (choice.Image is { } image)
        {
            var temp = bakedTex ?? Path.Combine(env.TempDir, $"matcap_{Guid.NewGuid():N}.tex");
            if (bakedTex == null && Bake(image, temp, env) is { } refused) return (null, null, null, refused.Code, refused.Message);
            return (new MatcapRecord.SourceRef
            {
                Kind = MatcapRecord.SourceRef.KindImage, Image = image.Name, ImageSha8 = HairPortPlan.Sha8(File.ReadAllBytes(image.Path)),
            }, null, temp, "", "");
        }
        var lib = choice.Library!;
        if (choice.Option is not { } option || !lib.Options.Any(o => o.Name == option))
            return (null, null, null, "unknown-option", string.Format(ProteusLoc.Localize("Matcap.Refused.UnknownOption.Fmt", "{0} has no option called {1}."), lib.Label, choice.Option));
        var src = new MatcapRecord.SourceRef
        {
            Kind = MatcapRecord.SourceRef.KindLibrary,
            Library = new MatcapRecord.LibraryRef { Mod = lib.Mod, Name = lib.Name, Website = lib.Website, Group = lib.Group, Option = option, Texture = lib.Texture },
        };
        byte[]? mtrl = null;
        if (!string.Equals(entry.Source.Library?.Texture, lib.Texture, StringComparison.OrdinalIgnoreCase))
        {
            var template = env.ReadGameFile(MatcapMaterial.VanillaTemplatePath);
            if (template == null)
                return (null, null, null, "no-template", ProteusLoc.Localize("Matcap.Refused.NoTemplate", "The game's reflection material could not be read, so no matcap material could be made."));
            mtrl = MatcapMaterial.Build(template, entry.Normal, MatcapWriter.MaskGamePath(record.Token), lib.Texture, entry.AlphaThreshold);
        }
        return (src, mtrl, null, "", "");
    }

    /// <summary>Bake <paramref name="image"/> to <paramref name="temp"/>, or the refusal.</summary>
    private static (string Code, string Message)? Bake(MatcapImage image, string temp, Environment env)
    {
        var baked = MatcapImages.Bake(env.Loader, image.Path, temp, env.Compress);
        if (baked.Refusal == null) return null;
        return baked.Refusal == "not-square"
            ? ("not-square", string.Format(ProteusLoc.Localize("Matcap.Refused.NotSquare.Fmt", "{0} is not square. A matcap is a picture of a sphere filling a square."), image.Name))
            : ("image-unreadable", string.Format(ProteusLoc.Localize("Matcap.Refused.ImageUnreadable.Fmt", "{0} could not be read as an image."), image.Name));
    }

    /// <summary>
    /// Apply on parts that already have a matcap: each of <paramref name="names"/> changes in place to
    /// <paramref name="choice"/> (<see cref="PrepareChange"/> + <see cref="MatcapWriter.Change"/>, the whole material).
    /// Everything that would refuse is checked first, with nothing written: the record, every entry, its kind, and the
    /// files a change rewrites still as Proteus wrote them (<see cref="MatcapWriter"/>'s own rule). An image is baked once
    /// for every material. A failure after an earlier material changed still returns Ok, with a line naming what was not
    /// changed, so the caller selects the library option and reloads for the ones that did.
    /// </summary>
    public static MatcapWriter.Outcome ChangeAll(string modRoot, IReadOnlyList<string> names, MatcapChoice choice, Environment env)
    {
        var record = MatcapRecord.Read(modRoot);
        if (record.Unreadable)
            return MatcapWriter.Outcome.Refuse(MatcapWriter.CodeUnreadable, ProteusLoc.Localize("Matcap.Refused.RecordUnreadable",
                "Proteus's record of this mod's matcaps (Proteus/matcaps.json) could not be read, so nothing was changed."));
        if (names.Count == 0)
            return MatcapWriter.Outcome.Refuse(MatcapWriter.CodeNothing, ProteusLoc.Localize("Matcap.Refused.NoEntry", NoEntryText));
        foreach (var name in names)
        {
            var entry = record.Materials.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
            if (entry == null)
                return MatcapWriter.Outcome.Refuse(MatcapWriter.CodeNothing, ProteusLoc.Localize("Matcap.Refused.NoEntry", NoEntryText));
            if (entry.Source.IsLibrary != choice.IsLibrary)
                return MatcapWriter.Outcome.Refuse(MatcapWriter.CodeCrossKind, ProteusLoc.Localize("Parts.Matcap.ChangeKindTip",
                    "A matcap from your images can only change to another image, and a library's to another library option. Undo it to switch kinds."));
            // The file the change rewrites: an image's texture, or a library's material when the slot path moves.
            (string File, string Sha8)? rewritten = choice.Library is { } lib
                ? (!string.Equals(entry.Source.Library?.Texture, lib.Texture, StringComparison.OrdinalIgnoreCase) ? (entry.File, entry.Sha8) : null)
                : entry.Source.Tex is { } tex ? (tex.File, tex.Sha8) : null;
            if (rewritten is { } rw && EditedSinceWritten(modRoot, rw.File, rw.Sha8))
                return MatcapWriter.Outcome.Refuse(MatcapWriter.CodeEdited, string.Format(ProteusLoc.Localize("Matcap.Refused.HandEdited.Fmt",
                    "{0} was edited by hand since Proteus wrote it, so it was left alone."), rw.File));
        }

        string? baked = null;
        try
        {
            if (choice.Image is { } image)
            {
                Directory.CreateDirectory(env.TempDir);
                SweepTemp(env.TempDir);
                baked = Path.Combine(env.TempDir, $"matcap_{Guid.NewGuid():N}.tex");
                if (Bake(image, baked, env) is { } refused) return MatcapWriter.Outcome.Refuse(refused.Code, refused.Message);
            }

            var changed = new List<string>();
            var lines = new List<string>();
            var notChanged = new List<string>();
            MatcapWriter.Outcome? first = null;
            foreach (var name in names)
            {
                MatcapWriter.Outcome outcome;
                string? reason = null;   // the short why for the "not changed" line (never "Nothing was changed")
                try
                {
                    BeforeChange?.Invoke(name);
                    var (src, mtrl, temp, code, msg) = PrepareChange(modRoot, name, choice, env, baked);
                    outcome = src == null ? MatcapWriter.Outcome.Refuse(code, msg) : MatcapWriter.Change(modRoot, name, src, mtrl, temp);
                }
                catch (Exception ex)
                {
                    reason = ex.Message;
                    outcome = MatcapWriter.Outcome.Refuse(MatcapWriter.CodeFailed, string.Format(ProteusLoc.Localize("Matcap.Refused.Failed.Fmt",
                        "The matcap could not be written ({0}). Nothing was changed."), ex.Message));
                }
                if (outcome.Ok)
                {
                    lines.Add(outcome.Message);
                    changed.AddRange(outcome.Files);
                }
                else
                {
                    first ??= outcome;
                    notChanged.Add($"{MatcapWriter.StemOf(name)} ({reason ?? outcome.Message})");
                }
            }
            if (lines.Count == 0) return first!;
            if (notChanged.Count > 0)
                lines.Add(string.Format(ProteusLoc.Localize("Parts.Matcap.ChangePartial.Fmt", ChangePartialText), string.Join("; ", notChanged)));
            return MatcapWriter.Outcome.Done(string.Join("\n", lines), changed);
        }
        finally
        {
            if (baked != null) try { File.Delete(baked); } catch { }
        }
    }

    /// <summary>
    /// <see cref="MatcapWriter"/>'s hand-edit rule, case for case: a missing file or an unrecorded hash is not an edit;
    /// a file that can't be read counts as one.
    /// </summary>
    private static bool EditedSinceWritten(string modRoot, string rel, string sha8)
    {
        var full = MatcapWriter.Full(modRoot, rel);
        try { return File.Exists(full) && sha8.Length > 0 && HairPortPlan.Sha8(File.ReadAllBytes(full)) != sha8; }
        catch { return true; }
    }
}
