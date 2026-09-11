using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using CheapLoc;

namespace Proteus.Services;

/// <summary>
/// What Proteus changed in a hair mod to make it fit under a hat, so the edit can be undone.
/// <para/>
/// Kept as <c>Proteus/hatcompat.json</c> INSIDE the mod, for the same reason
/// <see cref="MeshToggleRecord"/> is: it names backups that live there too, and a record separated from
/// its backups describes files it can no longer restore.
/// </summary>
internal sealed class HatCompatRecord
{
    /// <summary>Model files that were edited, relative to the mod root, with forward slashes.</summary>
    [JsonPropertyName("Files")] public List<string> Files { get; set; } = [];

    /// <summary>Submeshes tagged to vanish under a hat, as "mesh.submesh", per file.</summary>
    [JsonPropertyName("Hidden")] public Dictionary<string, List<string>> Hidden { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Which generation of <see cref="HatCompatSolve"/> patched each file — see
    /// <see cref="HatCompatSolve.Version"/>. Missing means a record written before this was stamped, which
    /// is exactly the generation most worth redoing.
    /// <para/>
    /// Per FILE and not per mod, because a hair pack ships a dozen hairstyles out of one folder and they are
    /// patched as they are worn, months apart. A single stamp on the mod would call the eleven that have not
    /// been touched since current, on the strength of the twelfth.
    /// </summary>
    [JsonPropertyName("Versions")] public Dictionary<string, int> Versions { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Makes a hairstyle hat-compatible in place, by writing into the mod the two things the game already knows
/// how to use: a <c>shp_hib</c> shape key it blends to while a head piece is worn, and the <c>atr_kam</c>
/// attribute on the parts that should vanish under one entirely.
/// <para/>
/// Both are native. Nothing here is hosted in Proteus and nothing depends on Proteus still running — the
/// patched mod works on its own and exports with the folder, which is why the edit goes into the mod rather
/// than into a redirect. That also means it MUST be undoable, so every file is copied aside before its first
/// edit and the record names what was done.
/// <para/>
/// Arranged so the mod is never left half-edited: every model is patched IN MEMORY and only written once
/// they have all succeeded. The failure that matters is the third file of four throwing after two are on
/// disk, which would leave a hairstyle with a shape key over geometry another file no longer has.
/// </summary>
public static class HatCompatService
{
    public const string RecordFile = "hatcompat.json";
    public const string BackupSubdir = "hatcompat-backup";

    /// <summary>The shape the game blends to while a head piece is worn.</summary>
    public const string HatShape = "shp_hib";

    /// <summary>"Scalp" — the attribute the game drops entirely under a head piece.</summary>
    public const string ScalpAttribute = "atr_kam";

    /// <summary>
    /// The one spelling of a mod-relative path this class compares, records and looks up by: forward
    /// slashes, as a manifest names it.
    /// <para/>
    /// Load-bearing, and it was not applied consistently. A path reaches here from two places that
    /// disagree: <see cref="InMods"/> normalises a resolved file to forward slashes, while
    /// <see cref="SiblingFiles"/> returned whatever the mod's own manifest said, which on Windows is
    /// backslashes. Every comparison in this file is then a plain string compare, so ONE FILE under TWO
    /// SPELLINGS is two files — and a record in the wild proves it happens, carrying
    /// <c>races/miqote/…</c> beside <c>races\miqote earless\…</c>.
    /// <para/>
    /// What that cost: <see cref="IsPatched"/> reported the backslash-recorded file as untouched, so
    /// <see cref="Inspect"/> then found the <c>shp_hib</c> PROTEUS ITSELF had written and credited it to
    /// the mod's author; <see cref="Revert"/> filtered the same list the same way and told the user there
    /// was nothing to undo while the patch sat on disk; and the leftover <see cref="HatCompatRecord.Hidden"/>
    /// and <see cref="HatCompatRecord.Versions"/> entries kept the record alive so it went on claiming
    /// files were patched.
    /// <para/>
    /// Not the same thing as <see cref="Native"/>, which goes the other way to build a real path.
    /// </summary>
    internal static string Rel(string rel) => rel.Replace('\\', '/');

    /// <param name="FilesPatched">How many model files were changed.</param>
    /// <param name="Unaddressable">Vertices the press wanted to move that no shape value could name — see
    /// <see cref="ModelAttributeWriter.AddShape"/>. Reported rather than discarded: it is hair left standing
    /// exactly where the format ran out, and it looks identical to the press deciding that hair was fine.</param>
    public sealed record Outcome(bool Ok, string Message, int FilesPatched, int Unaddressable = 0);

    /// <summary>
    /// What Proteus proposes to do to one hair model, before anything is written.
    /// <para/>
    /// Separated from applying it so the panel can describe the change before it happens, and so the same
    /// examination can be reused for the sibling files an option group swaps in.
    /// </summary>
    /// <param name="Unmeasurable">The wearer's head could not be read, so there is no skull to fit against
    /// and Proteus does nothing. See <see cref="Inspect"/>.</param>
    /// <param name="TookOver">The hairstyle arrived with hat support of its own, and that support measured
    /// as hiding hair no hat covers — so Proteus is replacing its <c>atr_kam</c> mask and LEAVING its shape
    /// alone. See <see cref="HatCompatSolve.MeasureScalpTagging"/> and the mask-only branch in
    /// <see cref="Apply"/>.</param>
    public sealed record Proposal(
        string Rel,
        ModelParts Parts,
        HatCompatSolve.Result Solve,
        bool AlreadyCompatible,
        bool Unmeasurable = false,
        bool TookOver = false);

    /// <summary>The hair Proteus would patch, and the head it would press it against.</summary>
    /// <param name="ModRoot">The mod folder that supplies the hair — the folder directly under Penumbra's
    /// mods root, which is the unit a backup and a record belong to.</param>
    /// <param name="Rel">The hair file's path inside that mod, with forward slashes, as a manifest names it.</param>
    /// <param name="Head">The wearer's face model, already read. Null when it could not be resolved, in
    /// which case the press falls back to guessing the skull from the hair.</param>
    public sealed record Target(string GamePath, string ModRoot, string Rel, byte[] Model, byte[]? Head);

    /// <summary>
    /// Which hair file to patch, from the models the character is actually drawing.
    /// <para/>
    /// Everything comes from the live model list rather than being constructed: a hairstyle's game path is
    /// only knowable by reading it back off the draw object, and the mod supplying it is only knowable by
    /// asking Penumbra to resolve that path and seeing where the file lands.
    /// <para/>
    /// Returns null for VANILLA hair — a path that resolves to the game's own data, or to somewhere outside
    /// the mods folder, is not something to edit, and vanilla hair already has a hat shape anyway.
    /// </summary>
    /// <param name="resolve">Penumbra's game-path-to-file resolver, honouring the active collection.</param>
    /// <param name="readFile">Reads any game file, from the mod redirect if there is one and from the
    /// game's own data otherwise — <c>TextureLoader.LoadRawFile</c>. REQUIRED for the face model to be
    /// found at all on a wearer whose face is vanilla; see the remarks below.</param>
    public static Target? FindEquippedHair(
        IReadOnlyList<string>? humanPartModels, Func<string, string?> resolve, string? modsRoot,
        Func<string, byte[]?> readFile)
    {
        if (Locate(humanPartModels, resolve, modsRoot) is not { } at) return null;
        var (hair, file, modRoot, rel) = at;

        byte[] model;
        try { model = File.ReadAllBytes(file); } catch (IOException) { return null; }

        // The face model carries the cranium the press aims at, and it MUST be read from the game's own
        // data when no mod supplies one.
        //
        // This used to require File.Exists on the resolved path, which quietly meant "only a face a mod
        // replaces". Penumbra hands back the game path unchanged for an unmodded file, and that is not a
        // file on disk — so every wearer with a VANILLA FACE fell through to guessing the skull from the
        // hair, and that guess is not close. Measured on one character: 1.5271 centre / 0.0728 radius from
        // the real face against 1.4613 / 0.1334 guessed, putting the cut plane 66 mm too low and the
        // lateral bound at 163 mm instead of 103 mm. Most of the hairstyle was tagged to vanish, and the
        // hair mod, the race and the hat all looked like plausible causes.
        //
        // The old comment here claimed reading game data was not something this could do. It has been for
        // as long as the second skin has been cutting shells from vanilla bodies.
        byte[]? head = null;
        var facePath = humanPartModels?.FirstOrDefault(
            p => p.Contains("/obj/face/", StringComparison.OrdinalIgnoreCase));
        if (facePath != null)
            try { head = readFile(facePath); } catch (IOException) { /* refuses to fit */ }

        return new Target(hair, modRoot, rel, model, head);
    }

    /// <summary>Which file currently serves the equipped hairstyle, without reading it.</summary>
    private static (string GamePath, string File, string ModRoot, string Rel)? Locate(
        IReadOnlyList<string>? humanPartModels, Func<string, string?> resolve, string? modsRoot)
    {
        if (humanPartModels == null || modsRoot is not { Length: > 0 }) return null;

        var hair = humanPartModels.FirstOrDefault(p => p.Contains("/obj/hair/", StringComparison.OrdinalIgnoreCase));
        if (hair == null) return null;

        var file = resolve(hair);
        if (file == null || !File.Exists(file)) return null;
        if (!InMods(file, modsRoot, out var modRoot, out var rel)) return null;   // vanilla, or another source
        return (hair, file, modRoot, rel);
    }

    /// <summary>
    /// A cheap identity for the hairstyle on the character — everything that has to stay the same for a
    /// previous examination to still be valid.
    /// <para/>
    /// The game path is NOT enough, and that was a real bug: switching hairstyle in Glamourer changes it,
    /// but switching which MOD supplies a hairstyle — right-clicking in Penumbra — leaves the path
    /// identical and swaps the file behind it. Keyed on the path alone, Proteus saw no change at all and
    /// went on describing the hair that had just been replaced. The resolved file, its length and its
    /// timestamp cover both, and the timestamp additionally notices Proteus's own patch landing.
    /// </summary>
    public static string? EquippedHairKey(
        IReadOnlyList<string>? humanPartModels, Func<string, string?> resolve, string? modsRoot)
    {
        if (Locate(humanPartModels, resolve, modsRoot) is not { } at) return null;
        try
        {
            var info = new FileInfo(at.File);
            return $"{at.GamePath}|{at.File}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// Split a resolved file into the mod folder that owns it and the path inside it.
    /// <para/>
    /// A Penumbra mod is exactly one directory under the mods root, so the owner is the FIRST segment below
    /// it and everything after is the manifest-relative path. Compared case-insensitively and on a
    /// normalised full path, because a resolved path and the configured root routinely disagree about
    /// slashes and drive-letter case on Windows.
    /// </summary>
    internal static bool InMods(string file, string modsRoot, out string modRoot, out string rel)
    {
        modRoot = "";
        rel = "";
        string full, root;
        try
        {
            full = Path.GetFullPath(file);
            root = Path.GetFullPath(modsRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (ArgumentException) { return false; }
        catch (NotSupportedException) { return false; }

        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;
        var tail = full[(root.Length + 1)..];
        int cut = tail.IndexOf(Path.DirectorySeparatorChar);
        if (cut <= 0 || cut == tail.Length - 1) return false;

        modRoot = Path.Combine(root, tail[..cut]);
        rel = tail[(cut + 1)..].Replace(Path.DirectorySeparatorChar, '/');
        return true;
    }

    /// <summary>
    /// Whether this model already carries a hat shape, in which case its author has done this by hand and
    /// Proteus has no business touching it.
    /// <para/>
    /// Reads the <c>Shape</c> records rather than <see cref="SecondSkinWriter.Source.Shapes"/>, which drops
    /// any shape with no LOD0 entries — a hair whose hat shape covers only its lower LODs would otherwise
    /// look untouched and be given a second one.
    /// </summary>
    public static bool IsHatCompatible(byte[] mdl)
    {
        try { return ModelAttributeWriter.DeclaresShape(mdl, HatShape); }
        catch { return false; }
    }

    /// <summary>
    /// Every file in the mod that serves this same hairstyle — the one Penumbra currently resolves, and any
    /// sibling an option would swap in for it.
    /// <para/>
    /// A hair mod routinely ships the same hairstyle several times over: a long version and a short one, or
    /// one per accessory, each its own file behind one option group. Patching only the file resolved right
    /// now leaves every other option unpatched, so the hat works until the wearer changes option and then
    /// silently stops — which looks exactly like the patch having failed. The Parts tab patches siblings
    /// for the same reason.
    /// </summary>
    public static List<string> SiblingFiles(string modRoot, string gamePath, string rel)
    {
        // Canonicalised on the way in AND on each manifest entry, or the dedupe below compares the
        // manifest's backslashes against a forward-slash rel, never matches, and returns the file we were
        // given a second time under its other spelling. See Rel.
        var found = new List<string> { Rel(rel) };
        try
        {
            foreach (var r in PenumbraModMeta.ReadAllRedirects(modRoot))
            {
                if (!r.GamePath.Equals(gamePath, StringComparison.OrdinalIgnoreCase)) continue;
                var sibling = Rel(r.File);
                if (found.Contains(sibling, StringComparer.OrdinalIgnoreCase)) continue;
                if (File.Exists(Path.Combine(modRoot, Native(sibling)))) found.Add(sibling);
            }
        }
        catch (IOException) { /* the one we were given is still worth patching */ }
        return found;
    }

    /// <summary>
    /// How many files Proteus has patched anywhere in this mod, from the record.
    /// <para/>
    /// For the panel, which cannot ask the question any other way: a hair mod ships ONE MODEL PER RACE,
    /// each with its own game path, so the files served by the hairstyle currently worn are only ever a
    /// subset of what Proteus has edited in the folder. The difference between this and that subset is
    /// exactly the set of patches a per-hairstyle undo cannot reach.
    /// </summary>
    public static int PatchedCount(string modRoot) => ReadRecord(modRoot)?.Files.Count ?? 0;

    /// <summary>Whether Proteus has already fitted this exact file, and if so whether that patch is current.</summary>
    /// <param name="stale">The patch was written by an older <see cref="HatCompatSolve.Version"/>, so redoing
    /// it from the author's own backup would produce different — and by construction better — geometry.</param>
    public static bool IsPatched(string modRoot, string rel, out bool stale)
    {
        stale = false;
        rel = Rel(rel);
        var record = ReadRecord(modRoot);
        if (record?.Files.Contains(rel, StringComparer.OrdinalIgnoreCase) != true) return false;
        stale = (record.Versions.TryGetValue(rel, out var v) ? v : 0) < HatCompatSolve.Version;
        return true;
    }

    /// <summary>Work out what this hair needs, without writing anything.</summary>
    /// <param name="head">The wearer's face model, which carries the cranium the press aims at. REQUIRED:
    /// null yields a proposal marked <see cref="Proposal.Unmeasurable"/> and nothing is fitted. It used to
    /// fall back to guessing the skull from the hair — see the remarks in the body for why that is gone.</param>
    /// <param name="raceCode">The wearer's model code ("0801"), which picks the baked hat profile the
    /// cut is measured against. Taken from the hair's own game path by the caller — see
    /// <see cref="HatProfile"/>.</param>
    public static Proposal? Inspect(byte[] mdl, string rel, byte[]? head, string? raceCode = null)
    {
        // Here, so everything downstream of a proposal is canonical without having to remember: Apply
        // writes proposal.Rel into all three of the record's collections, and Revert looks the file up by
        // the same string later.
        rel = Rel(rel);
        var parts = ModelPartReader.Read(mdl);
        if (parts == null) return null;
        if (IsHatCompatible(mdl))
        {
            // Hat support of its own — but whose, and is it any good? The attribute and the shape are the
            // game's own names, so a mask an author measured for this mesh and a mask inherited wholesale
            // from the vanilla hair it was built on are indistinguishable by name. Measure what the mask
            // actually costs instead; see MeasureScalpTagging for why depth alone is the wrong test.
            //
            // Needs a head: without one there is no hat line, so the existing support cannot be judged and
            // therefore stands. That is also why this cannot move above the head check below.
            var tagging = head != null ? HatCompatSolve.MeasureScalpTagging(mdl, head) : null;
            if (tagging is { } t && t.HarmfulShare > HatCompatSolve.InheritedTagShare)
                return new Proposal(rel, parts, HatCompatSolve.Solve(mdl, parts, head, raceCode: raceCode), false,
                                    TookOver: true);

            return new Proposal(rel, parts, HatCompatSolve.Result.None, true);
        }

        // NO HEAD, NO FIT. The skull used to be guessed from the hair when the face model could not be
        // read, and that guess is not a near miss — measured against the same character's real face it put
        // the centre 66 mm low and the radius at nearly double, because what it is really measuring is the
        // hair's own extent, tails included. The cut plane then lands around ear height and takes the
        // crown, the sides and the fringe with it.
        //
        // Refusing is strictly the better failure. A hat that clips through hair is a cosmetic annoyance
        // the wearer can see and reason about; most of a hairstyle silently missing under every hat is not,
        // and it looks like the hair mod's fault.
        if (head == null)
            return new Proposal(rel, parts, HatCompatSolve.Result.None, false, Unmeasurable: true);

        return new Proposal(rel, parts, HatCompatSolve.Solve(mdl, parts, head, raceCode: raceCode), false);
    }

    /// <summary>
    /// Write the shape and the tags into <paramref name="rel"/> inside <paramref name="modRoot"/>.
    /// <para/>
    /// The attribute goes on FIRST and the shape second, and not for a structural reason — either order
    /// works, because each edit re-parses the file it is given. It reads better this way: the cut decides
    /// which geometry still exists, so it is settled before anything is said about where that geometry goes.
    /// </summary>
    /// <param name="extra">Further submeshes to tag alongside the cut. Empty in the plugin — the cut is the
    /// whole of what is dropped — and present for tests that need to exercise the tagging path directly.</param>
    public static Outcome Apply(string modRoot, byte[] mdl, Proposal proposal,
                                IReadOnlyList<ModelPart>? extra = null)
    {
        // Before the AlreadyCompatible check, because it is the stronger refusal: without a skull there is
        // no hat line, so nothing here could even tell whether the hairstyle needed anything.
        if (proposal.Unmeasurable)
            return new Outcome(false, Loc.Localize("HatCompat.Apply.NoHead",
                "Proteus could not read your character's head, so it has not changed this hairstyle."), 0);

        if (proposal.AlreadyCompatible)
            return new Outcome(false, Loc.Localize("HatCompat.Apply.Already",
                "This hairstyle already has a hat shape of its own."), 0);
        // The geometry above the hat line, which the hat hides outright. Dropping it costs nothing where
        // pressing the same hair costs a shape value per index slot. Tagged in one pass so a submesh claimed
        // twice is cut once.
        var tag = (extra ?? []).Concat(proposal.Solve.Cut).ToList();

        if (proposal.Solve.Moved.Count == 0 && tag.Count == 0)
            return new Outcome(false, Loc.Localize("HatCompat.Apply.Nothing",
                "There is nothing to change: no hair stands proud of the scalp and no part needs hiding."), 0);

        byte[] patched;
        int unaddressable = 0;
        try
        {
            patched = mdl;
            if (tag.Count > 0)
            {
                // Whatever the author tagged to vanish under a hat, forget it and say it ourselves. A hair
                // mod carrying atr_kam without a hat shape has almost always inherited the flag from the
                // vanilla hair it was built on rather than chosen it — one of the models measured here drops
                // 51% of its geometry that way, down to 70 mm below the chin, and another drops all of it.
                // That happens with or without Proteus, and it is exactly the "cut far too low" it gets
                // blamed for. The cut this pass computed is the whole answer, so it starts from a clean mask.
                //
                // Inside this branch on purpose: clearing is only defensible because something replaces it.
                // With nothing to tag — a hairstyle whose geometry above the line all sits outside the hat —
                // clearing would take away the author's answer and put none of its own back, leaving hair
                // that used to vanish under a hat clipping through it instead.
                //
                // The author's file is untouched on disk and the backup restores it, so this is undoable
                // like everything else here.
                patched = ModelAttributeWriter.ClearAttribute(patched, ScalpAttribute);

                // Cut the tails out of whatever they share a submesh with FIRST. An attribute is carried by
                // a submesh record, and a hairstyle routinely keeps its scalp cap and every one of its
                // ponytail strands in one — so tagging by submesh number hid the scalp along with the
                // tails, which in game is being bald.
                //
                // Safe to do before the shape: a split re-describes which triangles belong to which record
                // and moves no vertex and no index, so every mesh-relative number the press produced still
                // means what it meant.
                var (split, targets) = ModelAttributeWriter.IsolateParts(patched, tag);
                patched = ModelAttributeWriter.AddAttribute(split, ScalpAttribute, targets);
            }
            // THE MASK ONLY, on a take-over. The hairstyle already declares shp_hib — that is what made
            // Proteus stand down for it — and AddShape refuses a name the model already has, for good
            // reason: a second shape record under one name leaves the game to pick, and which one it picks
            // is not ours to decide. Replacing the author's shape would mean cutting records out of the
            // shape block and restitching three parallel tables, which is a great deal of surgery to buy a
            // better press.
            //
            // It buys little, because the press is not the harm. What an inherited mask does is DELETE the
            // cap, the fringe and the nape — geometry no hat covers — and that is the bald head the user
            // sees. The author's press stays, so hair still flattens under a hat; if it flattens poorly,
            // the result is hair clipping through a hat, which is the failure this whole file is written to
            // prefer (see CutReach: "hair inside a hat that goes uncut is merely pressed instead, which
            // looks like nothing, while hair cut outside one is a hole").
            if (proposal.TookOver)
            {
                if (tag.Count == 0)
                    return new Outcome(false, Loc.Localize("HatCompat.Apply.Nothing",
                        "There is nothing to change: no hair stands proud of the scalp and no part needs "
                      + "hiding."), 0);
            }
            else if (proposal.Solve.Moved.Count > 0)
            {
                patched = ModelAttributeWriter.AddShape(
                    patched, HatShape, proposal.Solve.Moved, out unaddressable);
            }
        }
        catch (ModelAttributeWriter.ModelEditException ex)
        {
            // Nothing is on disk yet, which is the whole point of patching in memory first.
            return new Outcome(false, string.Format(Loc.Localize("HatCompat.Apply.EditFailed.Fmt",
                "{0} could not be edited: {1}. Nothing has been written."), proposal.Rel, ex.Message), 0);
        }

        // ── from here on the mod is being changed ───────────────────────────
        try
        {
            Backup(modRoot, proposal.Rel);
            PenumbraModMeta.AtomicWrite(Path.Combine(modRoot, Native(proposal.Rel)), patched);

            var record = ReadRecord(modRoot) ?? new HatCompatRecord();
            if (!record.Files.Contains(proposal.Rel, StringComparer.OrdinalIgnoreCase))
                record.Files.Add(proposal.Rel);
            record.Hidden[proposal.Rel] = tag.Select(p => $"{p.Mesh}.{p.Submesh}").ToList();
            record.Versions[proposal.Rel] = HatCompatSolve.Version;
            WriteRecord(modRoot, record);
        }
        catch (Exception ex)
        {
            return new Outcome(false, string.Format(
                Loc.Localize("HatCompat.Apply.Failed.Fmt", "Writing failed: {0}"), ex.Message), 0);
        }

        return new Outcome(true, "", 1, unaddressable);
    }

    /// <summary>
    /// Put every patched model back the way its author shipped it.
    /// <para/>
    /// Backups are deleted only once restored, so a partial failure leaves the rest to retry from rather
    /// than a record naming files it can no longer recover.
    /// </summary>
    /// <param name="only">One file to undo, or null for every file Proteus patched in this mod. A hair pack
    /// routinely ships a dozen hairstyles out of one folder, and undoing the one you are wearing should not
    /// take the other eleven with it.</param>
    public static Outcome Revert(string modRoot, string? only = null)
    {
        var record = ReadRecord(modRoot);
        if (record == null || record.Files.Count == 0)
            return new Outcome(false, Loc.Localize("HatCompat.Revert.Nothing",
                "This mod has no Proteus hat-compatibility edits to undo."), 0);

        var target = only != null ? Rel(only) : null;
        var wanted = record.Files.Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(f => target == null || f.Equals(target, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (wanted.Count == 0)
            return new Outcome(false, Loc.Localize("HatCompat.Revert.Nothing",
                "This mod has no Proteus hat-compatibility edits to undo."), 0);

        var restoredFrom = new List<string>();
        var skipped = new List<string>();
        foreach (var rel in wanted)
        {
            var backup = Path.Combine(modRoot, SidecarDiscoveryService.SidecarSubdir, BackupSubdir, Native(rel));
            if (!File.Exists(backup)) { skipped.Add(rel); continue; }
            try
            {
                PenumbraModMeta.AtomicWrite(Path.Combine(modRoot, Native(rel)), File.ReadAllBytes(backup));
                restoredFrom.Add(backup);
                record.Files.RemoveAll(f => f.Equals(rel, StringComparison.OrdinalIgnoreCase));
                record.Hidden.Remove(rel);
                record.Versions.Remove(rel);
            }
            catch { skipped.Add(rel); }
        }

        if (restoredFrom.Count == 0)
            return new Outcome(false, Loc.Localize("HatCompat.Revert.NoBackups",
                "None of the original models could be found to restore."), 0);

        // The record goes only when it has nothing left to describe; otherwise it is rewritten so the
        // hairstyles still patched keep naming the backups that can restore them.
        var recordPath = Path.Combine(modRoot, SidecarDiscoveryService.SidecarSubdir, RecordFile);
        try
        {
            if (record.Files.Count == 0) File.Delete(recordPath);
            else WriteRecord(modRoot, record);
        }
        catch { /* the models are already back; a stale record only over-reports */ }

        foreach (var backup in restoredFrom)
            try { File.Delete(backup); } catch { /* harmless leftover */ }

        return new Outcome(true, "", restoredFrom.Count);
    }

    // ── the record ──────────────────────────────────────────────────────────

    /// <summary>
    /// Read the record, with every path canonicalised — the one choke point where a record written by an
    /// older build heals itself.
    /// <para/>
    /// Rebuilt rather than fixed in place, and the comparers are set here rather than trusted from the
    /// field initializers: System.Text.Json REPLACES a deserialized dictionary wholesale, so the
    /// <c>StringComparer</c> each property is constructed with never survives a load. <c>Hidden</c> was
    /// case-sensitive and <c>Versions</c> case-insensitive on paper and both were ordinal in practice.
    /// </summary>
    internal static HatCompatRecord? ReadRecord(string modRoot)
    {
        try
        {
            var path = Path.Combine(modRoot, SidecarDiscoveryService.SidecarSubdir, RecordFile);
            if (!File.Exists(path)) return null;
            var record = JsonSerializer.Deserialize<HatCompatRecord>(File.ReadAllText(path));
            if (record == null) return null;

            // Distinct AFTER canonicalising: two spellings of one file collapse to one entry here, which
            // is the whole point — otherwise Revert would leave the duplicate behind and never delete the
            // record.
            record.Files = record.Files.Select(Rel).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            var hidden = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in record.Hidden) hidden[Rel(k)] = v;
            record.Hidden = hidden;

            // The LOWEST of two colliding stamps, not the newest. Only one of the two writes is the patch
            // actually on disk and nothing here can say which, so pick by which way the mistake recovers:
            // claiming the older generation refits a patch that was already current, which costs a revert
            // and a re-solve and is otherwise invisible, while claiming the newer one leaves a patch from
            // an older solver in place with nothing that will ever look at it again.
            var versions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in record.Versions)
            {
                var key = Rel(k);
                versions[key] = versions.TryGetValue(key, out var had) ? Math.Min(had, v) : v;
            }
            record.Versions = versions;

            return record;
        }
        catch { return null; }
    }

    private static void WriteRecord(string modRoot, HatCompatRecord record)
    {
        var dir = Path.Combine(modRoot, SidecarDiscoveryService.SidecarSubdir);
        Directory.CreateDirectory(dir);
        PenumbraModMeta.AtomicWrite(Path.Combine(dir, RecordFile),
            JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>
    /// Copy a model aside before its FIRST edit, and only then — a second pass must not overwrite the
    /// pristine copy with an already-patched one, or revert would restore the previous edit instead of the
    /// author's file.
    /// </summary>
    private static void Backup(string modRoot, string rel)
    {
        var backup = Path.Combine(modRoot, SidecarDiscoveryService.SidecarSubdir, BackupSubdir, Native(rel));
        if (File.Exists(backup)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        File.Copy(Path.Combine(modRoot, Native(rel)), backup);
    }

    private static string Native(string rel) => rel.Replace('/', Path.DirectorySeparatorChar);
}
