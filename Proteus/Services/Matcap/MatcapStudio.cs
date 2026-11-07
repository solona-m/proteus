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
    public sealed record Environment(Func<string, byte[]?> ReadGameFile, TextureLoader Loader, bool Compress, string TempDir,
                                     int? WornFolder = null);

    /// <summary>A prepared apply, or why not.</summary>
    public sealed record Prepared(MatcapWriter.ApplyRequest? Request, string Code, string Message,
                                  IReadOnlyList<string> SiblingsSkipped, bool AlphaTested, bool Lod0Cut)
    {
        public bool Ok => Request != null;
        public static Prepared Refuse(string code, string message) => new(null, code, message, [], false, false);
    }


    /// <summary>
    /// Prepare a matcap on <paramref name="chosen"/> — parts read from <paramref name="modelBytes"/>, the file at
    /// <paramref name="modelRel"/> as it is now — and on every sibling file at the same game path whose parts line up.
    /// </summary>
    public static Prepared Prepare(string modRoot, string modelRel, string modelGamePath, byte[] modelBytes,
                                   IReadOnlyList<ModelPart> chosen, MatcapChoice choice, Environment env)
    {
        if (MatcapPlan.TargetOf(modelGamePath, out _) is not { } target)
            return Prepared.Refuse("not-target", ProteusLoc.Localize("Matcap.Refused.NotTarget",
                "Matcaps go on gear, accessories, hair and ears or horns; this model is none of those."));
        if (MatcapPlan.Regenerated(modRoot, modelRel) is { } regen)
            return Prepared.Refuse("regenerated-model", string.Format(ProteusLoc.Localize("Matcap.Refused.Regenerated.Fmt",
                "This model is made by Proteus from another one ({0}), and remaking it would lose the matcap. Put the matcap on the model it was made from, then remake it."), regen));
        if (chosen.Count == 0) return Prepared.Refuse("unknown-part", ProteusLoc.Localize("Parts.Matcap.NeedParts", "Tick the parts that should take the matcap."));
        if (chosen.Any(MatcapPlan.IsSkin)) return Prepared.Refuse("skin-part", ProteusLoc.Localize("Matcap.Refused.Skin", "Skin can't take a matcap."));
        if (chosen.Any(MatcapPlan.IsOnMatcap))
            return Prepared.Refuse("already-matcap", ProteusLoc.Localize("Matcap.Refused.AlreadyMatcap",
                "Some of those parts already have a matcap. Use \"Use the chosen matcap\" to change it."));

        var record = MatcapRecord.Read(modRoot);
        if (record.Unreadable)
            return Prepared.Refuse(MatcapWriter.CodeUnreadable, ProteusLoc.Localize("Matcap.Refused.RecordUnreadable",
                "Proteus's record of this mod's matcaps (Proteus/matcaps.json) could not be read, so nothing was changed."));
        var token = record.Token.Length > 0 ? record.Token : MatcapPlan.TokenFor(Path.GetFileName(modRoot.TrimEnd('\\', '/')));
        var redirects = PenumbraModMeta.ReadAllRedirects(modRoot);

        // ── the parts' own material: its normal and alpha threshold ──
        var sourceName = chosen[0].Material;
        var sourceMtrl = SourceMaterial(modRoot, target, sourceName, redirects, env);
        var sh = sourceMtrl != null ? MatcapMaterial.ReadHeader(sourceMtrl) : null;
        var normal = sh?.TextureOf(MatcapMaterial.SamplerNormal);
        float? alpha = sh?.AlphaThreshold;
        bool alphaTested = alpha is > 0f || string.Equals(sh?.Shader, "hair.shpk", StringComparison.OrdinalIgnoreCase);

        // ── siblings: every file at this game path whose parts line up ──
        var models = new List<(string Rel, byte[] Bytes, IReadOnlyList<ModelPart> Parts)> { (modelRel, modelBytes, chosen) };
        var skipped = new List<string>();
        foreach (var sib in HatCompatService.SiblingFiles(modRoot, modelGamePath, modelRel))
        {
            if (PenumbraModMeta.SameFile(sib, modelRel)) continue;
            if (MatcapPlan.Regenerated(modRoot, sib) != null) { skipped.Add(sib); continue; }
            byte[] bytes;
            try { bytes = File.ReadAllBytes(MatcapWriter.Full(modRoot, sib)); } catch { skipped.Add(sib); continue; }
            var parts = ModelPartReader.Read(bytes)?.Parts;
            if (parts == null || !MatcapPlan.LinesUp(chosen, parts)) { skipped.Add(sib); continue; }
            models.Add((sib, bytes, chosen.Select(c => parts.First(p => PartClaims.LineUp(c, p))).ToList()));
        }

        // ── the name, free everywhere it could clash ──
        var taken = new List<string>();
        foreach (var (_, bytes, _) in models)
            try { taken.AddRange(SecondSkinWriter.MaterialNames(bytes)); } catch { }
        taken.AddRange(record.Materials.Select(m => m.Name));
        taken.AddRange(redirects.Select(r => r.GamePath));
        var name = MatcapPlan.NewName(sourceName, token, taken);

        // ── the splits ──
        var edits = new List<MatcapWriter.ModelEdit>();
        bool cut = false;
        foreach (var (rel, bytes, parts) in models)
        {
            try
            {
                var r = ModelMaterialWriter.MoveToMaterial(bytes, parts, name);
                edits.Add(new MatcapWriter.ModelEdit(rel, modelGamePath, bytes, r.Model, r.Lod0Cut, r.Meshes));
                cut |= r.Lod0Cut;
            }
            catch (ModelMaterialWriter.Refused ex)
            {
                if (rel != modelRel) { skipped.Add(rel); continue; }
                return Prepared.Refuse(ex.Code, ex.Code == ModelMaterialWriter.CodeMaterialCap
                    ? string.Format(ProteusLoc.Localize("Matcap.Refused.MaterialCap.Fmt", "This model can't take another material: {0}."), ex.Message)
                    : string.Format(ProteusLoc.Localize("Matcap.Refused.ModelLayout.Fmt", "This model can't be split: {0}."), ex.Message));
            }
        }

        // ── where the material is published ──
        var publishedAt = redirects.Where(r => models.Any(m => PenumbraModMeta.SameFile(r.File, m.Rel)))
                                   .Select(r => r.GamePath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        IEnumerable<int>? folders = null;
        if (target.Kind == MatcapPlan.Kind.Gear)
        {
            var item = target.Item!.Value;
            folders = MatcapPlan.GearFolders(item, ImcEntrySource.GameEntries(env.ReadGameFile, item.SetTag, item.SlotLabel),
                                             MatcapPlan.ModImcEntries(modRoot, item), redirects.Select(r => r.GamePath), env.WornFolder);
        }
        var gamePaths = MatcapPlan.MaterialGamePaths(target, name, publishedAt, folders);
        if (gamePaths.Count == 0)
            return Prepared.Refuse("no-name", ProteusLoc.Localize("Matcap.Refused.NoName", "Proteus could not tell where the game reads this model's materials."));

        // ── the textures ──
        Directory.CreateDirectory(env.TempDir);
        string stamp = Guid.NewGuid().ToString("N")[..8];
        var source = new MatcapRecord.SourceRef();
        string matcapPath;
        string? imageTemp = null;
        if (choice.Image is { } image)
        {
            imageTemp = Path.Combine(env.TempDir, $"matcap_{stamp}.tex");
            var baked = MatcapImages.Bake(env.Loader, image.Path, imageTemp, env.Compress);
            if (baked.Refusal != null)
                return Prepared.Refuse(baked.Refusal == "not-square" ? "not-square" : "image-unreadable", baked.Refusal == "not-square"
                    ? string.Format(ProteusLoc.Localize("Matcap.Refused.NotSquare.Fmt", "{0} is not square. A matcap is a picture of a sphere filling a square."), image.Name)
                    : string.Format(ProteusLoc.Localize("Matcap.Refused.ImageUnreadable.Fmt", "{0} could not be read as an image."), image.Name));
            matcapPath = MatcapWriter.ImageTexGamePath(token, name);
            source.Kind = MatcapRecord.SourceRef.KindImage;
            source.Image = image.Name;
            source.ImageSha8 = HairPortPlan.Sha8(File.ReadAllBytes(image.Path));
            source.Tex = new MatcapRecord.FileRef { File = MatcapWriter.ImageTexFile(name), GamePath = matcapPath };
        }
        else if (choice.Library is { } lib && choice.Option is { } option)
        {
            if (!lib.Options.Any(o => o.Name == option))
                return Prepared.Refuse("unknown-option", string.Format(ProteusLoc.Localize("Matcap.Refused.UnknownOption.Fmt", "{0} has no option called {1}."), lib.Label, option));
            matcapPath = lib.Texture;
            source.Kind = MatcapRecord.SourceRef.KindLibrary;
            source.Library = new MatcapRecord.LibraryRef
            {
                Mod = lib.Mod, Name = lib.Name, Website = lib.Website, Group = lib.Group, Option = option, Texture = lib.Texture,
            };
        }
        else return Prepared.Refuse("unknown-image", ProteusLoc.Localize("Parts.Matcap.NeedMatcap", "Pick a matcap first."));

        var maskTemp = Path.Combine(env.TempDir, $"mask_{stamp}.tex");
        if (!env.Loader.WriteTex(MatcapMaterial.Solid(MatcapMaterial.WhiteRgba), 16, 16, maskTemp))
            return Prepared.Refuse("failed", ProteusLoc.Localize("Matcap.Refused.Failed.Fmt", "The matcap could not be written ({0}). Nothing was changed.").Replace("{0}", "mask"));
        string? flatTemp = null;
        if (string.IsNullOrEmpty(normal))
        {
            flatTemp = Path.Combine(env.TempDir, $"flatnormal_{stamp}.tex");
            env.Loader.WriteTex(MatcapMaterial.Solid(MatcapMaterial.FlatNormalRgba), 16, 16, flatTemp);
            normal = MatcapWriter.FlatNormalGamePath(token);
        }

        // ── the material ──
        var template = env.ReadGameFile(MatcapMaterial.VanillaTemplatePath);
        if (template == null)
            return Prepared.Refuse("no-template", ProteusLoc.Localize("Matcap.Refused.NoTemplate", "The game's reflection material could not be read, so no matcap material could be made."));
        byte[] mtrl;
        try { mtrl = MatcapMaterial.Build(template, normal!, MatcapWriter.MaskGamePath(token), matcapPath, alpha); }
        catch (InvalidDataException ex)
        {
            return Prepared.Refuse("no-template", string.Format(ProteusLoc.Localize("Matcap.Refused.Failed.Fmt", "The matcap could not be written ({0}). Nothing was changed."), ex.Message));
        }

        var req = new MatcapWriter.ApplyRequest(token, name, mtrl, gamePaths, normal!, alpha, edits, source, imageTemp, maskTemp, flatTemp);
        return new Prepared(req, "", "", skipped, alphaTested, cut);
    }

    /// <summary>
    /// The parts' current material bytes: the mod's own file for it (a redirect whose game path ends in its name), else
    /// the game's at the first path the model would read it from.
    /// </summary>
    private static byte[]? SourceMaterial(string modRoot, MatcapPlan.Target target, string name,
                                          List<PenumbraModMeta.Redirect> redirects, Environment env)
    {
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
        IEnumerable<int>? folders = target.Kind == MatcapPlan.Kind.Gear ? [env.WornFolder ?? 1] : null;
        foreach (var gp in MatcapPlan.MaterialGamePaths(target, name, [], folders))
            if (env.ReadGameFile(gp) is { } bytes) return bytes;
        return null;
    }

    /// <summary>
    /// Prepare a change of an existing matcap to <paramref name="choice"/>: for an image, the new bake; for a library,
    /// the material rebuilt when the new slot reads another path. Same kind only (<see cref="MatcapWriter.Change"/>).
    /// </summary>
    public static (MatcapRecord.SourceRef? Source, byte[]? Material, string? TexTemp, string Code, string Message) PrepareChange(
        string modRoot, string materialName, MatcapChoice choice, Environment env)
    {
        var record = MatcapRecord.Read(modRoot);
        var entry = record.Materials.FirstOrDefault(m => string.Equals(m.Name, materialName, StringComparison.OrdinalIgnoreCase));
        if (entry == null) return (null, null, null, MatcapWriter.CodeNothing, ProteusLoc.Localize("Matcap.Refused.NoEntry", "That matcap is not in this mod's record."));
        if (entry.Source.IsLibrary != choice.IsLibrary)
            return (null, null, null, MatcapWriter.CodeCrossKind, ProteusLoc.Localize("Parts.Matcap.ChangeKindTip",
                "A matcap from your images can only change to another image, and a library's to another library option. Undo it to switch kinds."));

        Directory.CreateDirectory(env.TempDir);
        if (choice.Image is { } image)
        {
            var temp = Path.Combine(env.TempDir, $"matcap_{Guid.NewGuid():N}.tex");
            var baked = MatcapImages.Bake(env.Loader, image.Path, temp, env.Compress);
            if (baked.Refusal != null)
                return (null, null, null, baked.Refusal == "not-square" ? "not-square" : "image-unreadable", baked.Refusal == "not-square"
                    ? string.Format(ProteusLoc.Localize("Matcap.Refused.NotSquare.Fmt", "{0} is not square. A matcap is a picture of a sphere filling a square."), image.Name)
                    : string.Format(ProteusLoc.Localize("Matcap.Refused.ImageUnreadable.Fmt", "{0} could not be read as an image."), image.Name));
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
}
