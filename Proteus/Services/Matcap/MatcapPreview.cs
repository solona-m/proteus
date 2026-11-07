using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Proteus.Services.Matcap;

/// <summary>
/// The Matcaps tool's click preview, built off the framework thread: what an Apply of <see cref="Input.Choice"/> on the
/// ticked parts would put on the character, as files for <see cref="LiveBrushPreview.PushFiles"/>, with nothing written
/// into any mod. A new matcap is <see cref="MatcapStudio.Prepare"/> on the open model only (no siblings, an uncompressed
/// bake); a change re-emits each affected material as it is or rebuilt, and the new texture. Every file goes out as bytes,
/// a library option's texture too (copied, never pointed at in the library's own folder, which sync plugins read), so the
/// preview's every file is private and fresh. Refuses as Apply would.
/// </summary>
internal static class MatcapPreview
{
    /// <param name="DrawnAt">The game paths the open model is drawn at (where the brush preview redirects it).</param>
    /// <param name="ModsRoot">Penumbra's mod folder, where a library option's texture is read from.</param>
    public sealed record Input(string ModRoot, string ModelRel, string ModelGamePath, IReadOnlyCollection<string> DrawnAt,
                               byte[] ModelBytes, IReadOnlyList<ModelPart> Ticked, MatcapChoice Choice,
                               MatcapStudio.Environment Env, string ModsRoot);

    /// <summary>The files to push, or why not (<see cref="Code"/> empty when built).</summary>
    public sealed record Built(IReadOnlyList<LiveBrushPreview.PreviewFile> Files, string Code, string Message)
    {
        public bool Ok => Code.Length == 0;
        public static Built Refuse(string code, string message) => new([], code, message);
    }

    public const string CodeNotGear = "not-gear", CodeLibraryMissing = "library-missing";

    public static Built Build(Input input)
    {
        var root = input.ModRoot.TrimEnd('\\', '/');
        if (string.Equals(Path.GetFileName(root), SidecarDiscoveryService.ManagedModDir, StringComparison.OrdinalIgnoreCase))
            return Built.Refuse(MatcapWriter.CodeManaged, ProteusLoc.Localize("Matcap.Refused.ImportedPiece",
                "This piece belongs to an imported pack, which Proteus rebuilds itself. Put the matcap on the pack's own mod instead."));
        if (PenumbraModMeta.IsLegacyFolder(input.ModRoot))
            return Built.Refuse(MatcapWriter.CodeLegacy, ProteusLoc.Localize("Matcap.Refused.Legacy",
                "This mod is still in Penumbra's old folder layout. Open it in Penumbra once so it is updated, then try again."));
        if (MatcapPlan.TargetOf(input.ModelGamePath, out _) is { } target && target.Kind != MatcapPlan.Kind.Gear)
            return Built.Refuse(CodeNotGear, Localization.Strings.Matcap.PreviewGearOnly);
        if (input.DrawnAt.Count == 0)
            return Built.Refuse(CodeNotGear, Localization.Strings.Matcap.PreviewGearOnly);

        // A library option's texture, read once: the preview redirects the slot to a private copy of it.
        byte[]? optionBytes = null;
        if (input.Choice.Library is { } lib)
        {
            var option = lib.Options.FirstOrDefault(o => o.Name == input.Choice.Option);
            var full = option == null ? null
                : Path.Combine(input.ModsRoot, lib.Mod, option.File.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar));
            try { optionBytes = full != null && File.Exists(full) ? File.ReadAllBytes(full) : null; } catch { optionBytes = null; }
            if (optionBytes == null)
                return Built.Refuse(CodeLibraryMissing, string.Format(Localization.Strings.Matcap.LibraryMissingFmt, lib.Name));
        }

        var env = input.Env with { Compress = false };
        return MatcapPlan.KindOfApply(input.Ticked) switch
        {
            MatcapPlan.ApplyKind.Mixed => Built.Refuse("already-matcap", Localization.Strings.Matcap.AlreadyMatcap),
            MatcapPlan.ApplyKind.Change => BuildChange(input, env, optionBytes),
            _ => BuildNew(input, env, optionBytes),
        };
    }

    private static Built BuildNew(Input input, MatcapStudio.Environment env, byte[]? optionBytes)
    {
        var prepared = MatcapStudio.Prepare(input.ModRoot, input.ModelRel, input.ModelGamePath, input.ModelBytes, input.Ticked,
                                            input.Choice, env, withSiblings: false);
        if (!prepared.Ok) return Built.Refuse(prepared.Code, prepared.Message);
        var req = prepared.Request!;
        var temps = new List<string> { req.MaskTemp };
        if (req.FlatNormalTemp != null) temps.Add(req.FlatNormalTemp);
        temps.AddRange(req.Materials.Select(m => m.ImageTexTemp).OfType<string>());
        try
        {
            var files = new List<LiveBrushPreview.PreviewFile>
            {
                new(input.DrawnAt, ".mdl", req.Models[0].NewBytes),
            };
            foreach (var m in req.Materials)
            {
                files.Add(new(m.GamePaths, ".mtrl", m.Bytes));
                if (m.ImageTexTemp != null && m.Source.Tex is { } tex)
                    files.Add(new([tex.GamePath], ".tex", File.ReadAllBytes(m.ImageTexTemp)));
            }
            files.Add(new([MatcapWriter.MaskGamePath(req.Token)], ".tex", File.ReadAllBytes(req.MaskTemp)));
            if (req.FlatNormalTemp != null)
                files.Add(new([MatcapWriter.FlatNormalGamePath(req.Token)], ".tex", File.ReadAllBytes(req.FlatNormalTemp)));
            if (input.Choice.Library is { } lib && optionBytes != null)
                files.Add(new([lib.Texture], ".tex", optionBytes));
            return new Built(files, "", "");
        }
        finally
        {
            foreach (var t in temps.Distinct(StringComparer.OrdinalIgnoreCase)) TryDelete(t);
        }
    }

    private static Built BuildChange(Input input, MatcapStudio.Environment env, byte[]? optionBytes)
    {
        var record = MatcapRecord.Read(input.ModRoot);
        if (record.Unreadable)
            return Built.Refuse(MatcapWriter.CodeUnreadable, ProteusLoc.Localize("Matcap.Refused.RecordUnreadable",
                "Proteus's record of this mod's matcaps (Proteus/matcaps.json) could not be read, so nothing was changed."));
        string? baked = null;
        try
        {
            if (input.Choice.Image is { } image)
            {
                Directory.CreateDirectory(env.TempDir);
                baked = Path.Combine(env.TempDir, $"preview_{Guid.NewGuid():N}.tex");
                var b = MatcapImages.Bake(env.Loader, image.Path, baked, compress: false);
                if (b.Refusal != null)
                    return Built.Refuse(b.Refusal == "not-square" ? "not-square" : "image-unreadable", b.Refusal == "not-square"
                        ? string.Format(ProteusLoc.Localize("Matcap.Refused.NotSquare.Fmt", "{0} is not square. A matcap is a picture of a sphere filling a square."), image.Name)
                        : string.Format(ProteusLoc.Localize("Matcap.Refused.ImageUnreadable.Fmt", "{0} could not be read as an image."), image.Name));
            }

            // A copy of the model at a fresh path, so the gear reloads and asks for its materials again.
            var files = new List<LiveBrushPreview.PreviewFile> { new(input.DrawnAt, ".mdl", input.ModelBytes) };
            var texPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in MatcapPlan.ChangeNames(input.Ticked))
            {
                var entry = record.Materials.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
                var (src, mtrl, _, code, msg) = MatcapStudio.PrepareChange(input.ModRoot, name, input.Choice, env, baked);
                if (src == null || entry == null) return Built.Refuse(code.Length > 0 ? code : MatcapWriter.CodeNothing, msg);
                // Re-emitted even unchanged: only a path the game never loaded is read again.
                byte[] material;
                try { material = mtrl ?? File.ReadAllBytes(MatcapWriter.Full(input.ModRoot, entry.File)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return Built.Refuse(MatcapWriter.CodeFailed, string.Format(Localization.Strings.Matcap.PreviewFailedFmt, ex.Message));
                }
                files.Add(new(entry.GamePaths, ".mtrl", material));
                if (baked != null && entry.Source.Tex is { } tex) texPaths.Add(tex.GamePath);
            }
            if (baked != null && texPaths.Count > 0) files.Add(new(texPaths, ".tex", File.ReadAllBytes(baked)));
            if (input.Choice.Library is { } lib && optionBytes != null) files.Add(new([lib.Texture], ".tex", optionBytes));
            return new Built(files, "", "");
        }
        finally
        {
            if (baked != null) TryDelete(baked);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* swept with the temp folder */ }
    }
}
