using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Proteus.Services;

/// <summary>
/// Which hair ports Proteus added to a mod, so they can be told apart from the author's files, remade when out of date
/// and undone. Kept as <c>Proteus/hairport.json</c> INSIDE the mod, so it travels with the files it names.
/// </summary>
internal sealed class HairPortRecord
{
    public const string RecordFile = "hairport.json";

    [JsonPropertyName("Version")] public int Version { get; set; } = 1;

    [JsonPropertyName("Ports")] public List<Entry> Ports { get; set; } = [];

    /// <summary>Game paths (lower case) the user undid an automatic port of, which are not ported automatically
    /// again; see <see cref="HairPortPlan.DeclineKey"/>. Persisted, so an undo outlives the session.</summary>
    [JsonPropertyName("Declined")] public List<string> Declined { get; set; } = [];

    /// <summary>The record file is there but could not be read: nothing may be saved over it or undone from it.</summary>
    [JsonIgnore] public bool Unreadable { get; private set; }

    /// <summary>Why a write refuses an <see cref="Unreadable"/> record.</summary>
    public static string UnreadableMessage => CheapLoc.Loc.Localize("HairPort.Refused.RecordUnreadable",
        "Proteus's record of this mod's hair ports (Proteus/hairport.json) could not be read, so nothing was changed.");

    /// <summary>One port: one model published from one place in the mod, with the materials it needed.</summary>
    public sealed class Entry
    {
        /// <summary>The group the port is published from; "" for the default data.</summary>
        [JsonPropertyName("Group")] public string Group { get; set; } = "";
        [JsonPropertyName("Option")] public string Option { get; set; } = "";
        [JsonPropertyName("SourceGamePath")] public string SourceGamePath { get; set; } = "";
        /// <summary>The author's file the port was made from, mod-relative.</summary>
        [JsonPropertyName("SourceFile")] public string SourceFile { get; set; } = "";
        [JsonPropertyName("SourceSha8")] public string SourceSha8 { get; set; } = "";
        [JsonPropertyName("TargetGamePath")] public string TargetGamePath { get; set; } = "";
        /// <summary>The port, mod-relative, under <see cref="HairPortPlan.Folder"/>.</summary>
        [JsonPropertyName("File")] public string File { get; set; } = "";
        /// <summary>Material redirects the port added beside it: game path to mod-relative file.</summary>
        [JsonPropertyName("Materials")] public Dictionary<string, string> Materials { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>The extra skeleton (<c>Est</c> entry) the port added for its race beside it; 0 for none.</summary>
        [JsonPropertyName("Est")] public int Est { get; set; }
        /// <summary>The source race's skeleton the port carries as its race's own for the hairstyle, published among
        /// <see cref="Materials"/>: what the source borrowed, or the hairstyle's own; 0 for none. Ports made before it
        /// existed read 0, as they carry none.</summary>
        [JsonPropertyName("SkeletonFrom")] public int SkeletonFrom { get; set; }
        /// <summary>Made on equip rather than in Studio.</summary>
        [JsonPropertyName("Auto")] public bool Auto { get; set; }
        [JsonPropertyName("PortVersion")] public int PortVersion { get; set; }

        public HairPortPlan.Owner Owner => new(Group, Option, "", new Dictionary<string, string>());
    }

    private static string PathOf(string modRoot)
        => Path.Combine(modRoot, SidecarDiscoveryService.SidecarSubdir, RecordFile);

    /// <summary>
    /// The mod's record, with every entry whose redirect is no longer in the manifest dropped — the author updated the
    /// mod, or the user removed the port by hand. Nothing is dropped when the manifest cannot be read: unknown is not
    /// gone. Empty, never null, when there is no record.
    /// </summary>
    public static HairPortRecord Read(string modRoot)
    {
        HairPortRecord record;
        try
        {
            var path = PathOf(modRoot);
            record = File.Exists(path)
                ? JsonSerializer.Deserialize<HairPortRecord>(File.ReadAllText(path)) ?? new HairPortRecord()
                : new HairPortRecord();
        }
        // Unknown is not gone: a record that is there but cannot be read is never written over, or every port it names
        // would be left untracked and every undo it remembers forgotten.
        catch (Exception) { record = new HairPortRecord { Unreadable = true }; }
        record.Ports ??= [];
        record.Declined ??= [];
        foreach (var e in record.Ports)
        {
            e.File = Rel(e.File);
            e.SourceFile = Rel(e.SourceFile);
            e.Materials = new Dictionary<string, string>(
                (e.Materials ?? []).ToDictionary(p => p.Key, p => Rel(p.Value)), StringComparer.OrdinalIgnoreCase);
        }

        if (record.Ports.Count > 0 && HairPortPlan.ReadOwners(modRoot) is { } owners)
            record.Ports.RemoveAll(e => !owners.Any(o => HairPortPlan.Same(o, e.Owner)
                                                       && HairPortPlan.FileFor(o, e.TargetGamePath) is { } rel
                                                       && Rel(rel).Equals(e.File, StringComparison.OrdinalIgnoreCase)));
        return record;
    }

    public static void Write(string modRoot, HairPortRecord record)
    {
        var path = PathOf(modRoot);
        if (record.Ports.Count == 0 && record.Declined.Count == 0)
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        PenumbraModMeta.AtomicWrite(path, JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>The one spelling of a mod-relative path compared here: forward slashes. Penumbra writes back.</summary>
    public static string Rel(string rel) => rel.Replace('\\', '/');
}
