using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Proteus.Services.Matcap;

/// <summary>
/// <c>Proteus/matcaps.json</c> inside a mod: every matcap Proteus wrote there — which models it split, which materials,
/// textures and redirects it added, and where each matcap comes from. It travels with the mod (exports included), so an
/// undo works wherever the mod goes. Material entries are keyed by material name and mesh, never by part label: labels
/// shift after a split.
/// </summary>
internal sealed class MatcapRecord
{
    [JsonPropertyName("Version")] public int Version { get; set; } = 1;

    /// <summary>The mod's token in material names (<see cref="MatcapPlan.TokenFor"/>), kept across renames.</summary>
    [JsonPropertyName("Token")] public string Token { get; set; } = "";

    [JsonPropertyName("Models")] public List<ModelEntry> Models { get; set; } = [];

    [JsonPropertyName("Materials")] public List<MaterialEntry> Materials { get; set; } = [];

    [JsonPropertyName("Shared")] public SharedFiles Shared { get; set; } = new();

    /// <summary>The record file is there but could not be read: nothing may be written over it.</summary>
    [JsonIgnore] public bool Unreadable { get; private set; }

    public sealed class ModelEntry
    {
        /// <summary>The model file, mod-relative.</summary>
        [JsonPropertyName("File")] public string File { get; set; } = "";
        [JsonPropertyName("GamePath")] public string GamePath { get; set; } = "";
        /// <summary>The hash of every write of ours, in order: a file matching any of them is still ours to restore.</summary>
        [JsonPropertyName("WrittenSha8s")] public List<string> WrittenSha8s { get; set; } = [];
        [JsonPropertyName("Lod0Cut")] public bool Lod0Cut { get; set; }
    }

    public sealed class MeshRef
    {
        [JsonPropertyName("Model")] public string Model { get; set; } = "";
        /// <summary>The mesh that draws the material (a copy, or the source when it moved whole).</summary>
        [JsonPropertyName("Mesh")] public int Mesh { get; set; }
        /// <summary>The source mesh its triangles came from.</summary>
        [JsonPropertyName("From")] public int From { get; set; }
    }

    public sealed class FileRef
    {
        [JsonPropertyName("File")] public string File { get; set; } = "";
        [JsonPropertyName("GamePath")] public string GamePath { get; set; } = "";
        [JsonPropertyName("Sha8")] public string Sha8 { get; set; } = "";
    }

    public sealed class LibraryRef
    {
        [JsonPropertyName("Mod")] public string Mod { get; set; } = "";
        [JsonPropertyName("Name")] public string Name { get; set; } = "";
        [JsonPropertyName("Website")] public string Website { get; set; } = "";
        [JsonPropertyName("Group")] public string Group { get; set; } = "";
        [JsonPropertyName("Option")] public string Option { get; set; } = "";
        [JsonPropertyName("Texture")] public string Texture { get; set; } = "";
    }

    public sealed class SourceRef
    {
        public const string KindImage = "image", KindLibrary = "library";

        [JsonPropertyName("Kind")] public string Kind { get; set; } = KindImage;
        [JsonPropertyName("Image")] public string? Image { get; set; }
        [JsonPropertyName("ImageSha8")] public string? ImageSha8 { get; set; }
        /// <summary>The baked texture of an image matcap.</summary>
        [JsonPropertyName("Tex")] public FileRef? Tex { get; set; }
        [JsonPropertyName("Library")] public LibraryRef? Library { get; set; }

        [JsonIgnore] public bool IsLibrary => Kind == KindLibrary;
    }

    public sealed class MaterialEntry
    {
        /// <summary>As the model names it (<c>/mt_…_mcap3fa21c1.mtrl</c>).</summary>
        [JsonPropertyName("Name")] public string Name { get; set; } = "";
        [JsonPropertyName("File")] public string File { get; set; } = "";
        [JsonPropertyName("Sha8")] public string Sha8 { get; set; } = "";
        [JsonPropertyName("GamePaths")] public List<string> GamePaths { get; set; } = [];
        [JsonPropertyName("Meshes")] public List<MeshRef> Meshes { get; set; } = [];
        /// <summary>The normal map the material reads (the parts' own, or the generated flat one).</summary>
        [JsonPropertyName("Normal")] public string Normal { get; set; } = "";
        [JsonPropertyName("AlphaThreshold")] public float? AlphaThreshold { get; set; }
        /// <summary>Written before the files and cleared after: a crash leaves it set, and Undo cleans it up.</summary>
        [JsonPropertyName("Pending")] public bool Pending { get; set; }
        [JsonPropertyName("Source")] public SourceRef Source { get; set; } = new();
        /// <summary>A row matcap's cut: null for a whole-part matcap (every entry before row matcaps existed).</summary>
        [JsonPropertyName("Row")] public RowRef? Row { get; set; }
    }

    /// <summary>
    /// A row matcap's cut: the row and sub-row, the cut copy name the row triangles now draw with, the cut files (one per
    /// distinct source material, each with every redirect it was published at and the source it was cut from), the cut
    /// normals, and per variant folder which source the cut followed — what Undo removes and Detect checks.
    /// </summary>
    public sealed class RowRef
    {
        public const string SubBoth = "both", SubA = "a", SubB = "b";

        [JsonPropertyName("Row")] public int Row { get; set; }
        [JsonPropertyName("Sub")] public string Sub { get; set; } = SubBoth;
        /// <summary>The cut copy's name as the model names it (<c>/mt_…_mcut3fa21c1.mtrl</c>).</summary>
        [JsonPropertyName("Cut")] public string Cut { get; set; } = "";
        [JsonPropertyName("CutMeshes")] public List<MeshRef> CutMeshes { get; set; } = [];
        /// <summary>The cut meshes' vertex alpha was raised to 1 (every source had no alpha threshold).</summary>
        [JsonPropertyName("CutAlphaSet")] public bool CutAlphaSet { get; set; }
        [JsonPropertyName("Files")] public List<RowFileRef> Files { get; set; } = [];
        [JsonPropertyName("Normals")] public List<FileRef> Normals { get; set; } = [];
        [JsonPropertyName("Fingerprint")] public RowFingerprint Fingerprint { get; set; } = new();
    }

    /// <summary>One cut copy of a source material: where it is, where it is redirected, and what it was cut from.</summary>
    public sealed class RowFileRef
    {
        [JsonPropertyName("File")] public string File { get; set; } = "";
        [JsonPropertyName("Sha8")] public string Sha8 { get; set; } = "";
        [JsonPropertyName("ThresholdZero")] public bool ThresholdZero { get; set; }
        [JsonPropertyName("Source")] public RowSource Source { get; set; } = new();
        [JsonPropertyName("Normal")] public RowSource Normal { get; set; } = new();
        [JsonPropertyName("Index")] public RowSource? Index { get; set; }
        [JsonPropertyName("Redirects")] public List<RowRedirect> Redirects { get; set; } = [];
    }

    /// <summary>
    /// A source file: a mod file (<see cref="File"/>, with the container that redirected it) or a game file
    /// (<see cref="Game"/>), the game path it was read at, and its hash, size and write time when cut.
    /// </summary>
    public sealed class RowSource
    {
        [JsonPropertyName("GamePath")] public string GamePath { get; set; } = "";
        [JsonPropertyName("Group")] public string? Group { get; set; }
        [JsonPropertyName("Option")] public string? Option { get; set; }
        [JsonPropertyName("Index")] public int? Index { get; set; }
        [JsonPropertyName("File")] public string? File { get; set; }
        [JsonPropertyName("Game")] public bool Game { get; set; }
        [JsonPropertyName("Sha8")] public string Sha8 { get; set; } = "";
        [JsonPropertyName("Size")] public long Size { get; set; }
        [JsonPropertyName("MtimeUtc")] public long MtimeUtc { get; set; }
    }

    /// <summary>One redirect of a cut file or normal: the container and the game path.</summary>
    public sealed class RowRedirect
    {
        [JsonPropertyName("Group")] public string Group { get; set; } = "";
        [JsonPropertyName("Option")] public string? Option { get; set; }
        [JsonPropertyName("Index")] public int? Index { get; set; }
        [JsonPropertyName("GamePath")] public string GamePath { get; set; } = "";
    }

    /// <summary>Per variant folder, which source the cut followed; and a hash of the file swaps on the material's paths.</summary>
    public sealed class RowFingerprint
    {
        public const string CaseContainer = "container", CaseGame = "game", CaseWorn = "worn", CaseNone = "none";

        [JsonPropertyName("Folders")] public List<RowFolder> Folders { get; set; } = [];
        [JsonPropertyName("SwapsSha8")] public string SwapsSha8 { get; set; } = "";
    }

    public sealed class RowFolder
    {
        [JsonPropertyName("Folder")] public int Folder { get; set; }
        [JsonPropertyName("Case")] public string Case { get; set; } = RowFingerprint.CaseNone;
    }

    public sealed class SharedFiles
    {
        [JsonPropertyName("Mask")] public FileRef? Mask { get; set; }
        [JsonPropertyName("FlatNormal")] public FileRef? FlatNormal { get; set; }
    }

    public static string PathOf(string modRoot)
        => Path.Combine(modRoot, SidecarDiscoveryService.SidecarSubdir, MatcapWriter.RecordFile);

    public static bool Exists(string modRoot) => File.Exists(PathOf(modRoot));

    /// <summary>The record, an empty one when there is none, or an <see cref="Unreadable"/> one.</summary>
    public static MatcapRecord Read(string modRoot)
    {
        var path = PathOf(modRoot);
        if (!File.Exists(path)) return new MatcapRecord();
        MatcapRecord? record;
        try
        {
            record = JsonSerializer.Deserialize<MatcapRecord>(File.ReadAllText(path));
        }
        catch
        {
            return new MatcapRecord { Unreadable = true };
        }
        if (record == null) return new MatcapRecord { Unreadable = true };
        // A later format (written by a newer Proteus) is never read as this one, so never written over.
        if (record.Version > CurrentVersion) return new MatcapRecord { Unreadable = true };
        Contain(modRoot, record);
        return record;
    }

    private static readonly System.Text.RegularExpressions.Regex TokenShape =
        new("^[0-9a-f]{6}$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// The record ships inside the mod, so its author controls it: every FILE path it holds is kept inside the mod
    /// (<see cref="ModPath"/>), and what is not is dropped — a model, a material (its file, its baked texture's, or a mesh's
    /// model), a shared file. Game paths and labels (<c>Name</c>, which carries a leading '/', <c>Normal</c>,
    /// <c>GamePaths</c>, <c>FileRef.GamePath</c>, <c>Library</c>, <c>Image</c>) are never joined onto the mod, so they are
    /// not checked. The token goes into file names: one not of <see cref="MatcapPlan.TokenFor"/>'s shape reads as "none
    /// yet", which Apply fills from the folder.
    /// </summary>
    private static void Contain(string modRoot, MatcapRecord record)
    {
        int dropped = 0;
        bool Inside(string? rel) => rel != null && ModPath.TryResolve(modRoot, rel, out _);

        record.Models ??= [];
        record.Materials ??= [];
        record.Shared ??= new SharedFiles();
        dropped += record.Models.RemoveAll(m => m == null || !Inside(m.File));
        // A row matcap's cut files, normals, cut meshes' models and mod-file sources must be inside the mod too: Undo
        // deletes the cut files and Detect reads the sources.
        static bool RowOutside(RowRef? row, Func<string?, bool> inside) =>
            row != null
            && ((row.Files ?? []).Any(f => f == null || !inside(f.File)
                                           || f.Source is { Game: false, File: { } sf } && !inside(sf)
                                           || f.Normal is { Game: false, File: { } nf } && !inside(nf)
                                           || f.Index is { Game: false, File: { } xf } && !inside(xf))
                || (row.Normals ?? []).Any(n => n == null || !inside(n.File))
                || (row.CutMeshes ?? []).Any(x => x == null || !inside(x.Model)));
        dropped += record.Materials.RemoveAll(m => m == null || !Inside(m.File)
                                                   || m.Source?.Tex is { } tex && !Inside(tex.File)
                                                   || (m.Meshes ?? []).Any(x => x == null || !Inside(x.Model))
                                                   || RowOutside(m.Row, Inside));
        if (record.Shared.Mask is { } mask && !Inside(mask.File)) { record.Shared.Mask = null; dropped++; }
        if (record.Shared.FlatNormal is { } flat && !Inside(flat.File)) { record.Shared.FlatNormal = null; dropped++; }
        if (!string.IsNullOrEmpty(record.Token) && !TokenShape.IsMatch(record.Token)) { record.Token = ""; dropped++; }
        record.Token ??= "";
        ModPath.NoteDropped("matcaps", modRoot, dropped);
    }

    public static void Write(string modRoot, MatcapRecord record)
    {
        var path = PathOf(modRoot);
        if (record.Models.Count == 0 && record.Materials.Count == 0 && record.Shared.Mask == null && record.Shared.FlatNormal == null)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Version 2 only once a row matcap is recorded: a record of whole-part matcaps stays readable by older builds.
        record.Version = record.Materials.Any(m => m.Row != null) ? CurrentVersion : 1;
        PenumbraModMeta.AtomicWrite(path, JsonSerializer.Serialize(record, new JsonSerializerOptions
        {
            WriteIndented = true, Encoder = ProteusJson.Encoder,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        }));
    }

    public ModelEntry? Model(string rel) =>
        Models.FirstOrDefault(m => PenumbraModMeta.SameFile(m.File, rel));

    /// <summary>The newest record format this build reads and writes: 2 added row matcaps (<see cref="MaterialEntry.Row"/>).</summary>
    public const int CurrentVersion = 2;

    /// <summary>The entry a material name belongs to: the matcap material itself, or a row matcap's cut copy.</summary>
    public MaterialEntry? EntryFor(string name) =>
        Materials.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? Materials.FirstOrDefault(m => m.Row != null && string.Equals(m.Row.Cut, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The materials whose meshes are in <paramref name="rel"/>.</summary>
    public IEnumerable<MaterialEntry> MaterialsOf(string rel) =>
        Materials.Where(m => m.Meshes.Any(x => PenumbraModMeta.SameFile(x.Model, rel))
                             || (m.Row?.CutMeshes.Any(x => PenumbraModMeta.SameFile(x.Model, rel)) ?? false));
}
