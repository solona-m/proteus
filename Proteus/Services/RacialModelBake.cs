using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using CheapLoc;
using XivLiveMesh;
using Vec3 = Proteus.Services.SecondSkinWriter.Vec3;

namespace Proteus.Services;

/// <summary>
/// Turns a garment the game only ships for one race into a model of another — what TexTools does when its metadata
/// editor adds a race to an item.
/// <para/>
/// Most of the game's boots, and much else, exist only as Midlander male models. Everyone else wears them through the
/// EQDP fall-through: the set says "no model for this race", the game loads <c>c0101…</c> and bends it onto the wearer
/// at draw time with <c>human.pbd</c>'s per-bone matrices. Body size cannot fit a woman's garment from a man's model —
/// it would measure it against men's bodies and save it where men would wear it — so it first does here, once and
/// offline, exactly the bend the game would do: the same deformer chain, blended by the same weights. The result is
/// saved under the wearer's own race with an EQDP entry that says the model now exists.
/// </summary>
internal static partial class RacialModelBake
{
    /// <summary>Who a garment is baked for: their race, their skeleton's hierarchy, and the deformer their mods resolve.</summary>
    internal sealed record Wearer(ushort Race, IReadOnlyDictionary<string, string?> Parents, PbdFile Pbd)
    {
        public string? ParentOf(string bone) => Parents.TryGetValue(bone, out var parent) ? parent : null;
    }

    /// <summary>
    /// The race to bake a garment drawn from <paramref name="garment"/>'s model into, for a wearer of
    /// <paramref name="wearer"/> — or 0 when the garment should be refitted as it is. Codes as the game numbers them
    /// (201 for c0201).
    /// <para/>
    /// Only a race the game would reach by falling through from the wearer, so the baked model replaces exactly what is
    /// drawn now. Of those, the first — nearest the wearer — that the target body mod has bodies for, because the bake
    /// has to land where the bodies it is fitted between live: a Miqo'te woman's body mod usually ships Midlander
    /// female bodies and lets the game bend them, so her garment bakes to c0201 and is bent the same way.
    /// <para/>
    /// Nothing is baked when the garment is already of the wearer's sex and the body mod covers its race: a male
    /// Highlander's c0101 boots are refitted between c0101 bodies, and the game bends both alike. Across the sexes there
    /// is no such way round it, so a woman's garment is baked even when no body mod is known yet — to the last race of
    /// her sex before the fall-through leaves it, which is the shared shape the bodies of her sex are made for.
    /// </summary>
    /// <param name="hasBodies">Whether the body mod refitted onto has bodies of exactly this race.</param>
    public static ushort Target(ushort garment, ushort wearer, Func<ushort, bool> hasBodies)
    {
        int g = garment / 100, w = wearer / 100;
        if (!Playable(g) || !Playable(w) || g == w) return 0;

        var chain = new List<int>();
        bool reached = false;
        for (int cur = w, guard = 0; cur != 0 && guard < 8; cur = ModelRace.Fallback(cur), guard++)
        {
            if (cur == g) { reached = true; break; }
            chain.Add(cur);
        }
        if (!reached) return 0;

        bool sameSex = g % 2 == w % 2;
        if (sameSex && hasBodies(Code(g))) return 0;

        int last = 0;
        foreach (int race in chain)
        {
            if (race % 2 != w % 2) continue;
            if (hasBodies(Code(race))) return Code(race);
            last = race;
        }
        return sameSex || last == 0 ? (ushort)0 : Code(last);
    }

    private static bool Playable(int n) => n is >= 1 and <= 18;

    private static ushort Code(int n) => (ushort)(n * 100 + 1);

    /// <summary>
    /// The model <paramref name="mdl"/>, authored for <paramref name="from"/>, bent onto <paramref name="to"/> the way
    /// the game bends it at draw time. Null, with the reason, when it cannot be.
    /// </summary>
    /// <param name="parentOf">The skeleton's parent of a bone: a bone the deformer does not list takes its nearest
    /// listed ancestor's matrix, as in game.</param>
    public static byte[]? Bake(byte[] mdl, ushort from, ushort to, PbdFile pbd, Func<string, string?> parentOf,
                               out string refusal)
    {
        var chain = pbd.Chain(from, to);
        if (chain.Count == 0)
        {
            refusal = string.Format(Loc.Localize("Parts.Retarget.Bake.NoPath.Fmt",
                "the racial deformer has no way from c{0:D4} to c{1:D4}"), from, to);
            return null;
        }
        return Transform(mdl, chain, parentOf, inverse: false, out refusal);
    }

    /// <summary>
    /// The inverse of <see cref="Bake"/>: a model of <paramref name="to"/> put back into <paramref name="from"/>'s shape,
    /// blended by ITS OWN weights. Pushed onto the <paramref name="from"/> path the game bends it forward again with
    /// those same weights, which lands every vertex back where the baked model has it — so a refit can be previewed on
    /// the character without the EQDP entry the saved one comes with.
    /// </summary>
    public static byte[]? Unbake(byte[] mdl, ushort from, ushort to, PbdFile pbd, Func<string, string?> parentOf,
                                 out string refusal)
    {
        var chain = pbd.Chain(from, to);
        if (chain.Count == 0)
        {
            refusal = string.Format(Loc.Localize("Parts.Retarget.Bake.NoPath.Fmt",
                "the racial deformer has no way from c{0:D4} to c{1:D4}"), from, to);
            return null;
        }
        return Transform(mdl, chain, parentOf, inverse: true, out refusal);
    }

    private static byte[]? Transform(byte[] mdl, IReadOnlyList<IReadOnlyDictionary<string, Matrix4x4>> chain,
                                     Func<string, string?> parentOf, bool inverse, out string refusal)
    {
        var parts = ModelPartReader.Read(mdl);
        var skin = ModelSkinReader.Read(mdl, null, null);
        if (parts == null || skin == null)
        {
            refusal = Loc.Localize("Parts.Retarget.Bake.Unreadable", "the model could not be read");
            return null;
        }

        // Both readers promise the same vertex order; a count that differs means one skipped a mesh the other kept,
        // and every delta after it would land on the wrong vertex.
        int count = parts.Positions.Length / 3;
        if (skin.VertexCount != count)
        {
            refusal = string.Format(Loc.Localize("Parts.Retarget.Bake.WeightMismatch.Fmt",
                "the model's vertices could not be matched to their weights ({0} against {1})"), count, skin.VertexCount);
            return null;
        }

        var bones = new Matrix4x4[skin.BoneNames.Length];
        for (int j = 0; j < bones.Length; j++) bones[j] = PbdFile.Resolve(chain, skin.BoneNames[j], parentOf);

        var edit = new Edit(parts.MeshSpans, count);
        for (int v = 0; v < count; v++)
        {
            // The blend the game's vertex shader makes, as LiveMeshPoser does it: the matrices summed by weight.
            Matrix4x4 m = default;
            float total = 0f;
            int o = v * SkinnedMesh.MaxInfluences;
            for (int k = 0; k < SkinnedMesh.MaxInfluences; k++)
            {
                float w = skin.BoneWeights[o + k];
                if (w <= 0f) continue;
                m += bones[skin.BoneIndices[o + k]] * w;
                total += w;
            }
            if (total <= 1e-6f || !Matrix4x4.Invert(m * (1f / total), out var inv)) continue;
            m *= 1f / total;
            if (inverse) (m, inv) = (inv, m);

            var p = new Vector3(parts.Positions[v * 3], parts.Positions[v * 3 + 1], parts.Positions[v * 3 + 2]);
            var d = Vector3.Transform(p, m) - p;
            edit.Delta[v] = new Vec3(d.X, d.Y, d.Z);
            edit.Worst = MathF.Max(edit.Worst, d.Length());

            // Row vectors: a normal goes through the inverse transpose, or a squashed limb's normals lean the wrong way.
            var n = new Vector3(parts.Normals[v * 3], parts.Normals[v * 3 + 1], parts.Normals[v * 3 + 2]);
            if (n == Vector3.Zero) continue;
            n = Vector3.TransformNormal(n, Matrix4x4.Transpose(inv));
            if (n.LengthSquared() > 1e-12f) n = Vector3.Normalize(n);
            edit.Normal[v] = new Vec3(n.X, n.Y, n.Z);
        }

        try
        {
            refusal = "";
            // Every vertex by its own bend, shape keys' spares included: each has weights of its own, which the game
            // skins it with, and carrying the base vertex's delta instead is only right for an edit that has none.
            return MeshVolumeService.Inflate(mdl, edit, carrySpares: false).Model;
        }
        catch (Exception ex)
        {
            refusal = ex.Message;
            return null;
        }
    }

    /// <summary>The bend as an edit <see cref="MeshVolumeService.Inflate"/> can write, shape keys' spares and all.</summary>
    private sealed class Edit(IReadOnlyList<MeshSpan> spans, int count) : IMeshEdit
    {
        public readonly Vec3[] Delta = new Vec3[count];
        public readonly Vec3[] Normal = new Vec3[count];

        public IReadOnlyList<MeshSpan> Spans => spans;
        public float Worst { get; set; }
        public bool Dirty => true;
        public bool WindEdited => false;
        public Vec3 DeltaAt(int vertex) => Delta[vertex];
        public Vec3 NormalAt(int vertex) => Normal[vertex];
        public float WindAt(int vertex) => 0f;
    }

    /// <summary>
    /// Draw every skin <paramref name="model"/> still carries of its own with <paramref name="body"/>'s skin material.
    /// <para/>
    /// The game names a skin material after the MODEL's race: <c>/mt_c0101b0001_b.mtrl</c> in a c0201 model loads
    /// c0201's <c>_b</c>, which is whatever the wearer's mods make of it — often nothing that loads, since a woman's
    /// body mods publish their own variant (Neolithe's <c>_bibo</c>) and leave the rest. One material that fails to
    /// load and the game draws none of the model: the refitted garment vanishes, cloth and all. The body's own skin
    /// material is the one thing sure to load, because the character's body is drawn with it.
    /// </summary>
    /// <returns>The model, and the materials renamed (none when its skin is all the body's already).</returns>
    public static byte[] SkinLikeBody(byte[] model, byte[] body, out List<string> renamed)
    {
        renamed = [];
        var bodySkins = SecondSkinWriter.Parse(body).MatNames.Where(SecondSkinWriter.IsBodySkinMaterial).ToList();
        if (bodySkins.Count == 0) return model;

        foreach (string name in SecondSkinWriter.Parse(model).MatNames)
        {
            if (!SecondSkinWriter.IsBodySkinMaterial(name) || bodySkins.Contains(name)) continue;
            model = ModelAttributeWriter.RenameMaterial(model, name, bodySkins[0]);
            renamed.Add(name);
        }
        return model;
    }

    /// <summary>An equipment model's game path with its race code changed: <c>…/c0101e6023_sho.mdl</c> to c0201.</summary>
    public static string WithRace(string gamePath, ushort race)
        => FileRace().Replace(gamePath, $"c{race:D4}");

    [GeneratedRegex(@"(?<=(?:^|/))c\d{4}(?=e\d{4}_[a-z]{3}\.mdl$)", RegexOptions.IgnoreCase)]
    private static partial Regex FileRace();

    /// <summary>
    /// The EQDP entry that makes the game load a baked model: <paramref name="race"/> now has a model of this set in
    /// this slot. Null for a path that is not an equipment model of a slot the table covers.
    /// </summary>
    public static object? Switch(string gamePath, ushort race)
    {
        if (BodySizeCatalog.SlotOf(gamePath) is not { } slot || BodyRetarget.ImcSlotName(slot) is not { } eqdpSlot)
            return null;
        if (PenumbraManipulations.ParseSetId(gamePath, 'e') is not { } set) return null;
        return PenumbraManipulations.EqdpManipulation($"{race:D4}", eqdpSlot, set);
    }
}
