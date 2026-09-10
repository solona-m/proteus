using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;

namespace Proteus.Services;

/// <summary>
/// Edits a .mdl's STRUCTURE: splits a submesh, and adds an attribute to the table so the game can switch
/// that submesh off. Together with an IMC group over the same attribute, this is what turns geometry an
/// author welded on permanently into an ordinary Penumbra checkbox.
/// <para/>
/// Both edits grow the file, and everything hard about them is the consequence. The model format puts its
/// tables one after another with no offsets between them — each is found by stepping over the last — so an
/// insert relocates everything after it, and the handful of genuinely ABSOLUTE offsets have to be moved to
/// match. There are exactly eight of those and <see cref="Shift"/> owns all of them; get one wrong and the
/// model either fails to load or renders at the wrong LOD, which is why the shift is one function and not
/// spread across the two edits.
/// <para/>
/// Nothing here reorders an index entry, and that is deliberate rather than incidental. A shape key
/// (<c>ShapeValue.BaseIndicesIndex</c>) addresses a POSITION in the index buffer, so permuting the buffer
/// silently breaks every body slider the garment supports. <see cref="SplitSubmesh"/> therefore cuts a
/// submesh at the run boundaries it already has instead of gathering triangles together.
/// </summary>
public static class ModelAttributeWriter
{
    /// <summary>
    /// Attribute masks are 32 bits wide, so a model already carrying 32 attributes has nowhere to put
    /// another. Well above the ten an IMC entry can actually drive — this is the format's limit, not the
    /// budget the user sees.
    /// </summary>
    public const int MaxAttributes = 32;

    /// <summary>
    /// How many pieces one submesh may be cut into.
    /// <para/>
    /// A split makes one record per CONTIGUOUS RUN of triangles, and an island that interleaves with its
    /// neighbours triangle by triangle would want one record each. That is legal but absurd, and a sign the
    /// island split found something that is not really a separate object. Refused rather than written.
    /// <para/>
    /// Generous, because a record costs sixteen bytes and models routinely carry dozens of submeshes: the
    /// bound is here to catch geometry that is pathologically interleaved, not to second-guess a garment
    /// whose author happened to export its straps out of order.
    /// </summary>
    public const int MaxRuns = 256;

    public sealed class ModelEditException(string message) : InvalidOperationException(message);

    // ── attributes ──────────────────────────────────────────────────────────

    /// <summary>
    /// Add <paramref name="attributeName"/> to the model's attribute table and tag every named submesh with
    /// it. The submeshes then draw only while the attribute is enabled, which an IMC entry decides.
    /// <para/>
    /// The name goes at the END of the table and the bit is its position there, because a submesh's mask
    /// indexes the table positionally. Which IMC bit ends up driving it is a different question with a
    /// different answer — the trailing letter of the NAME, see <c>SecondSkinService.PartAttributeBit</c> —
    /// so the two never have to agree and the caller picks the letter.
    /// </summary>
    /// <param name="targets">(mesh index, submesh index within that mesh) pairs.</param>
    public static byte[] AddAttribute(
        byte[] mdl, string attributeName, IReadOnlyCollection<(int Mesh, int Submesh)> targets)
    {
        var src = SecondSkinWriter.Parse(mdl);
        int attrCount = src.AttrNames.Length;
        if (attrCount >= MaxAttributes)
            throw new ModelEditException(
                $"this model already declares {attrCount} attributes, which is all a submesh mask can hold");
        if (src.AttrNames.Contains(attributeName, StringComparer.Ordinal))
            throw new ModelEditException($"this model already declares an attribute named {attributeName}");

        // Padded to four bytes so every table after the string block keeps its alignment. The tables are
        // read by byte offset and would parse either way, but a u32 array landing on an odd address is not
        // something to hand the game to find out about.
        var text = Encoding.ASCII.GetBytes(attributeName);
        int nameLen = text.Length + 1;
        int pad = (4 - nameLen % 4) % 4;
        var strBytes = new byte[nameLen + pad];
        text.CopyTo(strBytes, 0);

        uint nameOffset = src.StrSize;                       // relative to the string block, as attrs are
        int insertStr = src.StrBlock + (int)src.StrSize;     // end of the string block
        int insertOff = src.AttrStart + attrCount * 4;       // end of the attribute offset table

        var offBytes = new byte[4];
        BitConverter.TryWriteBytes(offBytes, nameOffset);

        var o = Splice(mdl, [(insertStr, strBytes), (insertOff, offBytes)]);
        int dStr = strBytes.Length, delta = dStr + 4;

        W32(o, src.DeclEnd + 4, src.StrSize + (uint)dStr);          // string block size
        // The string COUNT, which the game ignores and every other reader does not: Penumbra and TexTools
        // walk exactly this many NUL-terminated strings and then resolve a table's offset against the list
        // they built (MdlFile.LoadStrings). Leave it stale and the new name is off the end of that list, so
        // the attribute reads back nameless in both — while working perfectly in game.
        W16(o, src.DeclEnd, (ushort)(BitConverter.ToUInt16(o, src.DeclEnd) + 1));
        W16(o, src.Mh + dStr + 6, (ushort)(attrCount + 1));         // attribute count
        Shift(o, src.LodStart + dStr, delta);

        // The NEW file's coordinates, and the two tables did NOT move by the same amount. The mesh table sits
        // BETWEEN the two inserts — the attribute offsets go in at its far end — so it follows only the
        // string block's growth, while the submeshes are behind both and follow the full delta.
        int meshStart = src.MeshStart + dStr;
        int submeshStart = src.SubmeshStart + delta;
        uint bit = 1u << attrCount;

        foreach (var (mesh, submesh) in targets)
        {
            int mo = meshStart + mesh * 36;
            ushort subIdx = BitConverter.ToUInt16(o, mo + 10), subCount = BitConverter.ToUInt16(o, mo + 12);
            if (submesh < 0 || submesh >= subCount)
                throw new ModelEditException($"mesh {mesh} has no submesh {submesh}");
            int ss = submeshStart + (subIdx + submesh) * 16;
            W32(o, ss + 8, BitConverter.ToUInt32(o, ss + 8) | bit);
        }
        return o;
    }

    // ── splitting ───────────────────────────────────────────────────────────

    /// <summary>
    /// Cut one submesh into several, so part of it can be tagged on its own.
    /// <para/>
    /// The cut follows the CONTIGUOUS RUNS of <paramref name="ordinals"/> within the submesh's own index
    /// range: nothing is moved, only described differently. Every new record inherits the original's
    /// attribute mask and its bone window, so a split on its own changes precisely nothing about how the
    /// model draws — which is the property that makes it safe to do before knowing whether the user will
    /// keep the toggle.
    /// </summary>
    /// <param name="ordinals">Triangle ordinals within the submesh that belong to the piece being split
    /// out — <see cref="ModelPart.Ordinals"/>.</param>
    /// <returns>The new file, and which of the mesh's submesh indices now hold those triangles.</returns>
    public static (byte[] Model, List<int> Submeshes) SplitSubmesh(
        byte[] mdl, int mesh, int submesh, IReadOnlySet<int> ordinals)
    {
        var (o, byGroup) = SplitSubmesh(mdl, mesh, submesh, t => ordinals.Contains(t) ? 0 : -1);
        return (o, byGroup.TryGetValue(0, out var subs) ? subs : []);
    }

    /// <summary>
    /// The general split: <paramref name="groupOf"/> labels each triangle ordinal, and the submesh is cut so
    /// that no record mixes two labels.
    /// <para/>
    /// More than two labels is not hypothetical — two switches can each claim a different island of the same
    /// submesh, and cutting for one at a time would have the second split re-cut records the first had just
    /// made. Doing it in one pass keeps the record count to the runs that are genuinely there.
    /// </summary>
    /// <returns>The new file, and, per label, the submesh indices now holding its triangles.</returns>
    public static (byte[] Model, Dictionary<int, List<int>> ByGroup) SplitSubmesh(
        byte[] mdl, int mesh, int submesh, Func<int, int> groupOf)
    {
        var src = SecondSkinWriter.Parse(mdl);
        int mo = src.MeshStart + mesh * 36;
        ushort subIdx = BitConverter.ToUInt16(mdl, mo + 10), subCount = BitConverter.ToUInt16(mdl, mo + 12);
        if (submesh < 0 || submesh >= subCount)
            throw new ModelEditException($"mesh {mesh} has no submesh {submesh}");

        int ss = src.SubmeshStart + (subIdx + submesh) * 16;
        uint so = BitConverter.ToUInt32(mdl, ss), sc = BitConverter.ToUInt32(mdl, ss + 4);
        int tris = (int)(sc / 3);

        // Maximal runs of one label, in order.
        var runs = new List<(int Start, int Count, int Group)>();
        for (int t = 0; t < tris;)
        {
            int group = groupOf(t);
            int start = t;
            while (t < tris && groupOf(t) == group) t++;
            runs.Add((start, t - start, group));
        }

        if (runs.Count == 0) throw new ModelEditException("that submesh has no triangles");
        if (runs.Count > MaxRuns)
            throw new ModelEditException(
                $"those triangles are interleaved with the rest of the part across {runs.Count} runs, which "
              + "is too fragmented to split cleanly");

        // Already its own submesh — nothing to cut, and inserting a zero-length record would be worse than
        // doing nothing.
        if (runs.Count == 1)
            return (mdl, new Dictionary<int, List<int>> { [runs[0].Group] = [submesh] });

        var mask = BitConverter.ToUInt32(mdl, ss + 8);
        ushort boneStart = BitConverter.ToUInt16(mdl, ss + 12), boneCount = BitConverter.ToUInt16(mdl, ss + 14);

        var records = new byte[runs.Count][];
        var byGroup = new Dictionary<int, List<int>>();
        for (int i = 0; i < runs.Count; i++)
        {
            var r = new byte[16];
            W32(r, 0, so + (uint)(runs[i].Start * 3));
            W32(r, 4, (uint)(runs[i].Count * 3));
            W32(r, 8, mask);
            W16(r, 12, boneStart);
            W16(r, 14, boneCount);
            records[i] = r;
            if (!byGroup.TryGetValue(runs[i].Group, out var list)) byGroup[runs[i].Group] = list = [];
            list.Add(submesh + i);
        }

        // The first run overwrites the original record in place; the rest are inserted after it, so nothing
        // before this submesh moves at all.
        int added = runs.Count - 1;
        var extra = new byte[added * 16];
        for (int i = 1; i < runs.Count; i++) records[i].CopyTo(extra, (i - 1) * 16);

        var o = Splice(mdl, [(ss + 16, extra)]);
        records[0].CopyTo(o, ss);

        int delta = extra.Length;
        W16(o, src.Mh + 8, (ushort)(BitConverter.ToUInt16(o, src.Mh + 8) + added));   // model submesh count
        W16(o, mo + 12, (ushort)(subCount + added));                                  // this mesh's count

        // Every mesh whose submeshes sit after these now starts later in the table. Compared on the ORIGINAL
        // submeshIndex, not on the mesh number: a model is not obliged to list its meshes in submesh order,
        // and one that does not would otherwise have a mesh renumbered onto another's records.
        for (int m = 0; m < src.MeshCount; m++)
        {
            if (m == mesh) continue;
            int other = src.MeshStart + m * 36;   // the mesh table is before the insert, so it has not moved
            ushort otherIdx = BitConverter.ToUInt16(o, other + 10);
            if (otherIdx > subIdx + submesh) W16(o, other + 10, (ushort)(otherIdx + added));
        }

        Shift(o, src.LodStart, delta);
        return (o, byGroup);
    }

    /// <summary>
    /// Cut a submesh by label like <see cref="SplitSubmesh(byte[], int, int, Func{int, int})"/>, but REORDER
    /// its triangles first so that each label ends up contiguous.
    /// <para/>
    /// The plain split can only cut where the labels already change, because it describes the geometry
    /// differently without moving any of it. That is the right contract for a mesh toggle, where the pieces
    /// being separated are islands an author modelled as units and are laid out together. It is the wrong
    /// one for a set chosen by GEOMETRY: "every triangle above the hat line" cuts across the author's layout
    /// completely, and on a real hairstyle it came out as 264 interleaved runs — refused as too fragmented,
    /// which is why hiding ponytails could not be applied at all.
    /// <para/>
    /// Reordering is safe because a submesh is drawn as a triangle LIST: each triple stands alone, so
    /// permuting whole triples changes nothing about what is drawn. No vertex moves and no index value
    /// changes — only the order the triples sit in. Relative order WITHIN a label is preserved, so a model
    /// whose labels are already contiguous comes back byte-identical and this costs nothing.
    /// <para/>
    /// The one thing that does care about index ORDER is a shape: a <c>ShapeValue</c> names an index-buffer
    /// slot. Existing shapes are remapped through the same permutation below, which is exact — but note that
    /// this must run BEFORE <see cref="AddShape"/>, never after, since a shape written against the old order
    /// would be silently rewired to the wrong corners.
    /// </summary>
    /// <returns>The new file, and, per label, the submesh indices now holding its triangles.</returns>
    public static (byte[] Model, Dictionary<int, List<int>> ByGroup) RegroupSubmesh(
        byte[] mdl, int mesh, int submesh, Func<int, int> groupOf)
    {
        var src = SecondSkinWriter.Parse(mdl);
        int mo = src.MeshStart + mesh * 36;
        ushort subIdx = BitConverter.ToUInt16(mdl, mo + 10), subCount = BitConverter.ToUInt16(mdl, mo + 12);
        if (submesh < 0 || submesh >= subCount)
            throw new ModelEditException($"mesh {mesh} has no submesh {submesh}");

        int ss = src.SubmeshStart + (subIdx + submesh) * 16;
        uint so = BitConverter.ToUInt32(mdl, ss), sc = BitConverter.ToUInt32(mdl, ss + 4);
        int tris = (int)(sc / 3);
        if (tris == 0) throw new ModelEditException("that submesh has no triangles");
        if ((long)src.Ib + (so + sc) * 2 > mdl.Length)
            throw new ModelEditException($"mesh {mesh}'s index range runs past the end of the file");

        // Labels in FIRST-APPEARANCE order, and original order kept within each. Stable on purpose: it is
        // what makes an already-grouped submesh a no-op, and it keeps an author's own ordering intact
        // inside each piece rather than shuffling geometry that had no reason to move.
        var labels = new int[tris];
        var seen = new List<int>();
        for (int t = 0; t < tris; t++)
        {
            labels[t] = groupOf(t);
            if (!seen.Contains(labels[t])) seen.Add(labels[t]);
        }

        var order = new int[tris];       // order[newTriangle] = old triangle
        int at = 0;
        foreach (var label in seen)
            for (int t = 0; t < tris; t++)
                if (labels[t] == label) order[at++] = t;

        bool moved = false;
        for (int t = 0; t < tris && !moved; t++) moved = order[t] != t;

        var regrouped = mdl;
        if (moved)
        {
            regrouped = (byte[])mdl.Clone();
            int at0 = src.Ib + (int)so * 2;
            for (int t = 0; t < tris; t++)
                Buffer.BlockCopy(mdl, at0 + order[t] * 6, regrouped, at0 + t * 6, 6);

            var newOf = new int[tris];   // the inverse, for the shape remap
            for (int t = 0; t < tris; t++) newOf[order[t]] = t;
            RemapShapeValues(regrouped, src, so, sc, newOf);
        }

        // The labels are contiguous now, so the plain split finds exactly one run per label.
        var byNewOrdinal = new int[tris];
        for (int t = 0; t < tris; t++) byNewOrdinal[t] = labels[order[t]];
        return SplitSubmesh(regrouped, mesh, submesh, t => byNewOrdinal[t]);
    }

    /// <summary>
    /// Follow every existing <c>ShapeValue</c> that names a slot inside <paramref name="so"/>..+<paramref
    /// name="sc"/> through a triangle permutation, so shapes already on the model keep deforming the same
    /// corners after <see cref="RegroupSubmesh"/> has moved the triples around.
    /// <para/>
    /// A value's slot is its record's <c>MeshIndexOffset</c> plus its own <c>BaseIndicesIndex</c>, and the
    /// latter is a u16 — so a remap that would push a value past its window's 65536 slots cannot be
    /// expressed without also re-cutting the <c>ShapeMesh</c> records. That is refused rather than written
    /// wrong. It needs a hairstyle that both carries a shape already and is over 65k indices long, which is
    /// not something the hat path ever reaches: it declines a model that already has a hat shape, and hair
    /// with some OTHER shape is rare.
    /// </summary>
    private static void RemapShapeValues(byte[] o, SecondSkinWriter.Source src, uint so, uint sc, int[] newOf)
    {
        ushort shapeCount = BitConverter.ToUInt16(o, src.Mh + 16);
        ushort shapeMeshCount = BitConverter.ToUInt16(o, src.Mh + 18);
        ushort shapeValueCount = BitConverter.ToUInt16(o, src.Mh + 20);
        if (shapeMeshCount == 0 || shapeValueCount == 0) return;

        int shapeMeshBlock = src.ShapeBlock + shapeCount * 16;
        int shapeValBlock = shapeMeshBlock + shapeMeshCount * 12;
        if (shapeValBlock + shapeValueCount * 4 > o.Length)
            throw new ModelEditException("this model's shape block runs past the end of the file");

        for (int m = 0; m < shapeMeshCount; m++)
        {
            uint windowBase = BitConverter.ToUInt32(o, shapeMeshBlock + m * 12);
            uint count = BitConverter.ToUInt32(o, shapeMeshBlock + m * 12 + 4);
            uint start = BitConverter.ToUInt32(o, shapeMeshBlock + m * 12 + 8);
            if (start + count > shapeValueCount) continue;

            for (uint v = start; v < start + count; v++)
            {
                int vo = shapeValBlock + (int)v * 4;
                long slot = windowBase + BitConverter.ToUInt16(o, vo);
                if (slot < so || slot >= so + sc) continue;

                long within = slot - so;
                long moved = (long)newOf[within / 3] * 3 + within % 3;
                long rebased = so + moved - windowBase;
                if (rebased < 0 || rebased > ushort.MaxValue)
                    throw new ModelEditException(
                        "this model's existing shape addresses this submesh from too far away to follow a "
                      + "reorder — it cannot be split by geometry");
                W16(o, vo, (ushort)rebased);
            }
        }
    }

    /// <summary>
    /// Cut <paramref name="parts"/> out of whatever submeshes they share with other geometry, so they can
    /// be tagged without taking that geometry with them.
    /// <para/>
    /// The need is not hypothetical and not rare: a hairstyle routinely keeps its scalp cap and every one
    /// of its ponytail strands in ONE submesh, and an attribute is carried by a submesh record. Tagging at
    /// that granularity to hide the tails hides the scalp too, which in game is being bald.
    /// <para/>
    /// A part that already IS a whole submesh is passed through untouched — there is nothing to cut, and
    /// splitting it would only add records.
    /// <para/>
    /// Cuts through <see cref="RegroupSubmesh"/>, so a part's triangles are gathered together first and this
    /// DOES reorder them. That is what lets a part chosen by geometry be isolated at all, and it also bounds
    /// the result at two records per submesh however scattered the part was. Read that method for why
    /// reordering is safe and for the one ordering thing that is not — it must run before any shape is
    /// written, never after.
    /// </summary>
    /// <returns>The new model, and the submeshes now holding exactly those parts.</returns>
    public static (byte[] Model, List<(int Mesh, int Submesh)> Targets) IsolateParts(
        byte[] mdl, IReadOnlyList<ModelPart> parts)
    {
        var targets = new List<(int Mesh, int Submesh)>();
        if (parts.Count == 0) return (mdl, targets);

        // Which triangles are claimed within each submesh, and which submeshes are claimed entire.
        var byOrdinal = new Dictionary<(int Mesh, int Submesh), HashSet<int>>();
        var whole = new HashSet<(int Mesh, int Submesh)>();
        foreach (var part in parts)
        {
            var key = (part.Mesh, part.Submesh);
            if (part.Island < 0) { whole.Add(key); continue; }
            if (!byOrdinal.TryGetValue(key, out var set)) byOrdinal[key] = set = [];
            foreach (var t in part.Ordinals) set.Add(t);
        }
        // Claiming a submesh whole makes any island claim on it redundant.
        foreach (var key in whole) byOrdinal.Remove(key);

        var edited = mdl;
        foreach (var mesh in byOrdinal.Keys.Concat(whole).Select(k => k.Mesh).Distinct().OrderBy(m => m))
        {
            // Ascending, with a running shift: a split inserts its extra records straight after the submesh
            // it cut, so every submesh later in the same mesh has moved along by that many places.
            int shift = 0;
            var here = byOrdinal.Keys.Concat(whole).Where(k => k.Mesh == mesh)
                .Select(k => k.Submesh).Distinct().OrderBy(s => s);

            foreach (var submesh in here)
            {
                if (whole.Contains((mesh, submesh))) { targets.Add((mesh, submesh + shift)); continue; }

                var claimed = byOrdinal[(mesh, submesh)];
                // Regroup rather than split: the parts sent here are chosen by geometry, so their triangles
                // are scattered through the author's layout and a describe-only split refuses them.
                var (next, groups) = RegroupSubmesh(
                    edited, mesh, submesh + shift, t => claimed.Contains(t) ? 0 : -1);
                edited = next;

                if (groups.TryGetValue(0, out var mine))
                    targets.AddRange(mine.Select(s => (mesh, s)));
                shift += groups.Values.Sum(v => v.Count) - 1;
            }
        }
        return (edited, targets);
    }

    // ── shapes ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Position element types <see cref="SecondSkinWriter.WriteXYZ"/> can actually encode a 3-vector into.
    /// <para/>
    /// Checked rather than assumed because that method has no <c>default</c> arm: handed a type it does not
    /// know, it writes nothing and returns, leaving the duplicate vertex sitting exactly on its source. The
    /// shape is then structurally perfect and moves the hair nowhere, which is the worst kind of bug to go
    /// looking for. Half2 is excluded deliberately even though it is a case there — it has no z.
    /// </summary>
    private static readonly byte[] PositionTypes = [2, 3, 14];   // Float3, Float4, Half4

    /// <summary>
    /// Add a shape key (a morph target) named <paramref name="shapeName"/>, moving the named vertices of the
    /// named LOD0 meshes to new positions.
    /// <para/>
    /// A shape does not store offsets. Each <c>ShapeValue</c> rewires ONE INDEX-BUFFER SLOT to a replacement
    /// vertex that already lives in the same mesh's vertex buffer, so the deformation is expressed as whole
    /// spare vertices the game swaps in while the shape is enabled. This appends those spares at the end of
    /// each mesh's own block — which is what keeps every existing vertex index and byte address valid, and
    /// therefore what lets an existing shape survive the edit untouched.
    /// <para/>
    /// LOD1 and LOD2 are left alone. Their geometry lives in different buffers and only their offsets move,
    /// so a model shaped here simply stops deforming at distance rather than breaking.
    /// </summary>
    /// <param name="moved">Per mesh index, the MESH-RELATIVE vertex indices to move and where to. Mesh
    /// relative on both counts: a model's index buffer stores mesh-relative vertex numbers, and
    /// <c>ModelPart.Triangles</c> does NOT — those are rebased across meshes for drawing and skip meshes the
    /// reader could not decode, so they are the wrong thing to pass here.</param>
    /// <param name="normals">Optional replacement normals, addressed the same way. Omitted, a spare keeps
    /// its source's normal, which is a lighting approximation, not a correctness problem.</param>
    public static byte[] AddShape(
        byte[] mdl, string shapeName,
        IReadOnlyDictionary<int, IReadOnlyDictionary<int, Vector3>> moved,
        IReadOnlyDictionary<int, IReadOnlyDictionary<int, Vector3>>? normals = null)
        => AddShape(mdl, shapeName, moved, out _, normals);

    /// <inheritdoc cref="AddShape(byte[], string, IReadOnlyDictionary{int, IReadOnlyDictionary{int, Vector3}}, IReadOnlyDictionary{int, IReadOnlyDictionary{int, Vector3}})"/>
    /// <param name="unaddressable">How many requested vertices were left where they are because every
    /// triangle drawing them sits past index slot 65535, which a shape value cannot name. Zero for most
    /// models; a large fraction means this hair is too heavily welded to shape and the caller should say so
    /// rather than ship a shape that moves a third of what was asked.</param>
    public static byte[] AddShape(
        byte[] mdl, string shapeName,
        IReadOnlyDictionary<int, IReadOnlyDictionary<int, Vector3>> moved,
        out int unaddressable,
        IReadOnlyDictionary<int, IReadOnlyDictionary<int, Vector3>>? normals = null)
    {
        unaddressable = 0;
        if (string.IsNullOrEmpty(shapeName))
            throw new ModelEditException("a shape needs a name");

        var src = SecondSkinWriter.Parse(mdl);

        ushort shapeCount = BitConverter.ToUInt16(mdl, src.Mh + 16);
        ushort shapeMeshCount = BitConverter.ToUInt16(mdl, src.Mh + 18);
        ushort shapeValueCount = BitConverter.ToUInt16(mdl, src.Mh + 20);

        int shapeBlock = src.ShapeBlock;
        int shapeMeshBlock = shapeBlock + shapeCount * 16;
        int shapeValBlock = shapeMeshBlock + shapeMeshCount * 12;
        int shapeValEnd = shapeValBlock + shapeValueCount * 4;

        // The shape block sits after the bone tables, whose layout is the one thing that changed at
        // Dawntrail — so its position is also the proof that the walk was right for this model's version.
        // Reading the submesh bone map's length prefix is the cheapest way to ask: on a mis-walk it is
        // arbitrary bytes and fails one of these. Without it, a v5 model read as v6 would splice the new
        // tables into the middle of a bone table and produce a file the game cannot load, silently.
        if (shapeValEnd < 0 || shapeValEnd + 4 > mdl.Length)
            throw new ModelEditException("this model's shape block runs past the end of the file");
        uint boneMapBytes = BitConverter.ToUInt32(mdl, shapeValEnd);
        if (boneMapBytes % 2 != 0 || (long)shapeValEnd + 4 + boneMapBytes > mdl.Length)
            throw new ModelEditException(
                "this model's tables do not add up — the shape block is not where the format says it is");

        if (DeclaresShape(mdl, shapeName))
            throw new ModelEditException($"this model already declares a shape named {shapeName}");

        // Nothing here accounts for a neck-morph array, and neither does the parse it relies on. Refuse
        // rather than splice past a table whose size is unknown.
        if (mdl[src.Mh + 43] != 0)
            throw new ModelEditException("this model carries neck morph data, which cannot be edited here");

        var plans = BuildShapePlans(mdl, src, moved, normals, out unaddressable);
        if (plans.Count == 0)
            throw new ModelEditException(
                "none of those vertices are used by a triangle, so a shape over them would deform nothing");

        int totalValues = plans.Sum(p => p.ValueCount);
        int totalMeshes = plans.Sum(p => p.Windows.Count);
        if (shapeCount + 1 > ushort.MaxValue
         || shapeMeshCount + totalMeshes > ushort.MaxValue
         || shapeValueCount + (long)totalValues > ushort.MaxValue)
            throw new ModelEditException(
                $"this shape needs {totalValues} shape values, which overflows what the format counts");

        // ── the three table records ──────────────────────────────────────────
        // Appended at the ends of their arrays, which is why NO existing record needs renumbering: a
        // Shape's shapeMeshStart and a ShapeMesh's valueStart are GLOBAL indices into those arrays, so
        // every range an existing shape names still names the same records afterwards.
        var shapeRec = new byte[16];
        W32(shapeRec, 0, src.StrSize);                     // name offset — the string goes at the block's end
        W16(shapeRec, 4, shapeMeshCount);                  // LOD0 start
        W16(shapeRec, 6, (ushort)(shapeMeshCount + totalMeshes));   // LOD1/2 start: empty, past ours
        W16(shapeRec, 8, (ushort)(shapeMeshCount + totalMeshes));
        W16(shapeRec, 10, (ushort)totalMeshes);            // LOD0 count; LOD1/2 counts stay zero

        var shapeMeshRecs = new byte[totalMeshes * 12];
        var shapeValRecs = new byte[totalValues * 4];
        int valueBase = 0, rec = 0;
        foreach (var p in plans)
            foreach (var (windowBase, values) in p.Windows)
            {
                W32(shapeMeshRecs, rec * 12, windowBase);
                W32(shapeMeshRecs, rec * 12 + 4, (uint)values.Count);
                W32(shapeMeshRecs, rec * 12 + 8, (uint)(shapeValueCount + valueBase));
                for (int v = 0; v < values.Count; v++)
                {
                    W16(shapeValRecs, (valueBase + v) * 4, values[v].Item1);
                    W16(shapeValRecs, (valueBase + v) * 4 + 2, values[v].Item2);
                }
                valueBase += values.Count;
                rec++;
            }

        var text = Encoding.ASCII.GetBytes(shapeName);
        int nameLen = text.Length + 1;
        var strBytes = new byte[nameLen + (4 - nameLen % 4) % 4];
        text.CopyTo(strBytes, 0);

        // ── splice ───────────────────────────────────────────────────────────
        // On a model with NO shapes at all, inserts 2, 3 and 4 all address the same offset — every table is
        // empty and starts where the block does. They land in the right order only because Splice sorts
        // with a STABLE sort, so equal keys keep the order given here. Do not reorder these four.
        var inserts = new List<(int At, byte[] Bytes)>
        {
            (src.StrBlock + (int)src.StrSize, strBytes),
            (shapeMeshBlock, shapeRec),
            (shapeValBlock, shapeMeshRecs),
            (shapeValEnd, shapeValRecs),
        };
        foreach (var p in plans)
            foreach (var (at, bytes) in p.Inserts)
                inserts.Add((at, bytes));

        int dStr = strBytes.Length;
        int dMeta = dStr + shapeRec.Length + shapeMeshRecs.Length + shapeValRecs.Length;
        int dV = plans.Sum(p => p.Inserts.Sum(i => i.Bytes.Length));

        var o = Splice(mdl, inserts.ToArray());

        // ── the new file's coordinates ───────────────────────────────────────
        // The LOD and mesh tables sit BETWEEN the string insert and the shape inserts, so they move by the
        // string block's growth alone — the same trap AddAttribute documents, from the other side.
        int mhN = src.Mh + dStr, lodStartN = src.LodStart + dStr, meshStartN = src.MeshStart + dStr;

        W32(o, src.DeclEnd + 4, src.StrSize + (uint)dStr);                          // string block size
        W16(o, src.DeclEnd, (ushort)(BitConverter.ToUInt16(o, src.DeclEnd) + 1));    // string COUNT
        W16(o, mhN + 16, (ushort)(shapeCount + 1));
        W16(o, mhN + 18, (ushort)(shapeMeshCount + totalMeshes));
        W16(o, mhN + 20, (ushort)(shapeValueCount + totalValues));

        foreach (var p in plans)
            W16(o, meshStartN + p.Mesh * 36, (ushort)(p.VertexCount + p.SrcVerts.Length));

        // Every LOD0 mesh's per-stream offset moves past the inserts at or below it. At or below, not
        // strictly below: Splice puts inserted bytes BEFORE the byte originally at that offset, so a block
        // whose start coincides with an insertion point — mesh A's stream 1 beginning exactly where its
        // stream 0 ended — does move, while the block that grew does not.
        var vInserts = plans.SelectMany(p => p.Inserts).ToArray();
        int lod0End = Math.Min(src.Lod0MeshIndex + src.Lod0MeshCount, src.MeshCount);
        for (int m = src.Lod0MeshIndex; m < lod0End; m++)
        {
            int mo = src.MeshStart + m * 36;
            for (int j = 0; j < 3; j++)
            {
                if (mdl[mo + 32 + j] == 0) continue;
                long at = src.Vb + BitConverter.ToUInt32(mdl, mo + 20 + j * 4);
                int shift = vInserts.Where(i => i.At <= at).Sum(i => i.Bytes.Length);
                if (shift != 0)
                    W32(o, meshStartN + m * 36 + 20 + j * 4,
                        BitConverter.ToUInt32(o, meshStartN + m * 36 + 20 + j * 4) + (uint)shift);
            }
        }

        W32(o, 40, BitConverter.ToUInt32(o, 40) + (uint)dV);                    // VertexBufferSize[0]
        W32(o, lodStartN + 44, BitConverter.ToUInt32(o, lodStartN + 44) + (uint)dV);

        // Two phases, because the inserts straddle the vertex buffer. The metadata grew before all of it,
        // so every absolute offset follows; the vertices grew INSIDE buffer 0, so only what comes after
        // that buffer follows. RuntimeSize is the distance from the header to the START of vertex data,
        // which the second phase does not move — and it stays in Shift precisely so no caller can include
        // it by accident.
        Shift(o, lodStartN, dMeta);
        ShiftOffsets(o, lodStartN, dV, (long)src.Vb + dMeta);
        return o;
    }

    /// <summary>
    /// How many index slots one <c>ShapeMesh</c> can address: <c>BaseIndicesIndex</c> is a u16, so a slot is
    /// named as a number in 0..65535 added to that record's own <c>MeshIndexOffset</c>.
    /// </summary>
    private const int SlotWindow = ushort.MaxValue + 1;

    /// <summary>One mesh's share of a shape: the spare vertices to append and the slots that select them.</summary>
    private sealed class ShapePlan
    {
        public required int Mesh { get; init; }
        public required ushort VertexCount { get; init; }
        public required int[] SrcVerts { get; init; }
        public required List<(int At, byte[] Bytes)> Inserts { get; init; }

        /// <summary>
        /// The slots to rewire, split into <see cref="SlotWindow"/>-sized runs of the index buffer — one
        /// <c>ShapeMesh</c> record each, at absolute base <c>Base</c>.
        /// <para/>
        /// A mesh usually needs exactly one window and it starts at the mesh's own <c>StartIndex</c>, which
        /// is what every well-formed model on disk looks like. A mesh with more than 65535 index entries
        /// needs more than one, and that case is the whole reason this is a list: hair welded into one big
        /// mesh routinely runs to 150,000 slots, and a single window reaches less than half of it.
        /// </summary>
        public required List<(uint Base, List<(ushort Slot, ushort Replace)> Values)> Windows { get; init; }

        public int ValueCount => Windows.Sum(w => w.Values.Count);
    }

    private static List<ShapePlan> BuildShapePlans(
        byte[] mdl, SecondSkinWriter.Source src,
        IReadOnlyDictionary<int, IReadOnlyDictionary<int, Vector3>> moved,
        IReadOnlyDictionary<int, IReadOnlyDictionary<int, Vector3>>? normals,
        out int unaddressable)
    {
        unaddressable = 0;
        int lod0End = Math.Min(src.Lod0MeshIndex + src.Lod0MeshCount, src.MeshCount);
        var plans = new List<ShapePlan>();

        foreach (var mesh in moved.Keys.OrderBy(k => k))
        {
            var wanted = moved[mesh];
            if (wanted.Count == 0) continue;
            if (mesh < src.Lod0MeshIndex || mesh >= lod0End)
                throw new ModelEditException(
                    $"mesh {mesh} is not part of LOD0, whose meshes are the only ones this can shape");

            int mo = src.MeshStart + mesh * 36;
            ushort vc = BitConverter.ToUInt16(mdl, mo);
            uint ic = BitConverter.ToUInt32(mdl, mo + 4), startIndex = BitConverter.ToUInt32(mdl, mo + 16);

            var asked = wanted.Keys.OrderBy(k => k).ToArray();
            foreach (var v in asked)
                if (v < 0 || v >= vc)
                    throw new ModelEditException($"mesh {mesh} has no vertex {v} — it has {vc}");
            if (vc + asked.Length > ushort.MaxValue)
                throw new ModelEditException(
                    $"mesh {mesh} would need {vc + asked.Length} vertices, past the {ushort.MaxValue} a "
                  + "model can count — this is the format reason a hair meant for hats is kept under 30k polys");

            if ((long)src.Ib + (startIndex + ic) * 2 > mdl.Length)
                throw new ModelEditException($"mesh {mesh}'s index range runs past the end of the file");

            var srcVerts = asked;

            // Slot -> replacement, over this mesh's own index range only. ONE VALUE PER SLOT, not per
            // vertex: a vertex shared by six triangles is named by six slots and every one of them has to
            // be rewired, or the shape tears the mesh along the ones that were missed.
            var newIndexOf = new Dictionary<int, ushort>(srcVerts.Length);
            for (int r = 0; r < srcVerts.Length; r++) newIndexOf[srcVerts[r]] = (ushort)(vc + r);

            // Split into windows as we go. A slot is named RELATIVE to its record's own MeshIndexOffset, so
            // a mesh longer than a u16 can count simply gets a second record based 65536 slots further in.
            // Everything before this reached less than half of a big hairstyle: the unreachable tail was a
            // contiguous REGION of the head — measured at head-centre height on the model that prompted
            // this — so pressing everything except it stretched the geometry along the boundary. That was
            // "the hair under the hat looked broken".
            var windows = new List<(uint Base, List<(ushort, ushort)> Values)>();
            for (uint w = 0; w * SlotWindow < ic; w++)
            {
                uint from = w * (uint)SlotWindow, to = Math.Min(ic, from + SlotWindow);
                List<(ushort, ushort)>? here = null;
                for (uint s = from; s < to; s++)
                {
                    int idx = BitConverter.ToUInt16(mdl, src.Ib + (int)(startIndex + s) * 2);
                    if (newIndexOf.TryGetValue(idx, out var rep))
                        (here ??= []).Add(((ushort)(s - from), rep));
                }
                if (here != null) windows.Add((startIndex + from, here));
            }
            // Nothing draws these vertices, so a shape over them would grow the file and deform nothing.
            if (windows.Count == 0) continue;

            plans.Add(new ShapePlan
            {
                Mesh = mesh,
                VertexCount = vc,
                SrcVerts = srcVerts,
                Windows = windows,
                Inserts = SpareVertices(mdl, src, mesh, mo, vc, srcVerts, wanted,
                                        normals != null && normals.TryGetValue(mesh, out var n) ? n : null),
            });
        }
        return plans;
    }

    /// <summary>
    /// The bytes of one mesh's spare vertices, per stream, positioned at the end of that stream's block.
    /// <para/>
    /// EVERY stream the mesh has is grown, not just the one holding position. A mesh routinely keeps
    /// position in stream 0 and its blend weights, UV and tangent frame in stream 1, and the streams are
    /// addressed independently by vertex number — so growing one alone leaves every later mesh reading its
    /// second stream one stride out of register for every vertex. That miscompiles into nothing, crashes
    /// nothing, and skins the model to the wrong bones.
    /// </summary>
    private static List<(int At, byte[] Bytes)> SpareVertices(
        byte[] mdl, SecondSkinWriter.Source src, int mesh, int mo, ushort vc,
        int[] srcVerts, IReadOnlyDictionary<int, Vector3> positions, IReadOnlyDictionary<int, Vector3>? normals)
    {
        var decl = src.Decls[mesh];
        var pos = Array.Find(decl, e => e.Usage == SecondSkinWriter.UsePosition);
        if (pos == default && !Array.Exists(decl, e => e.Usage == SecondSkinWriter.UsePosition))
            throw new ModelEditException($"mesh {mesh} declares no position element");
        if (Array.IndexOf(PositionTypes, pos.Type) < 0)
            throw new ModelEditException(
                $"mesh {mesh} stores its positions as vertex type {pos.Type}, which cannot be written here");

        var nrm = Array.Find(decl, e => e.Usage == SecondSkinWriter.UseNormal);
        bool haveNormal = normals != null && Array.Exists(decl, e => e.Usage == SecondSkinWriter.UseNormal);

        var inserts = new List<(int At, byte[] Bytes)>();
        for (int j = 0; j < 3; j++)
        {
            byte stride = mdl[mo + 32 + j];
            if (stride == 0) continue;
            uint vbo = BitConverter.ToUInt32(mdl, mo + 20 + j * 4);
            int blockStart = src.Vb + (int)vbo;
            if (blockStart < 0 || blockStart + (vc + 1) * stride > mdl.Length + stride)
                throw new ModelEditException($"mesh {mesh}'s stream {j} runs past the end of the file");

            var bytes = new byte[srcVerts.Length * stride];
            for (int r = 0; r < srcVerts.Length; r++)
            {
                // Verbatim, then overwrite. The spare has to keep its source's blend weights, blend
                // indices, UV, tangent frame and colour or it skins and shades as a different vertex.
                Array.Copy(mdl, blockStart + srcVerts[r] * stride, bytes, r * stride, stride);
                if (pos.Stream == j)
                {
                    var p = positions[srcVerts[r]];
                    SecondSkinWriter.WriteXYZ(bytes, r * stride + pos.Offset, pos.Type, p.X, p.Y, p.Z);
                }
                if (haveNormal && nrm.Stream == j && normals!.TryGetValue(srcVerts[r], out var n))
                    SecondSkinWriter.WriteNormal(bytes, r * stride + nrm.Offset, nrm.Type, n.X, n.Y, n.Z);
            }
            inserts.Add((blockStart + vc * stride, bytes));
        }
        return inserts;
    }

    /// <summary>
    /// Whether the model declares a shape by this name.
    /// <para/>
    /// Reads the <c>Shape</c> records directly, NOT <see cref="SecondSkinWriter.Source.Shapes"/>: the parse
    /// keeps only shapes with LOD0 entries and drops the rest, so a shape covering only the lower LODs is
    /// absent from that dictionary. Asking it instead would report a hairstyle as having no hat shape and
    /// invite a second one to be added beside the one it has.
    /// </summary>
    public static bool DeclaresShape(byte[] mdl, string shapeName)
    {
        var src = SecondSkinWriter.Parse(mdl);
        ushort shapeCount = BitConverter.ToUInt16(mdl, src.Mh + 16);
        for (int si = 0; si < shapeCount; si++)
        {
            int at = src.ShapeBlock + si * 16;
            if (at + 16 > mdl.Length) break;
            if (string.Equals(StringAt(mdl, src, BitConverter.ToUInt32(mdl, at)), shapeName, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>A string from the model's own block, by the block-relative offset the tables store.</summary>
    private static string StringAt(byte[] mdl, SecondSkinWriter.Source src, uint rel)
    {
        if (rel >= src.StrSize) return "";
        int o = src.StrBlock + (int)rel, e = o;
        while (e < mdl.Length && mdl[e] != 0) e++;
        return Encoding.ASCII.GetString(mdl, o, e - o);
    }

    // ── shared ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Insert byte runs at the given ORIGINAL offsets, lowest first. The offsets all address the input, so a
    /// caller never has to think about how one insert moves another.
    /// </summary>
    private static byte[] Splice(byte[] src, (int At, byte[] Bytes)[] inserts)
    {
        var ordered = inserts.OrderBy(i => i.At).ToArray();
        var o = new byte[src.Length + ordered.Sum(i => i.Bytes.Length)];

        int read = 0, write = 0;
        foreach (var (at, bytes) in ordered)
        {
            Array.Copy(src, read, o, write, at - read);
            write += at - read;
            read = at;
            bytes.CopyTo(o, write);
            write += bytes.Length;
        }
        Array.Copy(src, read, o, write, src.Length - read);
        return o;
    }

    /// <summary>
    /// Move every absolute file offset on by <paramref name="delta"/>. These eight fields are the entire
    /// list, and the reason it is short: the mesh structs' own vertex offsets are relative to the vertex
    /// BUFFER, the attribute and material offsets are relative to the string block, and the bone tables are
    /// relative to themselves. Only the file header's two offset triples and each LOD's vertex/index data
    /// pointers count from the start of the file.
    /// <para/>
    /// The LOD struct carries an edge-geometry offset too, which is deliberately NOT touched: Penumbra's own
    /// model writer rebases vertex and index and nothing else (<c>MdlFile.Write</c>), so that field is not a
    /// file offset.
    /// <para/>
    /// <c>RuntimeSize</c> comes along because it is defined as the distance from the end of the header to
    /// the vertex data, so growing the metadata grows it one for one.
    /// </summary>
    /// <param name="lodStart">The first LOD struct's position IN THE OUTPUT.</param>
    private static void Shift(byte[] o, int lodStart, int delta)
    {
        W32(o, 8, BitConverter.ToUInt32(o, 8) + (uint)delta);       // RuntimeSize
        ShiftOffsets(o, lodStart, delta, -1);
    }

    /// <summary>
    /// The eight offsets alone, moving only those past <paramref name="past"/>.
    /// <para/>
    /// Split out for the one edit whose inserts do not all land before the geometry: adding a shape appends
    /// spare vertices INSIDE LOD0's vertex buffer, and the regions run V0, I0, V1, I1, V2, I2 — so that
    /// growth moves everything except <c>VertexOffset[0]</c> and LOD0's <c>VertexDataOffset</c>, which name
    /// where that buffer starts rather than anything inside it. A threshold says that in one line and keeps
    /// the field list in one place; enumerating the survivors by hand is how the list drifts.
    /// <para/>
    /// <c>RuntimeSize</c> is deliberately NOT here. It measures the distance from the header to the START of
    /// the vertex data, so growing the buffer's contents does not change it — and leaving it in
    /// <see cref="Shift"/> means no caller can include it by accident.
    /// </summary>
    /// <param name="past">Only offsets strictly greater than this move; -1 moves all of them.</param>
    private static void ShiftOffsets(byte[] o, int lodStart, int delta, long past)
    {
        for (int i = 0; i < 3; i++)
        {
            Bump(o, 16 + i * 4, delta, past);                       // VertexOffset[i]
            Bump(o, 28 + i * 4, delta, past);                       // IndexOffset[i]
            Bump(o, lodStart + i * 60 + 52, delta, past);           // LOD VertexDataOffset
            Bump(o, lodStart + i * 60 + 56, delta, past);           // LOD IndexDataOffset
        }
    }

    /// <summary>Add to a u32, leaving a zero alone — an unused LOD or stream reads 0 and must stay 0.</summary>
    private static void Bump(byte[] o, int at, int delta, long past = -1)
    {
        if (at + 4 > o.Length) return;
        var v = BitConverter.ToUInt32(o, at);
        if (v != 0 && v > past) W32(o, at, v + (uint)delta);
    }

    private static void W16(byte[] b, int o, ushort v) => BitConverter.TryWriteBytes(b.AsSpan(o), v);
    private static void W32(byte[] b, int o, uint v) => BitConverter.TryWriteBytes(b.AsSpan(o), v);
}
