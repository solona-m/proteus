using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Proteus.Services;

/// <summary>
/// Puts a finished retarget into the outfit's own mod: as a new option of a group Proteus owns, or as one more option
/// of a single-choice group the author wrote (their size group, where a new size naturally belongs).
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
        /// <summary>The group Proteus made most recently. Kept for records written before each entry named its own.</summary>
        public string Group { get; set; } = "";
        public List<Entry> Options { get; set; } = [];

        /// <summary>The groups Proteus made itself — as opposed to the author's, which it only added options to.</summary>
        public IEnumerable<string> OwnGroups
            => Options.Where(e => !e.InAuthorGroup).Select(GroupOf)
                      .Append(Group).Where(g => g.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase);

        /// <summary>The group an entry went into; an entry from before entries named one was in <see cref="Group"/>.</summary>
        public string GroupOf(Entry e) => e.Group.Length > 0 ? e.Group : Group;

        /// <summary>The own groups still holding an entry, other than <paramref name="group"/>.</summary>
        public IEnumerable<string> OwnGroupsExcept(string group)
            => Options.Where(e => !e.InAuthorGroup).Select(GroupOf)
                      .Where(g => g.Length > 0 && !string.Equals(g, group, StringComparison.OrdinalIgnoreCase))
                      .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    internal sealed class Entry
    {
        /// <summary>The group this option was saved into.</summary>
        public string Group { get; set; } = "";

        /// <summary>Saved into a group of the author's, which undo must leave standing, rather than one Proteus made.</summary>
        public bool InAuthorGroup { get; set; }

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

    /// <summary>One refitted size to save: the option it becomes, its model, and the size it was refitted onto.</summary>
    internal readonly record struct Refit(string Option, byte[] Model, string To);

    /// <summary>
    /// Write <paramref name="model"/> into the mod as <paramref name="optionName"/> of <paramref name="groupName"/>,
    /// adding the group if it is not there and replacing the option if it is.
    /// </summary>
    public static Outcome Save(string modRoot, string groupName, string optionName, string gamePath, byte[] model,
                               string bodyMod, string from, string to)
        => Save(modRoot, groupName, gamePath, bodyMod, from, [new Refit(optionName, model, to)]);

    /// <summary>
    /// Whether <paramref name="groupName"/> is a group of the author's that a save would add options to, rather than
    /// one Proteus made (or would make) itself: it exists, and no save of ours created it.
    /// </summary>
    public static bool IsAuthorGroup(string modRoot, string groupName)
    {
        bool exists = (PenumbraModMeta.TryReadGroups(modRoot) ?? [])
            .Any(g => string.Equals(g.Name, groupName, StringComparison.OrdinalIgnoreCase));
        bool ours = ReadRecord(modRoot)?.OwnGroups.Contains(groupName, StringComparer.OrdinalIgnoreCase) ?? false;
        return exists && !ours;
    }

    /// <summary>
    /// Write every refit into the mod as options of <paramref name="groupName"/> in one manifest write, adding the
    /// group if it is not there and replacing any option of the same name.
    /// <para/>
    /// One write rather than one per size: Penumbra reloads the mod each time its manifest changes, and a half-saved
    /// batch — three sizes of five, then a failure — is harder to reason about than all or nothing.
    /// </summary>
    public static Outcome Save(string modRoot, string groupName, string gamePath, string bodyMod, string from,
                               IReadOnlyList<Refit> refits)
    {
        string names = string.Join(", ", refits.Select(r => r.Option));
        lock (WriteLock)
        {
            try
            {
                if (refits.Count == 0) return new Outcome(false, groupName, "", "Nothing to save.");

                bool author = IsAuthorGroup(modRoot, groupName);
                var options = PenumbraModMeta.TryReadFileOptions(modRoot, groupName) ?? [];

                // An author's option is never overwritten: only one this tool put there earlier may be replaced.
                if (author)
                {
                    var ours = ReadRecord(modRoot)?.Options
                                   .Where(e => e.InAuthorGroup && string.Equals(e.Group, groupName,
                                                                               StringComparison.OrdinalIgnoreCase))
                                   .Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase)
                               ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var taken = refits.FirstOrDefault(r => !ours.Contains(r.Option)
                                                           && options.Any(o => string.Equals(o.Name, r.Option,
                                                                                             StringComparison.OrdinalIgnoreCase)));
                    if (taken.Option != null)
                        return new Outcome(false, groupName, names,
                                           $"\"{groupName}\" already has an option called \"{taken.Option}\" of its own. " +
                                           "Save the refit to another group.");
                }
                // Paths every OTHER option already points at. A refit's file is named after its option, and an option
                // can be renamed in Penumbra afterwards while its file keeps the old name: the next refit whose option
                // has that name then wrote over the file the renamed option still points at, and both showed the model
                // saved last. An option of a name we are saving is not "other" — saving the same size twice replaces
                // it, file and all.
                var claimed = Claimed(modRoot, groupName, refits);

                var written = new List<(Refit Refit, string Rel)>();
                foreach (var refit in refits)
                {
                    // The folder this option already uses, so a second slot saved into it lands beside the first.
                    string folder = FolderOf(options, refit.Option) ?? Sanitise(refit.Option);
                    string rel = Free(claimed, folder, TailOf(gamePath));
                    claimed.Add(rel.Replace('\\', '/'));   // and two refits in one batch cannot collide either
                    string full = Path.Combine(modRoot, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                    PenumbraModMeta.AtomicWrite(full, refit.Model);
                    written.Add((refit, rel));
                }

                if (author)
                {
                    PenumbraModMeta.AddFileOptions(modRoot, groupName, written.Select(w => new PenumbraModMeta.FileOption(
                        w.Refit.Option,
                        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            [gamePath] = w.Rel.Replace('\\', '/'),
                        })).ToList());
                    foreach (var (refit, rel) in written)
                        WriteRecord(modRoot, groupName, true, refit.Option, gamePath, rel, bodyMod, from, refit.To);
                    return new Outcome(true, groupName, names, Added(refits, groupName) +
                                       "Choose it there in Penumbra to wear it.");
                }

                // Rebuilt from what is there, so saving more sizes grows the group instead of replacing it, and
                // saving the same size twice replaces just that option.
                bool Saving(string name) => refits.Any(r => string.Equals(r.Option, name,
                                                                         StringComparison.OrdinalIgnoreCase));
                var kept = options.Where(o => !Saving(o.Name)
                                           && !string.Equals(o.Name, OriginalOption, StringComparison.OrdinalIgnoreCase))
                                  .ToList();

                var final = new List<PenumbraModMeta.FileOption>
                {
                    new(OriginalOption, new Dictionary<string, string>()),
                };
                final.AddRange(kept);

                foreach (var (refit, rel) in written)
                {
                    var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        [gamePath] = rel.Replace('\\', '/'),
                    };

                    // Carry over any other game path an earlier save of this same option wrote, so retargeting a
                    // mod's _top and then its _dwn leaves one option that covers both.
                    var existing = options.FirstOrDefault(o => string.Equals(o.Name, refit.Option,
                                                                             StringComparison.OrdinalIgnoreCase));
                    if (existing.Files != null)
                        foreach (var (path, at) in existing.Files)
                            if (!files.ContainsKey(path)) files[path] = at;

                    final.Add(new PenumbraModMeta.FileOption(refit.Option, files));
                }

                // A priority above every other group, which is what decides a game path two groups both claim. The
                // POSITION is left alone: see Position.
                int priority = Math.Max(PenumbraModMeta.MaxGroupPriority(modRoot) + 1, 1);
                PenumbraModMeta.WriteFileOptionGroup(modRoot, Position(modRoot, groupName), groupName, priority, final,
                                                     final.Count - 1);

                foreach (var (refit, rel) in written)
                    WriteRecord(modRoot, groupName, false, refit.Option, gamePath, rel, bodyMod, from, refit.To);

                return new Outcome(true, groupName, names,
                                   Added(refits, groupName) +
                                   "Penumbra only picks a default for a mod it is adding for the first time, so " +
                                   "choose the option there to see it.");
            }
            catch (PenumbraModMeta.LegacyFolderException)
            {
                return new Outcome(false, groupName, names,
                                   "This mod is still in Penumbra's old folder format, which Proteus will not edit. " +
                                   "Enable it in Penumbra once so Penumbra upgrades it, then come back.");
            }
            catch (Exception e)
            {
                return new Outcome(false, groupName, names, $"Could not save: {e.Message}");
            }
        }
    }

    /// <summary>
    /// Every file path the mod's options point at, less the options this save is replacing — the paths a save may not
    /// land on. Read from the manifest rather than from our own record, because the record holds the name an option had
    /// when we wrote it and the player may have renamed it since; the manifest is what the game actually reads.
    /// </summary>
    private static HashSet<string> Claimed(string modRoot, string groupName, IReadOnlyList<Refit> refits)
    {
        var mine = refits.Select(r => r.Option).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // The groups once, then each one's options off the element already read: asking for a group at a time re-reads
        // and re-parses the manifest every time, and this walks all of them.
        foreach (var (name, group) in PenumbraModMeta.TryReadGroups(modRoot) ?? [])
            foreach (var option in PenumbraModMeta.FileOptionsOf(group) ?? [])
            {
                bool replacing = string.Equals(name, groupName, StringComparison.OrdinalIgnoreCase)
                              && mine.Contains(option.Name);
                if (replacing) continue;
                foreach (string at in option.Files.Values) claimed.Add(at.Replace('\\', '/'));
            }
        return claimed;
    }

    /// <summary>
    /// The folder under <see cref="Subfolder"/> that an option's files already sit in, or null when it has none there.
    /// An option keeps one folder for the life of it: saving a second slot into an option whose folder was numbered
    /// (see <see cref="Free"/>) must put the new file in that same folder, not start another.
    /// </summary>
    private static string? FolderOf(IReadOnlyList<PenumbraModMeta.FileOption> options, string option)
    {
        var existing = options.FirstOrDefault(o => string.Equals(o.Name, option, StringComparison.OrdinalIgnoreCase));
        foreach (string at in existing.Files?.Values ?? Enumerable.Empty<string>())
        {
            var parts = at.Replace('\\', '/').Split('/');
            if (parts.Length > 2 && string.Equals(parts[0], Subfolder, StringComparison.OrdinalIgnoreCase))
                return parts[1];
        }
        return null;
    }

    /// <summary>
    /// Where one refit's file goes: the option's own folder under <see cref="Subfolder"/>, with a number on the end if
    /// another option already holds that path. The OPTION's folder, not any deeper one — an option's files belong
    /// together, and the rest of the path is the game path, which the file has to keep.
    /// </summary>
    private static string Free(HashSet<string> claimed, string option, string tail)
    {
        string rel = Path.Combine(Subfolder, option, tail);
        if (!claimed.Contains(rel.Replace('\\', '/'))) return rel;

        for (int n = 2; n < 1000; n++)
        {
            string candidate = Path.Combine(Subfolder, $"{option} {n}", tail);
            if (!claimed.Contains(candidate.Replace('\\', '/'))) return candidate;
        }
        return rel;
    }

    private static string Added(IReadOnlyList<Refit> refits, string groupName)
        => refits.Count == 1
               ? $"Saved as \"{refits[0].Option}\" in the \"{groupName}\" group. "
               : $"Saved {refits.Count} sizes in the \"{groupName}\" group. ";

    /// <summary>
    /// Remove one retargeted option. From a group Proteus made, the whole group goes once only
    /// <see cref="OriginalOption"/> is left; from an author's group, only the option goes and the group stays.
    /// </summary>
    public static Outcome Undo(string modRoot, string groupName, string optionName)
    {
        lock (WriteLock)
        {
            try
            {
                bool author = ReadRecord(modRoot)?.Options.Any(e => e.InAuthorGroup
                                  && string.Equals(e.Group, groupName, StringComparison.OrdinalIgnoreCase)
                                  && string.Equals(e.Name, optionName, StringComparison.OrdinalIgnoreCase)) ?? false;
                if (author)
                {
                    var removed = PenumbraModMeta.RemoveOption(modRoot, groupName, optionName);
                    ForgetOption(modRoot, groupName, optionName);
                    if (removed != null)
                        foreach (var rel in removed.Values)
                            DeleteQuietly(Path.Combine(modRoot, rel.Replace('/', Path.DirectorySeparatorChar)));
                    PruneEmptyFolders(Path.Combine(modRoot, Subfolder));
                    return new Outcome(true, groupName, optionName, $"Removed \"{optionName}\".");
                }

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
                    ForgetGroup(modRoot, groupName);
                }
                else
                {
                    var final = new List<PenumbraModMeta.FileOption>
                    {
                        new(OriginalOption, new Dictionary<string, string>()),
                    };
                    final.AddRange(kept);
                    int priority = Math.Max(PenumbraModMeta.MaxGroupPriority(modRoot), 1);
                    PenumbraModMeta.WriteFileOptionGroup(modRoot, Position(modRoot, groupName), groupName, priority,
                                                         final, 0);
                    ForgetOption(modRoot, groupName, optionName);
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

    /// <summary>
    /// Where the group goes in the mod's group list: where it already is, or after everything else when it is new.
    /// NEVER in front of the author's groups.
    /// <para/>
    /// Penumbra carries a mod's settings across a reload by group POSITION. Inserting a group at the front shifts every
    /// author group down one, and each then inherits its neighbour's selection: on "Seaside" the multi-select group
    /// that switches the halter and the tanga on inherited the size group's 0, which ticks nothing, and the character
    /// was left naked the moment the save reloaded the mod. Appending shifts nothing; keeping an existing group where
    /// it is means saving again, or undoing, shifts nothing either — and a group at the end is removed without moving
    /// anything above it.
    /// </summary>
    internal static int Position(string modRoot, string groupName)
    {
        var groups = PenumbraModMeta.TryReadGroups(modRoot) ?? [];
        int at = groups.FindIndex(g => string.Equals(g.Name, groupName, StringComparison.OrdinalIgnoreCase));
        return at >= 0 ? at : groups.Count;
    }

    /// <summary>
    /// The mod's retarget record, without entries whose option is no longer in the mod — see <see cref="Pruned"/>.
    /// </summary>
    public static Record? ReadRecord(string modRoot)
    {
        try
        {
            string path = Path.Combine(modRoot, SidecarDiscoveryService.SidecarSubdir, RecordFile);
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<Record>(File.ReadAllText(path)) is { } record ? Pruned(modRoot, record) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Drop the entries whose group, or whose option in that group, is gone from the mod — deleted in Penumbra, or by
    /// hand. Left in, the newest ghost is what Undo reaches for: it answers "There is no retarget group in this mod",
    /// removes nothing, and every press after it does the same. The next write saves the pruned record, so a record
    /// heals itself.
    /// <para/>
    /// Nothing is pruned when the manifest cannot be read: "could not tell" must never mean "gone".
    /// </summary>
    private static Record Pruned(string modRoot, Record record)
    {
        // Entries from before each named its own group were in the record's top-level one.
        foreach (var e in record.Options.Where(e => e.Group.Length == 0)) e.Group = record.Group;

        if (PenumbraModMeta.TryReadGroups(modRoot) is not { } groups) return record;
        var options = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, group) in groups)
            options.TryAdd(name, new HashSet<string>(PenumbraModMeta.ReadOptionNames(group), StringComparer.OrdinalIgnoreCase));

        record.Options.RemoveAll(e => !options.TryGetValue(e.Group, out var names) || !names.Contains(e.Name));
        if (!options.ContainsKey(record.Group))
            record.Group = record.OwnGroupsExcept(record.Group).LastOrDefault() ?? "";
        return record;
    }

    private static void WriteRecord(string modRoot, string group, bool inAuthorGroup, string option, string gamePath,
                                    string rel, string bodyMod, string from, string to)
    {
        var record = ReadRecord(modRoot) ?? new Record();
        // Entries from before each named its own group take the old top-level one now, before it moves on.
        foreach (var old in record.Options.Where(e => e.Group.Length == 0)) old.Group = record.Group;
        if (!inAuthorGroup) record.Group = group;
        record.Options.RemoveAll(e => string.Equals(e.Group, group, StringComparison.OrdinalIgnoreCase)
                                   && string.Equals(e.Name, option, StringComparison.OrdinalIgnoreCase)
                                   && string.Equals(e.GamePath, gamePath, StringComparison.OrdinalIgnoreCase));
        record.Options.Add(new Entry
        {
            Group = group, InAuthorGroup = inAuthorGroup,
            Name = option, GamePath = gamePath, File = rel.Replace('\\', '/'),
            BodyMod = bodyMod, From = from, To = to,
        });
        SaveRecord(modRoot, record);
    }

    private static void ForgetOption(string modRoot, string group, string option)
        => Forget(modRoot, group, e => string.Equals(e.Name, option, StringComparison.OrdinalIgnoreCase));

    private static void ForgetGroup(string modRoot, string group) => Forget(modRoot, group, _ => true);

    /// <summary>Drop the entries of <paramref name="group"/> that match, and the record once none are left.</summary>
    private static void Forget(string modRoot, string group, Func<Entry, bool> match)
    {
        var record = ReadRecord(modRoot);
        if (record == null) return;
        record.Options.RemoveAll(e => string.Equals(record.GroupOf(e), group, StringComparison.OrdinalIgnoreCase)
                                   && match(e));
        if (record.Options.Count == 0)
        {
            DeleteRecord(modRoot);
            return;
        }
        if (string.Equals(record.Group, group, StringComparison.OrdinalIgnoreCase))
            record.Group = record.OwnGroupsExcept(group).LastOrDefault() ?? "";
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

    /// <summary>
    /// An option name as a folder name. Never empty, so two options cannot collide on "".
    /// <para/>
    /// Plain ASCII only. The option names this tool writes carry "—" and "·", and a file path with them in did load in
    /// game but was never recognised by the Studio's live tools, which match the game's own name for the drawn file
    /// against the path on disk: the game hands that name back in another encoding. Dashes and dots become "-", anything
    /// else outside ASCII "_". The option's NAME keeps its characters; only the folder is plain.
    /// </summary>
    internal static string Sanitise(string name)
    {
        var chars = name.Trim().ToCharArray();
        var invalid = Path.GetInvalidFileNameChars();
        for (int i = 0; i < chars.Length; i++)
        {
            if (chars[i] is '—' or '–' or '·' or '•') chars[i] = '-';
            else if (chars[i] > '~' || Array.IndexOf(invalid, chars[i]) >= 0) chars[i] = '_';
        }

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
