using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Proteus.Services;

/// <summary>
/// Puts a finished retarget into the outfit's own mod, as a new option of a group Proteus owns.
/// <para/>
/// The author's own files are never touched, which is what makes this safe to do to somebody else's mod and is the one
/// real advantage it has over the brush: there is no backup to keep, no ordering to negotiate with the other features
/// that back the same file up, and undo is a deletion rather than a restore.
/// </summary>
internal static class BodyRetargetWriter
{
    /// <summary>The folder inside the mod that holds every retargeted model, one subfolder per option.</summary>
    public const string Subfolder = "Body Retarget";

    /// <summary>The record, beside the mod's other Proteus state.</summary>
    public const string RecordFile = "bodyretarget.json";

    /// <summary>
    /// The option that means "leave this model as its author shipped it".
    /// <para/>
    /// It carries NO files, deliberately. With it selected the group contributes nothing, so whatever the mod already
    /// does for that game path — its default data, or another group's option — applies unchanged. Copying the author's
    /// file into an option of ours would look equivalent and is not: it would freeze a copy that stops tracking the
    /// original, and undo would have something to put back rather than nothing.
    /// </summary>
    public const string OriginalOption = "Original";

    private static readonly object WriteLock = new();

    internal sealed class Record
    {
        public string Group { get; set; } = "";
        public List<Entry> Options { get; set; } = [];
    }

    internal sealed class Entry
    {
        public string Name { get; set; } = "";
        public string GamePath { get; set; } = "";
        public string File { get; set; } = "";
        public string BodyMod { get; set; } = "";
        public string From { get; set; } = "";
        public string To { get; set; } = "";
    }

    /// <param name="Group">The group written.</param>
    /// <param name="Option">The option added.</param>
    /// <param name="Message">What to tell the user.</param>
    internal readonly record struct Outcome(bool Ok, string Group, string Option, string Message);

    /// <summary>
    /// Another group of this mod already replaces <paramref name="gamePath"/>, so the retarget group has to outrank it
    /// and the user should be told before they save rather than after.
    /// </summary>
    public static List<string> ClashingGroups(string modRoot, string gamePath, string ownGroup)
        => ClashingGroups(PenumbraModMeta.ReadAllRedirects(modRoot), gamePath, ownGroup);

    /// <inheritdoc cref="ClashingGroups(string, string, string)"/>
    /// <remarks>Over redirects already read, so a caller drawing every frame never re-parses the manifest.</remarks>
    public static List<string> ClashingGroups(IEnumerable<PenumbraModMeta.Redirect> redirects, string gamePath,
                                              string ownGroup)
    {
        var clashes = new List<string>();
        foreach (var r in redirects)
        {
            if (!string.Equals(r.GamePath, gamePath, StringComparison.OrdinalIgnoreCase)) continue;

            int split = r.Source.IndexOf(" / ", StringComparison.Ordinal);
            string group = split < 0 ? r.Source : r.Source[..split];
            if (group.Length == 0) continue;                                  // the mod's default data, not a group
            if (string.Equals(group, ownGroup, StringComparison.OrdinalIgnoreCase)) continue;
            if (!clashes.Contains(group, StringComparer.OrdinalIgnoreCase)) clashes.Add(group);
        }
        return clashes;
    }

    /// <summary>
    /// Write <paramref name="model"/> into the mod as <paramref name="optionName"/> of <paramref name="groupName"/>,
    /// adding the group if it is not there and replacing the option if it is.
    /// </summary>
    public static Outcome Save(string modRoot, string groupName, string optionName, string gamePath, byte[] model,
                               string bodyMod, string from, string to)
    {
        lock (WriteLock)
        {
            try
            {
                string rel = Path.Combine(Subfolder, Sanitise(optionName), TailOf(gamePath));
                string full = Path.Combine(modRoot, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                PenumbraModMeta.AtomicWrite(full, model);

                var options = PenumbraModMeta.TryReadFileOptions(modRoot, groupName) ?? [];

                // Rebuilt from what is there, so saving a second size grows the group instead of replacing it, and
                // saving the same size twice replaces just that option.
                var kept = options.Where(o => !string.Equals(o.Name, optionName, StringComparison.OrdinalIgnoreCase)
                                           && !string.Equals(o.Name, OriginalOption, StringComparison.OrdinalIgnoreCase))
                                  .ToList();

                var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [gamePath] = rel.Replace('\\', '/'),
                };

                // Carry over any other game path an earlier save of this same option wrote, so retargeting a mod's
                // _top and then its _dwn leaves one option that covers both.
                var existing = options.FirstOrDefault(o => string.Equals(o.Name, optionName,
                                                                         StringComparison.OrdinalIgnoreCase));
                if (existing.Files != null)
                    foreach (var (path, at) in existing.Files)
                        if (!files.ContainsKey(path)) files[path] = at;

                var final = new List<PenumbraModMeta.FileOption>
                {
                    new(OriginalOption, new Dictionary<string, string>()),
                };
                final.AddRange(kept);
                final.Add(new PenumbraModMeta.FileOption(optionName, files));

                // Ordinal 0 AND a priority above every other group: Penumbra's conflict rule between two groups of one
                // mod is priority, and the ordinal is what an older reading used, so setting both to agree means this
                // group wins either way.
                int priority = Math.Max(PenumbraModMeta.MaxGroupPriority(modRoot) + 1, 1);
                PenumbraModMeta.WriteFileOptionGroup(modRoot, 0, groupName, priority, final, final.Count - 1);

                WriteRecord(modRoot, groupName, optionName, gamePath, rel, bodyMod, from, to);

                return new Outcome(true, groupName, optionName,
                                   $"Saved as \"{optionName}\" in the \"{groupName}\" group. " +
                                   "Penumbra only picks a default for a mod it is adding for the first time, so " +
                                   "choose the option there to see it.");
            }
            catch (PenumbraModMeta.LegacyFolderException)
            {
                return new Outcome(false, groupName, optionName,
                                   "This mod is still in Penumbra's old folder format, which Proteus will not edit. " +
                                   "Enable it in Penumbra once so Penumbra upgrades it, then come back.");
            }
            catch (Exception e)
            {
                return new Outcome(false, groupName, optionName, $"Could not save: {e.Message}");
            }
        }
    }

    /// <summary>Remove one retargeted option, and the whole group once only <see cref="OriginalOption"/> is left.</summary>
    public static Outcome Undo(string modRoot, string groupName, string optionName)
    {
        lock (WriteLock)
        {
            try
            {
                var options = PenumbraModMeta.TryReadFileOptions(modRoot, groupName);
                if (options == null)
                    return new Outcome(false, groupName, optionName, "There is no retarget group in this mod.");

                var going = options.FirstOrDefault(o => string.Equals(o.Name, optionName,
                                                                      StringComparison.OrdinalIgnoreCase));
                var kept = options.Where(o => !string.Equals(o.Name, optionName, StringComparison.OrdinalIgnoreCase)
                                           && !string.Equals(o.Name, OriginalOption, StringComparison.OrdinalIgnoreCase))
                                  .ToList();

                if (kept.Count == 0)
                {
                    PenumbraModMeta.DeleteGroup(modRoot, groupName);
                    DeleteRecord(modRoot);
                }
                else
                {
                    var final = new List<PenumbraModMeta.FileOption>
                    {
                        new(OriginalOption, new Dictionary<string, string>()),
                    };
                    final.AddRange(kept);
                    int priority = Math.Max(PenumbraModMeta.MaxGroupPriority(modRoot), 1);
                    PenumbraModMeta.WriteFileOptionGroup(modRoot, 0, groupName, priority, final, 0);
                    ForgetOption(modRoot, optionName);
                }

                // The files last, so a crash between the two leaves an unreferenced folder rather than a group
                // pointing at a file that is gone — Penumbra treats the second as a broken mod.
                if (going.Files != null)
                    foreach (var rel in going.Files.Values)
                        DeleteQuietly(Path.Combine(modRoot, rel.Replace('/', Path.DirectorySeparatorChar)));

                PruneEmptyFolders(Path.Combine(modRoot, Subfolder));

                return new Outcome(true, groupName, optionName, $"Removed \"{optionName}\".");
            }
            catch (PenumbraModMeta.LegacyFolderException)
            {
                return new Outcome(false, groupName, optionName,
                                   "This mod is in Penumbra's old folder format. Enable it in Penumbra once, then " +
                                   "come back.");
            }
            catch (Exception e)
            {
                return new Outcome(false, groupName, optionName, $"Could not undo: {e.Message}");
            }
        }
    }

    public static Record? ReadRecord(string modRoot)
    {
        try
        {
            string path = Path.Combine(modRoot, SidecarDiscoveryService.SidecarSubdir, RecordFile);
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<Record>(File.ReadAllText(path));
        }
        catch
        {
            return null;
        }
    }

    private static void WriteRecord(string modRoot, string group, string option, string gamePath, string rel,
                                    string bodyMod, string from, string to)
    {
        var record = ReadRecord(modRoot) ?? new Record();
        record.Group = group;
        record.Options.RemoveAll(e => string.Equals(e.Name, option, StringComparison.OrdinalIgnoreCase)
                                   && string.Equals(e.GamePath, gamePath, StringComparison.OrdinalIgnoreCase));
        record.Options.Add(new Entry
        {
            Name = option, GamePath = gamePath, File = rel.Replace('\\', '/'),
            BodyMod = bodyMod, From = from, To = to,
        });
        SaveRecord(modRoot, record);
    }

    private static void ForgetOption(string modRoot, string option)
    {
        var record = ReadRecord(modRoot);
        if (record == null) return;
        record.Options.RemoveAll(e => string.Equals(e.Name, option, StringComparison.OrdinalIgnoreCase));
        SaveRecord(modRoot, record);
    }

    private static void SaveRecord(string modRoot, Record record)
    {
        string dir = Path.Combine(modRoot, SidecarDiscoveryService.SidecarSubdir);
        Directory.CreateDirectory(dir);
        PenumbraModMeta.AtomicWrite(Path.Combine(dir, RecordFile),
                                    JsonSerializer.Serialize(record, RecordJson));
    }

    private static void DeleteRecord(string modRoot)
        => DeleteQuietly(Path.Combine(modRoot, SidecarDiscoveryService.SidecarSubdir, RecordFile));

    private static readonly JsonSerializerOptions RecordJson = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>
    /// The game path's own tail, so the file sits where it would in a mod laid out by game path and reads correctly in
    /// Penumbra's file list.
    /// </summary>
    internal static string TailOf(string gamePath)
        => gamePath.Replace('/', Path.DirectorySeparatorChar);

    /// <summary>An option name as a folder name. Never empty, so two options cannot collide on "".</summary>
    internal static string Sanitise(string name)
    {
        var chars = name.Trim().ToCharArray();
        var invalid = Path.GetInvalidFileNameChars();
        for (int i = 0; i < chars.Length; i++)
            if (Array.IndexOf(invalid, chars[i]) >= 0) chars[i] = '_';

        string cleaned = new string(chars).Trim(' ', '.');
        return cleaned.Length > 0 ? cleaned : "option";
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // A file the game or Penumbra still holds open: the group no longer points at it, so it is inert.
        }
    }

    private static void PruneEmptyFolders(string root)
    {
        try
        {
            if (!Directory.Exists(root)) return;
            foreach (string dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                                            .OrderByDescending(d => d.Length))
                if (!Directory.EnumerateFileSystemEntries(dir).Any())
                    Directory.Delete(dir);

            if (!Directory.EnumerateFileSystemEntries(root).Any()) Directory.Delete(root);
        }
        catch
        {
            // Leftover empty folders are harmless.
        }
    }
}
