using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using CheapLoc;
using Vec3 = Proteus.Services.SecondSkinWriter.Vec3;

namespace Proteus.Services;

/// <summary>What the brush changed in a mod, so it can be put back.</summary>
internal sealed class MeshVolumeRecord
{
    /// <summary>Model files that were edited, relative to the mod root, with forward slashes.</summary>
    [JsonPropertyName("Files")] public List<string> Files { get; set; } = [];

    /// <summary>Which generation of <see cref="MeshVolumeSolve"/> wrote each file.</summary>
    [JsonPropertyName("Versions")] public Dictionary<string, int> Versions { get; set; }
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The furthest anything was moved, in metres, per file — for the panel to report.</summary>
    [JsonPropertyName("Worst")] public Dictionary<string, float> Worst { get; set; }
        = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Writes a brush edit into a mod's own model file, and puts it back.
/// <para/>
/// The edit is positions and normals IN PLACE. Nothing changes the file's length, so no offset in the header
/// moves and none of <see cref="ModelAttributeWriter"/>'s splice-and-shift machinery is needed — the bytes
/// are copied and overwritten where they sit. That is the same property
/// <c>SecondSkinWriter.SmoothBodyNipples</c> relies on, and it is what makes this safe to do to somebody
/// else's mod.
/// <para/>
/// Arranged the way the other two writers in this project are: the model is patched in memory and only
/// written once it has succeeded, so a refusal leaves the mod untouched rather than half-edited. The
/// author's file is copied aside before the first write and the record names what was done.
/// </summary>
internal static class MeshVolumeService
{
    public const string RecordFile = "meshvolume.json";
    public const string BackupSubdir = "meshvolume-backup";

    /// <summary>
    /// Held around every backup, write and record update. Apply to other sizes saves from a worker thread while the
    /// framework thread goes on saving the open model, and the record is read, changed and written back: unguarded,
    /// one save's rewrite drops the other's entry — and an entry missing from the record is a file Undo saved changes
    /// never restores, whose backup it then deletes with the folder. Two backups of one shared material racing is the
    /// same story: the loser can copy the file the winner has already patched and keep that as "the original".
    /// Only the writing is held, never the edit's computation, so the framework thread waits at most one file's write.
    /// </summary>
    private static readonly object WriteLock = new();

    /// <param name="UnmappedSpares">Shape-key replacement vertices whose base vertex could not be found, so
    /// they keep the author's position. Enabling that shape reverts the edit on the slots it rewires, which
    /// looks exactly like the brush having missed a patch — hence a count rather than silence.</param>
    /// <param name="WindMeshesRefused">Meshes wind could not be written to because the wind channel could not
    /// be added — every declaration slot used, or a vertex record already at the format's size limit.</param>
    public sealed record Outcome(bool Ok, string Message, int FilesWritten, int UnmappedSpares = 0,
                                 int WindMeshesRefused = 0);

    /// <summary>The bytes of one edited model, plus what the caller needs to warn about.</summary>
    /// <param name="WindChannel">What adding the wind channel did, when wind was painted; null otherwise.</param>
    internal sealed record Written(byte[] Model, int UnmappedSpares, bool HasOtherLods,
                                   VertexColorWriter.Report? WindChannel = null);

    private const int BBoxSize = 32;   // min Vec4 then max Vec4

    /// <summary>
    /// Apply the brush to one model and write it into the mod.
    /// </summary>
    /// <param name="mdl">The bytes the solve was BUILT FROM — not whatever is on disk now. The panel saves
    /// repeatedly as strokes come in, and each save is those bytes plus the whole edit so far; starting from
    /// the file on disk would apply every earlier stroke a second time on every save.</param>
    /// <param name="writeUntouched">Write even when nothing is painted. An autosave after undoing back to
    /// nothing has to put the unpainted model back, or the last saved edit stays in the mod.</param>
    public static Outcome Apply(string modRoot, string rel, byte[] mdl, MeshVolumeSolve solve,
                                bool writeUntouched = false)
    {
        rel = Rel(rel);
        if (!solve.Dirty && !writeUntouched)
            return new Outcome(false, Loc.Localize("MeshVolume.Apply.Nothing",
                "Nothing has been painted, so there is nothing to save."), 0);

        Written written;
        try { written = Inflate(mdl, solve); }
        catch (ModelAttributeWriter.ModelEditException ex)
        {
            // Nothing is on disk yet, which is the whole point of editing in memory first.
            return new Outcome(false, string.Format(Loc.Localize("MeshVolume.Apply.EditFailed.Fmt",
                "{0} could not be edited: {1}. Nothing has been written."), rel, ex.Message), 0);
        }
        catch (Exception ex)
        {
            return new Outcome(false, string.Format(Loc.Localize("MeshVolume.Apply.EditFailed.Fmt",
                "{0} could not be edited: {1}. Nothing has been written."), rel, ex.Message), 0);
        }

        // ── from here on the mod is being changed ───────────────────────────
        try
        {
            lock (WriteLock)
            {
                Backup(modRoot, rel);
                PenumbraModMeta.AtomicWrite(Path.Combine(modRoot, Native(rel)), written.Model);

                var record = ReadRecord(modRoot) ?? new MeshVolumeRecord();
                if (!record.Files.Contains(rel, StringComparer.OrdinalIgnoreCase)) record.Files.Add(rel);
                record.Versions[rel] = MeshVolumeSolve.Version;
                record.Worst[rel] = solve.Worst;
                WriteRecord(modRoot, record);
            }
        }
        catch (Exception ex)
        {
            return new Outcome(false, string.Format(
                Loc.Localize("MeshVolume.Apply.Failed.Fmt", "Writing failed: {0}"), ex.Message), 0);
        }

        return new Outcome(true, "", 1, written.UnmappedSpares, written.WindChannel?.MeshesRefused ?? 0);
    }

    /// <summary>
    /// The edit itself, on bytes alone — no mod folder, no record. Separate so it can be tested against a
    /// model without a folder on disk around it.
    /// </summary>
    internal static Written Inflate(byte[] mdl, MeshVolumeSolve solve)
    {
        SecondSkinWriter.Source src;
        try { src = SecondSkinWriter.Parse(mdl); }
        catch (Exception ex)
        {
            throw new ModelAttributeWriter.ModelEditException(
                $"this model could not be read ({ex.Message})");
        }

        // A face's neck morph table is NOT refused. It is a handful of per-bone adjustments (position, normal,
        // bone indices), not a copy of the vertices, so moving vertices does not put it out of step; every write
        // here is in place or in the vertex buffer, and Parse now walks past it and the Patch 7.2 face table to
        // find the bounding boxes. Faces are sculpted with small brushes.

        if (src.ModelBBoxAt <= 0 || src.ModelBBoxAt + 4 * BBoxSize > mdl.Length)
            throw new ModelAttributeWriter.ModelEditException(
                "this model's bounding boxes are not where the format says they are");

        var o = (byte[])mdl.Clone();

        // Every mesh the concatenated arrays came from, in the order they were read — the only route back
        // from a vertex the brush moved to the bytes that describe it.
        var spanOf = new Dictionary<int, MeshSpan>();
        foreach (var span in solve.Spans) spanOf[span.Mesh] = span;

        // Shape replacement vertices are found FIRST and then excluded from the main pass, because a spare
        // must be displaced exactly once and by its base vertex's delta — not by its own.
        //
        // Two ways the naive order goes wrong, both silent. A spare is an ordinary vertex as far as the
        // reader is concerned, so it welds by POSITION like any other — and it sits at the shape's target
        // pose, which may coincide with a node the brush moved, giving it a displacement that has nothing to
        // do with the vertex it stands in for. And where a model names the same vertex as both a base and a
        // replacement, the two passes would each add the delta, moving it twice as far as anything around it.
        var spares = SpareVertices(mdl, src);

        foreach (var span in solve.Spans)
        {
            int mo = src.MeshStart + span.Mesh * 36;
            if (mo + 36 > mdl.Length) continue;

            var decl = span.Mesh < src.Decls.Length ? src.Decls[span.Mesh] : [];
            SecondSkinWriter.VElem? posEl = null, nrmEl = null;
            foreach (var el in decl)
            {
                if (el.Usage == SecondSkinWriter.UsePosition) posEl = el;
                else if (el.Usage == SecondSkinWriter.UseNormal) nrmEl = el;
            }
            if (posEl is not { } pe) continue;

            // WriteXYZ has no default arm: handed a type it does not know it writes nothing and returns,
            // leaving the vertex exactly where it was. The edit would then be structurally perfect and move
            // the model nowhere, which is the worst kind of bug to go looking for.
            if (Array.IndexOf(ModelAttributeWriter.PositionTypes, pe.Type) < 0)
                throw new ModelAttributeWriter.ModelEditException(
                    $"mesh {span.Mesh} stores its positions in a format this cannot write (type {pe.Type})");

            WriteSpan(o, src, mo, span, pe, nrmEl, solve, spares);
        }

        int unmapped = CarrySpares(o, mdl, src, spanOf, solve);
        GrowExtents(o, src, solve.Worst);

        // WIND LAST, because adding the channel changes the file's length and every in-place write above
        // addresses the original layout. Only when wind was actually painted: a model the user only pulled keeps
        // its vertex layout exactly as the author shipped it.
        VertexColorWriter.Report? wind = null;
        if (solve.WindEdited)
        {
            o = VertexColorWriter.EnsureSecondColor(o, out var report);
            VertexColorWriter.WriteWind(o, solve.Spans, solve.WindAt);
            wind = report;
        }

        ushort lodCount = BitConverter.ToUInt16(mdl, src.Mh + 22);
        return new Written(o, unmapped, lodCount > 1, wind);
    }

    /// <summary>
    /// Every vertex named as a shape's replacement, per mesh — the spares, which the main pass must leave
    /// alone. Keyed <c>(mesh &lt;&lt; 32) | localVertex</c>.
    /// </summary>
    private static HashSet<long> SpareVertices(byte[] mdl, SecondSkinWriter.Source src)
    {
        var spares = new HashSet<long>();
        if (src.Shapes.Count == 0) return spares;

        int end = Math.Min(src.Lod0MeshIndex + src.Lod0MeshCount, src.MeshCount);
        foreach (var (_, entries) in src.Shapes)
        foreach (var entry in entries)
        for (int m = src.Lod0MeshIndex; m < end; m++)
        {
            int mo = src.MeshStart + m * 36;
            if (mo + 36 > mdl.Length) continue;
            uint start = BitConverter.ToUInt32(mdl, mo + 16), count = BitConverter.ToUInt32(mdl, mo + 4);
            if (entry.MeshIndexOffset < start || entry.MeshIndexOffset >= start + count) continue;

            foreach (var (_, replace) in entry.Values) spares.Add(((long)m << 32) | replace);
            break;
        }
        return spares;
    }

    private static void WriteSpan(byte[] o, SecondSkinWriter.Source src, int mo, MeshSpan span,
                                  SecondSkinWriter.VElem pe, SecondSkinWriter.VElem? nrmEl,
                                  MeshVolumeSolve solve, HashSet<long> spares)
    {
        uint[] vbo =
        {
            BitConverter.ToUInt32(o, mo + 20), BitConverter.ToUInt32(o, mo + 24),
            BitConverter.ToUInt32(o, mo + 28),
        };
        byte[] bs = { o[mo + 32], o[mo + 33], o[mo + 34] };
        if (pe.Stream > 2 || bs[pe.Stream] == 0) return;

        Span<float> tmp = stackalloc float[4];
        for (int k = 0; k < span.Count; k++)
        {
            if (spares.Contains(((long)span.Mesh << 32) | (uint)k)) continue;

            var d = solve.DeltaAt(span.BaseVertex + k);
            if (d.X == 0f && d.Y == 0f && d.Z == 0f) continue;

            int pa = (int)(src.Vb + vbo[pe.Stream]) + k * bs[pe.Stream] + pe.Offset;
            if (pa < 0 || pa + 16 > o.Length) continue;

            // Read the stored position and add the displacement, rather than writing the solve's own idea of
            // where the vertex started. They agree today — the solve decoded these very bytes — but only one
            // of the two is what the game will read, and a half-float position round-trips lossily.
            SecondSkinWriter.ReadTyped(o, pa, pe.Type, tmp);
            SecondSkinWriter.WriteXYZ(o, pa, pe.Type, tmp[0] + d.X, tmp[1] + d.Y, tmp[2] + d.Z);

            // Normals too. A stale normal does not merely mis-shade: anything that cuts a shell from this
            // surface offsets along it, so the garment above would splay where the normal still describes
            // the shape before the edit.
            if (nrmEl is not { } ne || ne.Stream > 2 || bs[ne.Stream] == 0) continue;
            int na = (int)(src.Vb + vbo[ne.Stream]) + k * bs[ne.Stream] + ne.Offset;
            if (na < 0 || na + 16 > o.Length) continue;

            var n = solve.NormalAt(span.BaseVertex + k);
            if (n.X == 0f && n.Y == 0f && n.Z == 0f) continue;
            SecondSkinWriter.WriteNormal(o, na, ne.Type, n.X, n.Y, n.Z);
        }
    }

    /// <summary>
    /// Move each shape key's replacement vertices with the base vertices they stand in for.
    /// <para/>
    /// A shape does not store offsets: every <c>ShapeValue</c> rewires one index-buffer slot to a whole
    /// spare vertex living in the same mesh's buffer. Those spares carry the shape's target pose and are
    /// named by no triangle, so the brush never sees them — and left behind, enabling the shape puts the
    /// slots it rewires back exactly where the author had them. On a garment that means turning a body
    /// slider on undoes the clipping fix along whatever the slider touches, which is most of the garment.
    /// <para/>
    /// The spare takes its BASE vertex's displacement rather than being solved for itself, because the shape
    /// is a difference from the base pose and that difference is what has to survive the edit.
    /// </summary>
    private static int CarrySpares(byte[] o, byte[] mdl, SecondSkinWriter.Source src,
                                   Dictionary<int, MeshSpan> spanOf, MeshVolumeSolve solve)
    {
        int unmapped = 0;
        if (src.Shapes.Count == 0) return 0;
        Span<float> tmp = stackalloc float[4];

        // Which mesh owns an index-buffer window, by the mesh's own index range.
        var meshes = new List<(int Mesh, uint Start, uint Count)>();
        int end = Math.Min(src.Lod0MeshIndex + src.Lod0MeshCount, src.MeshCount);
        for (int m = src.Lod0MeshIndex; m < end; m++)
        {
            int mo = src.MeshStart + m * 36;
            if (mo + 36 > mdl.Length) continue;
            meshes.Add((m, BitConverter.ToUInt32(mdl, mo + 16), BitConverter.ToUInt32(mdl, mo + 4)));
        }

        foreach (var (_, entries) in src.Shapes)
        foreach (var entry in entries)
        {
            int ownerMesh = -1;
            foreach (var (m, start, count) in meshes)
                if (entry.MeshIndexOffset >= start && entry.MeshIndexOffset < start + count)
                { ownerMesh = m; break; }

            if (ownerMesh < 0 || !spanOf.TryGetValue(ownerMesh, out var span))
            {
                unmapped += entry.Values.Length;
                continue;
            }

            int mo = src.MeshStart + ownerMesh * 36;
            var decl = ownerMesh < src.Decls.Length ? src.Decls[ownerMesh] : [];
            SecondSkinWriter.VElem? posEl = null, nrmEl = null;
            foreach (var el in decl)
            {
                if (el.Usage == SecondSkinWriter.UsePosition) posEl = el;
                else if (el.Usage == SecondSkinWriter.UseNormal) nrmEl = el;
            }
            if (posEl is not { } pe) { unmapped += entry.Values.Length; continue; }

            uint[] vbo =
            {
                BitConverter.ToUInt32(mdl, mo + 20), BitConverter.ToUInt32(mdl, mo + 24),
                BitConverter.ToUInt32(mdl, mo + 28),
            };
            byte[] bs = { mdl[mo + 32], mdl[mo + 33], mdl[mo + 34] };
            if (pe.Stream > 2 || bs[pe.Stream] == 0) { unmapped += entry.Values.Length; continue; }

            foreach (var (baseIdx, replace) in entry.Values)
            {
                long slot = entry.MeshIndexOffset + baseIdx;
                if (src.Ib + slot * 2 + 2 > mdl.Length) { unmapped++; continue; }
                int baseVertex = BitConverter.ToUInt16(mdl, src.Ib + (int)slot * 2);
                if (baseVertex >= span.Count || replace >= span.Count) { unmapped++; continue; }

                var d = solve.DeltaAt(span.BaseVertex + baseVertex);
                if (d.X == 0f && d.Y == 0f && d.Z == 0f) continue;

                int pa = (int)(src.Vb + vbo[pe.Stream]) + replace * bs[pe.Stream] + pe.Offset;
                if (pa < 0 || pa + 16 > o.Length) { unmapped++; continue; }

                SecondSkinWriter.ReadTyped(o, pa, pe.Type, tmp);
                SecondSkinWriter.WriteXYZ(o, pa, pe.Type, tmp[0] + d.X, tmp[1] + d.Y, tmp[2] + d.Z);

                if (nrmEl is not { } ne || ne.Stream > 2 || bs[ne.Stream] == 0) continue;
                int na = (int)(src.Vb + vbo[ne.Stream]) + replace * bs[ne.Stream] + ne.Offset;
                if (na < 0 || na + 16 > o.Length) continue;
                var n = solve.NormalAt(span.BaseVertex + baseVertex);
                if (n.X == 0f && n.Y == 0f && n.Z == 0f) continue;
                SecondSkinWriter.WriteNormal(o, na, ne.Type, n.X, n.Y, n.Z);
            }
        }
        return unmapped;
    }

    /// <summary>
    /// Widen every stored extent by the furthest anything moved.
    /// <para/>
    /// Nothing in this project ever recomputed a bounding box, and an inflate is the one edit that makes
    /// that matter: it moves geometry monotonically outward, which is exactly the direction that understates
    /// a box. Understating a radius or a clip distance makes the game cull the model while the body it
    /// belongs to is still on screen — blinking out at an angle or a distance, with nothing in any log.
    /// <para/>
    /// Grown by a known amount rather than re-measured from the vertices. Re-measuring would also quietly
    /// correct extents that were already understated for unrelated reasons, which is a different change than
    /// the one the user asked for, on a file that is not ours.
    /// </summary>
    private static void GrowExtents(byte[] o, SecondSkinWriter.Source src, float by)
    {
        if (by <= 0f) return;

        for (int box = 0; box < 4; box++) Grow(src.ModelBBoxAt + box * BBoxSize);
        for (int b = 0; b < src.BoneCount; b++) Grow(src.BoneBBoxAt + b * BBoxSize);

        // Only a value already in use. A zero or negative radius is a sentinel rather than a measurement,
        // and adding a millimetre to it would change what it means rather than widening it.
        Bump(src.Mh);          // Radius
        Bump(src.Mh + 28);     // ModelClipOutDistance
        Bump(src.Mh + 32);     // ShadowClipOutDistance

        void Grow(int at)
        {
            if (at < 0 || at + BBoxSize > o.Length) return;

            // An all-zero box is unused — the water and vertical-fog boxes on a character model are — and
            // turning one into a tiny box around the origin is inventing a volume, not widening one.
            bool any = false;
            for (int c = 0; c < 8 && !any; c++) any = BitConverter.ToSingle(o, at + c * 4) != 0f;
            if (!any) return;

            for (int c = 0; c < 3; c++)
            {
                int lo = at + c * 4, hi = at + 16 + c * 4;
                BitConverter.GetBytes(BitConverter.ToSingle(o, lo) - by).CopyTo(o, lo);
                BitConverter.GetBytes(BitConverter.ToSingle(o, hi) + by).CopyTo(o, hi);
            }
        }

        void Bump(int at)
        {
            if (at < 0 || at + 4 > o.Length) return;
            float v = BitConverter.ToSingle(o, at);
            if (v > 0f) BitConverter.GetBytes(v + by).CopyTo(o, at);
        }
    }

    // ── wind: the garment's materials ───────────────────────────────────────

    /// <summary>character.shpk's <c>g_VertexMovementScale</c>: how far the wind moves a vertex, times its wind.</summary>
    public const uint VertexMovementScaleId = 0x641E0F22;

    /// <summary>character.shpk's <c>g_VertexMovementMaxLength</c>: the furthest the wind may move a vertex.</summary>
    public const uint VertexMovementMaxLengthId = 0xD26FF0AE;

    public const float WindMovementScale = 100f, WindMovementMaxLength = 1f;

    /// <param name="Changed">Material files rewritten.</param>
    /// <param name="Missing">Material files that do not carry the movement constants at all, left unchanged.</param>
    /// <param name="NotInMod">Materials the model uses that this mod does not ship, which only the game has.</param>
    public sealed record MaterialOutcome(int Changed, int Missing, int NotInMod);

    /// <summary>
    /// Set the vertex movement constants on every material the painted wind is drawn with: the wind channel alone
    /// moves nothing where a material holds the movement at 0, which is how many mods ship their gear materials.
    /// <para/>
    /// Found by NAME among the mod's own redirects — every variant and option that ships it — because the model
    /// only names <c>/mt_….mtrl</c> and the variant folder is the IMC's to choose. Each file is backed up and
    /// recorded like the models, so Undo saved changes puts it back too. Only constants the material already
    /// declares are changed: adding one means rebuilding the file's layout, and one that is absent falls back to the
    /// shader's own default rather than 0.
    /// </summary>
    /// <param name="materialNames">The cloth materials the brushed model uses, as the model names them.</param>
    public static MaterialOutcome ApplyWindMaterials(string modRoot, IEnumerable<string> materialNames,
                                                     IReadOnlyList<PenumbraModMeta.Redirect> redirects)
    {
        lock (WriteLock) return ApplyWindMaterialsLocked(modRoot, materialNames, redirects);
    }

    private static MaterialOutcome ApplyWindMaterialsLocked(string modRoot, IEnumerable<string> materialNames,
                                                            IReadOnlyList<PenumbraModMeta.Redirect> redirects)
    {
        int changed = 0, missing = 0, notInMod = 0;
        MeshVolumeRecord? record = null;

        foreach (var name in materialNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var file = "/" + name.TrimStart('/');
            var rels = redirects
                .Where(r => r.GamePath.EndsWith(file, StringComparison.OrdinalIgnoreCase)
                         && r.File.EndsWith(".mtrl", StringComparison.OrdinalIgnoreCase))
                .Select(r => Rel(r.File))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (rels.Count == 0) { notInMod++; continue; }

            foreach (var rel in rels)
            {
                var path = Path.Combine(modRoot, Native(rel));
                if (!File.Exists(path)) continue;
                var before = File.ReadAllBytes(path);
                var (withScale, hasScale) = TextureLoader.PatchConstantValues(before, VertexMovementScaleId, WindMovementScale);
                var (after, hasMax) = TextureLoader.PatchConstantValues(withScale, VertexMovementMaxLengthId, WindMovementMaxLength);
                if (!hasScale || !hasMax) missing++;
                if (after.AsSpan().SequenceEqual(before)) continue;

                Backup(modRoot, rel);
                PenumbraModMeta.AtomicWrite(path, after);
                record ??= ReadRecord(modRoot) ?? new MeshVolumeRecord();
                if (!record.Files.Contains(rel, StringComparer.OrdinalIgnoreCase)) record.Files.Add(rel);
                record.Versions[rel] = MeshVolumeSolve.Version;
                changed++;
            }
        }

        if (record != null) WriteRecord(modRoot, record);
        return new MaterialOutcome(changed, missing, notInMod);
    }

    // ── the record, the backup and the way back ─────────────────────────────

    public static int PatchedCount(string modRoot) => ReadRecord(modRoot)?.Files.Count ?? 0;

    public static bool IsPatched(string modRoot, string rel)
        => ReadRecord(modRoot)?.Files.Contains(Rel(rel), StringComparer.OrdinalIgnoreCase) == true;

    /// <summary>
    /// Put every brushed model back the way its author shipped it.
    /// <para/>
    /// Refuses while a LATER feature still holds a backup of the same file, and that guard is the whole
    /// reason this method is more than a copy. Toggles, hat compatibility and the brush each back the file
    /// up separately, so every feature's idea of "the original" is whatever the previous one left. Undoing
    /// out of order restores bytes from before a change that has already been undone — a Penumbra switch
    /// left pointing at an attribute the restore removed, which silently does nothing.
    /// </summary>
    public static Outcome Revert(string modRoot)
    {
        lock (WriteLock) return RevertLocked(modRoot);
    }

    private static Outcome RevertLocked(string modRoot)
    {
        var record = ReadRecord(modRoot);
        if (record == null || record.Files.Count == 0)
            return new Outcome(false, Loc.Localize("MeshVolume.Revert.Nothing",
                "The brush has not changed anything in this mod."), 0);

        foreach (var rel in record.Files)
            if (ModelBackupOrder.LaterFeature(modRoot, BackupSubdir, rel) is { } other)
                return new Outcome(false, string.Format(Loc.Localize("MeshVolume.Revert.Blocked.Fmt",
                    "Undo {0} first: it was applied to the same model after the brush was, so putting the "
                  + "brush back now would also undo it."), other), 0);

        int restored = 0;
        var reason = "";
        foreach (var rel in record.Files.Distinct(StringComparer.OrdinalIgnoreCase).ToList())
        {
            var backup = Path.Combine(modRoot, SidecarDiscoveryService.SidecarSubdir, BackupSubdir, Native(rel));
            if (!File.Exists(backup)) { reason = rel; continue; }
            try
            {
                PenumbraModMeta.AtomicWrite(Path.Combine(modRoot, Native(rel)), File.ReadAllBytes(backup));
                record.Files.RemoveAll(f => f.Equals(rel, StringComparison.OrdinalIgnoreCase));
                record.Versions.Remove(rel);
                record.Worst.Remove(rel);
                restored++;
            }
            catch { reason = rel; }
        }

        if (restored == 0)
            return new Outcome(false, Loc.Localize("MeshVolume.Revert.NoBackups",
                "None of the original models could be found to restore."), 0);

        // The record goes only when it has nothing left to describe, and the backups go LAST — a throw
        // between the two would otherwise leave a record naming backups that no longer exist.
        var recordPath = Path.Combine(modRoot, SidecarDiscoveryService.SidecarSubdir, RecordFile);
        try
        {
            if (record.Files.Count == 0) File.Delete(recordPath);
            else WriteRecord(modRoot, record);
        }
        catch { /* the models are already back; a stale record only over-reports */ }

        if (record.Files.Count == 0)
            foreach (var dir in new[] { Path.Combine(modRoot, SidecarDiscoveryService.SidecarSubdir, BackupSubdir) })
                try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { /* harmless */ }

        _ = reason;
        return new Outcome(true, "", restored);
    }

    /// <summary>Copy the author's file aside, ONCE — a second pass must not overwrite the pristine copy.</summary>
    private static void Backup(string modRoot, string rel)
    {
        var backup = Path.Combine(modRoot, SidecarDiscoveryService.SidecarSubdir, BackupSubdir, Native(rel));
        if (File.Exists(backup)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        File.Copy(Path.Combine(modRoot, Native(rel)), backup);
    }

    internal static MeshVolumeRecord? ReadRecord(string modRoot)
    {
        var at = Path.Combine(modRoot, SidecarDiscoveryService.SidecarSubdir, RecordFile);
        try
        {
            if (!File.Exists(at)) return null;
            var record = JsonSerializer.Deserialize<MeshVolumeRecord>(File.ReadAllText(at));
            if (record == null) return null;

            // Canonicalised on the way in, the one choke point — a record written with native separators
            // still names the same file, and every comparison here is a plain string compare.
            record.Files = record.Files.Select(Rel).ToList();
            record.Versions = record.Versions.ToDictionary(kv => Rel(kv.Key), kv => kv.Value,
                                                           StringComparer.OrdinalIgnoreCase);
            record.Worst = record.Worst.ToDictionary(kv => Rel(kv.Key), kv => kv.Value,
                                                     StringComparer.OrdinalIgnoreCase);
            return record;
        }
        catch { return null; }
    }

    private static void WriteRecord(string modRoot, MeshVolumeRecord record)
    {
        var at = Path.Combine(modRoot, SidecarDiscoveryService.SidecarSubdir, RecordFile);
        Directory.CreateDirectory(Path.GetDirectoryName(at)!);
        PenumbraModMeta.AtomicWrite(at, JsonSerializer.Serialize(record, new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        }));
    }

    /// <summary>The one spelling of a mod-relative path this class compares, records and looks up by.</summary>
    internal static string Rel(string rel) => rel.Replace('\\', '/').TrimStart('/');

    private static string Native(string rel) => rel.Replace('/', Path.DirectorySeparatorChar);
}
