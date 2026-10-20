using System;
using System.Collections.Generic;
using CheapLoc;

namespace Proteus.Services;

/// <summary>
/// A Miqo'te hairstyle without its ears: the geometry half of the "Take the ears off Miqo'te hair mods" setting, with no
/// file or mod I/O. The ears ship inside the hairstyle — fur on the <c>j_mimi</c> bones and a ring of ear skin
/// (<see cref="HatCompatSolve.ReadEarGeometry"/>) — and come out exactly as a port to a race without ears takes them out
/// (<see cref="HairPortBaker.HideEars"/>): every triangle touching them collapses to one corner. Nothing moves.
/// </summary>
internal static class EarlessHair
{
    /// <summary>Raised whenever a copy would come out differently, so copies made by an older build are made again.</summary>
    public const int Version = 1;

    /// <param name="Model">The earless copy.</param>
    /// <param name="Hidden">Triangles collapsed.</param>
    /// <param name="LodTrimmed">Cut to LOD0, so the ears do not come back from a distance.</param>
    /// <param name="TrimRefusal">Why a model of several LODs could not be cut to LOD0 (its LOD0 is earless all the
    /// same); "" otherwise.</param>
    public sealed record Result(byte[] Model, int Hidden, bool LodTrimmed, string TrimRefusal);

    /// <summary>
    /// How many LOD0 triangles still draw ear fur: a corner on the fur or its skin, and three distinct corners (a
    /// collapsed triangle has one). 0 for a hairstyle with no ears, and for an earless copy — whose fur vertices still
    /// ride the ear bones, so "has ear bones" or "has fur vertices" would call it eared.
    /// </summary>
    /// <param name="shapes">Also count what each shape key draws, its index replacements applied one shape at a time.</param>
    public static int DrawnFur(byte[] mdl, bool shapes = false)
    {
        SecondSkinWriter.Source src;
        try { src = SecondSkinWriter.Parse(mdl); }
        catch (Exception) { return 0; }
        var meshes = HatCompatSolve.ReadLod0Meshes(mdl);
        var ears = HatCompatSolve.ReadEarGeometry(mdl, src, meshes);
        if (ears.Fur.Count == 0) return 0;

        var replacements = new List<Dictionary<long, ushort>> { new() };
        if (shapes) replacements.AddRange(ShapeReplacements(mdl, src));

        int drawn = 0;
        foreach (var replaced in replacements)
            foreach (var mv in meshes)
            {
                int mo = src.MeshStart + mv.Mesh * 36;
                if (mo + 36 > mdl.Length) continue;
                uint ic = BitConverter.ToUInt32(mdl, mo + 4), start = BitConverter.ToUInt32(mdl, mo + 16);
                if ((long)src.Ib + ((long)start + ic) * 2 > mdl.Length) continue;
                for (uint t = 0; t + 3 <= ic; t += 3)
                {
                    int a = Corner(start + t), b = Corner(start + t + 1), c = Corner(start + t + 2);
                    if (a == b || b == c || a == c) continue;
                    if (ears.Fur.Contains(Key(mv.Mesh, a)) || ears.Fur.Contains(Key(mv.Mesh, b))
                        || ears.Fur.Contains(Key(mv.Mesh, c)))
                        drawn++;
                }

                int Corner(long slot)
                    => replaced.TryGetValue(slot, out var v) ? v : BitConverter.ToUInt16(mdl, src.Ib + (int)slot * 2);
            }
        return drawn;

        static long Key(int mesh, int v) => ((long)mesh << 32) | (uint)v;
    }

    /// <summary>Each LOD0 shape key's index replacements, by absolute index slot — the layout
    /// <see cref="ModelPartHider"/> rewires.</summary>
    private static IEnumerable<Dictionary<long, ushort>> ShapeReplacements(byte[] mdl, SecondSkinWriter.Source src)
    {
        ushort shapeCount = BitConverter.ToUInt16(mdl, src.Mh + 16), shapeMeshCount = BitConverter.ToUInt16(mdl, src.Mh + 18);
        int shapeMeshBlock = src.ShapeBlock + shapeCount * 16;
        int shapeValueBlock = shapeMeshBlock + shapeMeshCount * 12;
        for (int si = 0; si < shapeCount; si++)
        {
            int shp = src.ShapeBlock + si * 16;
            if (shp + 16 > mdl.Length) yield break;
            var map = new Dictionary<long, ushort>();
            int smStart = BitConverter.ToUInt16(mdl, shp + 4), smCount = BitConverter.ToUInt16(mdl, shp + 10);
            for (int mi = 0; mi < smCount; mi++)
            {
                int sm = shapeMeshBlock + (smStart + mi) * 12;
                if (sm + 12 > mdl.Length) break;
                uint meshIndexOffset = BitConverter.ToUInt32(mdl, sm), valueCount = BitConverter.ToUInt32(mdl, sm + 4),
                     valueStart = BitConverter.ToUInt32(mdl, sm + 8);
                if (shapeValueBlock + ((long)valueStart + valueCount) * 4 > mdl.Length) continue;
                for (uint vi = 0; vi < valueCount; vi++)
                {
                    int sv = shapeValueBlock + (int)(valueStart + vi) * 4;
                    map[meshIndexOffset + (long)BitConverter.ToUInt16(mdl, sv)] = BitConverter.ToUInt16(mdl, sv + 2);
                }
            }
            yield return map;
        }
    }

    /// <summary>
    /// The earless copy of <paramref name="author"/>, or null: with an empty <paramref name="refusal"/> when it draws no
    /// ear fur (nothing to take out), else with why it cannot be made. The copy is checked before it is handed back — it
    /// parses and draws no fur — so a model the collapse cannot fully reach is refused rather than saved half-eared.
    /// </summary>
    public static Result? Make(byte[] author, out string refusal)
    {
        refusal = "";
        SecondSkinWriter.Source src;
        try { src = SecondSkinWriter.Parse(author); }
        catch (Exception)
        {
            refusal = Unreadable;
            return null;
        }
        if (DrawnFur(author) == 0) return null;

        var model = (byte[])author.Clone();
        int hidden = HairPortBaker.HideEars(model, src);

        // The collapse reaches LOD0 only: the other levels would draw the ears again from a distance.
        bool trimmed = false;
        string trimRefusal = "";
        if (ModelLodTrimmer.LodCount(model) > 1)
        {
            if (ModelLodTrimmer.KeepLod0(model, out trimRefusal) is { } cut)
            {
                model = cut;
                trimmed = true;
            }
        }

        try { SecondSkinWriter.Parse(model); }
        catch (Exception)
        {
            refusal = Unreadable;
            return null;
        }
        if (DrawnFur(model) > 0)
        {
            refusal = Loc.Localize("HairPort.Earless.Refused.Leftover", "not all of its ears could be taken out");
            return null;
        }
        return new Result(model, hidden, trimmed, trimmed ? "" : trimRefusal);
    }

    private static string Unreadable => Loc.Localize("Parts.Retarget.Bake.Unreadable", "the model could not be read");
}
