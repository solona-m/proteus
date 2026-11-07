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
        try
        {
            return JsonSerializer.Deserialize<MatcapRecord>(File.ReadAllText(path)) ?? new MatcapRecord { Unreadable = true };
        }
        catch
        {
            return new MatcapRecord { Unreadable = true };
        }
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
        PenumbraModMeta.AtomicWrite(path, JsonSerializer.Serialize(record, new JsonSerializerOptions
        {
            WriteIndented = true, Encoder = ProteusJson.Encoder,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        }));
    }

    public ModelEntry? Model(string rel) =>
        Models.FirstOrDefault(m => PenumbraModMeta.SameFile(m.File, rel));

    /// <summary>The materials whose meshes are in <paramref name="rel"/>.</summary>
    public IEnumerable<MaterialEntry> MaterialsOf(string rel) =>
        Materials.Where(m => m.Meshes.Any(x => PenumbraModMeta.SameFile(x.Model, rel)));
}
