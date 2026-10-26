using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Proteus.Services;

/// <summary>
/// Which of a mod's redirects Proteus pointed at an earless copy, so they can be pointed back. Kept as
/// <c>Proteus/earlesshair.json</c> INSIDE the mod, beside <see cref="HairPortRecord"/> but apart from it: an older build
/// that rewrites <c>hairport.json</c> would drop a list it does not know, and with it every way back to the author's file.
/// Losing this record loses nothing else — the author's file is in the copy's own path
/// (<see cref="EarlessHairPlan.AuthorFileFor"/>).
/// </summary>
internal sealed class EarlessHairRecord
{
    public const string RecordFile = "earlesshair.json";

    [JsonPropertyName("Version")] public int Version { get; set; } = 1;

    [JsonPropertyName("Swaps")] public List<Entry> Swaps { get; set; } = [];

    /// <summary>The record file is there but could not be read: nothing may be saved over it or restored from it.</summary>
    [JsonIgnore] public bool Unreadable { get; private set; }

    public static string UnreadableMessage => ProteusLoc.Localize("HairPort.Earless.Refused.RecordUnreadable",
        "Proteus's record of this mod's earless hairstyles (Proteus/earlesshair.json) could not be read, so nothing was changed.");

    /// <summary>One redirect pointed at an earless copy, in one place of the mod.</summary>
    public sealed class Entry
    {
        /// <summary>"" for the default data.</summary>
        [JsonPropertyName("Group")] public string Group { get; set; } = "";
        [JsonPropertyName("Option")] public string Option { get; set; } = "";
        [JsonPropertyName("GamePath")] public string GamePath { get; set; } = "";
        /// <summary>The author's file the redirect named, mod-relative — what it is pointed back at.</summary>
        [JsonPropertyName("AuthorFile")] public string AuthorFile { get; set; } = "";
        /// <summary>The author's file when the copy was made; empty for an entry made up for a copy found unrecorded,
        /// which so is always out of date.</summary>
        [JsonPropertyName("AuthorSha8")] public string AuthorSha8 { get; set; } = "";
        /// <summary>The copy, under <see cref="EarlessHairPlan.Folder"/>.</summary>
        [JsonPropertyName("File")] public string File { get; set; } = "";
        [JsonPropertyName("EarlessVersion")] public int EarlessVersion { get; set; }

        public HairPortPlan.Owner Owner => new(Group, Option, "", new Dictionary<string, string>());
    }

    private static string PathOf(string modRoot)
        => Path.Combine(modRoot, SidecarDiscoveryService.SidecarSubdir, RecordFile);

    /// <summary>Whether the mod has a record at all — a cheap look before reading one.</summary>
    internal static bool Exists(string modRoot) => System.IO.File.Exists(PathOf(modRoot));

    /// <summary>
    /// The mod's record, with every entry whose redirect no longer names its copy dropped — the author updated the mod,
    /// or the user pointed it elsewhere. Nothing is dropped when the manifest cannot be read: unknown is not gone. Empty,
    /// never null, when there is no record.
    /// </summary>
    public static EarlessHairRecord Read(string modRoot)
    {
        EarlessHairRecord record;
        try
        {
            var path = PathOf(modRoot);
            record = System.IO.File.Exists(path)
                ? JsonSerializer.Deserialize<EarlessHairRecord>(System.IO.File.ReadAllText(path)) ?? new EarlessHairRecord()
                : new EarlessHairRecord();
        }
        catch (Exception) { record = new EarlessHairRecord { Unreadable = true }; }
        record.Swaps ??= [];
        foreach (var e in record.Swaps)
        {
            e.File = HairPortRecord.Rel(e.File ?? "");
            e.AuthorFile = HairPortRecord.Rel(e.AuthorFile ?? "");
        }

        if (record.Swaps.Count > 0 && HairPortPlan.ReadOwners(modRoot) is { } owners)
            record.Swaps.RemoveAll(e => !owners.Any(o => HairPortPlan.Same(o, e.Owner)
                                                       && HairPortPlan.FileFor(o, e.GamePath) is { } rel
                                                       && HairPortRecord.Rel(rel).Equals(e.File, StringComparison.OrdinalIgnoreCase)));
        return record;
    }

    /// <summary>The entry of one place's redirect of <paramref name="gamePath"/> to <paramref name="file"/>, or null.</summary>
    public Entry? Find(HairPortPlan.Owner owner, string gamePath, string file)
        => Swaps.FirstOrDefault(e => HairPortPlan.Same(e.Owner, owner)
                                     && e.GamePath.Equals(gamePath, StringComparison.OrdinalIgnoreCase)
                                     && e.File.Equals(HairPortRecord.Rel(file), StringComparison.OrdinalIgnoreCase));

    /// <summary>Saved; an empty record is deleted.</summary>
    public static void Write(string modRoot, EarlessHairRecord record)
    {
        var path = PathOf(modRoot);
        if (record.Swaps.Count == 0)
        {
            if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        PenumbraModMeta.AtomicWrite(path, JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true }));
    }
}
