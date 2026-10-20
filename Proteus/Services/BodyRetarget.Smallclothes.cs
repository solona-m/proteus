using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Proteus.Services;

internal static partial class BodyRetarget
{
    /// <summary>
    /// A material file the refit's option must publish beside the model: a byte copy of the new body's smallclothes
    /// material, under the garment's own name for it.
    /// </summary>
    /// <param name="Name">The file's name as the model names it, without the slash (<c>mt_c0201e6144_dwn_z.mtrl</c>).</param>
    /// <param name="Bytes">The body mod's own file.</param>
    /// <param name="From">The material as the body names it (<c>/mt_c0201e0000_dwn_b.mtrl</c>).</param>
    internal sealed record PublishedMaterial(string Name, byte[] Bytes, string From);

    /// <summary>An equipment material the garment names as its own: race, set, slot and suffix.</summary>
    private static readonly Regex OwnMaterial = new(@"^/?mt_c(\d{4})e(\d{4})_([a-z]{3})_([a-z0-9_]+)\.mtrl$",
                                                    RegexOptions.CultureInvariant);

    /// <summary>
    /// Readies each <see cref="SlotPair.Worn"/> slot the swap puts its skin in to bring the new body's smallclothes in
    /// with it: the new body draws them where it has no skin (Muse+'s boxers), so a garment whose own skin reached there
    /// would otherwise show a hole. A slot is ready when each of its smallclothes materials is found in the body mod,
    /// the garment has room for them, and the garment names an equipment material of its own to name them after; it
    /// then carries the names they go in under (<see cref="SlotPair.Smallclothes"/>). A slot that is not ready keeps
    /// the garment's own skin (<see cref="SlotPair.ShapeOnly"/>): nothing of it is swapped, so nothing is left bare.
    /// </summary>
    /// <param name="materials">The copies the ready slots need published, under their new names.</param>
    /// <param name="fallback">Worn slots that keep the garment's own skin instead.</param>
    internal static IReadOnlyList<SlotPair> ReadySmallclothes(byte[] garmentBytes, IReadOnlyList<SlotPair> pairs,
                                                               bool replaceSkin, out List<PublishedMaterial> materials,
                                                               out int fallback)
    {
        materials = [];
        fallback = 0;
        if (!replaceSkin || !pairs.Any(p => p.Worn && p.Swaps)) return pairs;

        var garmentMaterials = SecondSkinWriter.Parse(garmentBytes).MatNames;
        var own = garmentMaterials.Select(m => OwnMaterial.Match(m)).FirstOrDefault(m => m.Success);
        var taken = new HashSet<string>(garmentMaterials.Select(m => m.TrimStart('/')), StringComparer.OrdinalIgnoreCase);
        // Skin first: one layer per slot whose skin goes in, then the smallclothes; nails and piercings take what is left.
        int room = SecondSkinWriter.MaxMaterials - garmentMaterials.Count - pairs.Count(p => p.Swaps);

        var ready = new List<SlotPair>(pairs.Count);
        foreach (var pair in pairs)
        {
            if (!pair.Worn || !pair.Swaps)
            {
                ready.Add(pair);
                continue;
            }
            var names = SmallclothesMaterialsOf(pair);
            var found = names.Select(n => (Name: n, Bytes: pair.TargetMaterial?.Invoke(n.TrimStart('/')))).ToList();
            if (own == null || names.Count == 0 || names.Count > room || found.Any(f => f.Bytes == null))
            {
                ready.Add(pair with { ShapeOnly = true });
                fallback++;
                continue;
            }

            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (name, bytes) in found)
            {
                string leaf = FreeMaterialName(own, taken);
                taken.Add(leaf);
                map[name] = "/" + leaf;
                materials.Add(new PublishedMaterial(leaf, bytes!, name));
                room--;
            }
            ready.Add(pair with { Smallclothes = map });
        }
        return ready;
    }

    /// <summary>The materials of the new body's smallclothes parts it draws, by name, in model order.</summary>
    internal static List<string> SmallclothesMaterialsOf(SlotPair pair)
    {
        var names = new List<string>();
        foreach (var part in pair.Target.Parts)
            if (part.Island < 0 && !SecondSkinWriter.IsBodySkinMaterial(part.Material)
                && BodySurface.IsSmallclothes(part.Material) && !names.Contains(part.Material))
                names.Add(part.Material);
        return names;
    }

    /// <summary>
    /// A material name of the garment's own — its race, set and slot — with a one-letter suffix nothing of it uses yet,
    /// from z down: the letters a garment's author uses run up from a.
    /// </summary>
    private static string FreeMaterialName(Match own, IReadOnlySet<string> taken)
    {
        string stem = $"mt_c{own.Groups[1].Value}e{own.Groups[2].Value}_{own.Groups[3].Value}_";
        for (char c = 'z'; c >= 'a'; c--)
            if (!taken.Contains(stem + c + ".mtrl")) return stem + c + ".mtrl";
        return stem + "proteus.mtrl";
    }

    /// <summary>
    /// Reads a material of the body mod a body model belongs to, by the name its model gives it: what the mod publishes
    /// at that name in the game's smallclothes folder (<c>chara/equipment/e0000/material/v0001/</c>). The mod is the
    /// nearest folder above the model with a Penumbra manifest. Null when the mod publishes no such file, or more than
    /// one that differ — options of one group can each carry their own, and which is on cannot be told from here.
    /// </summary>
    internal static Func<string, byte[]?> BodyMaterialReader(string bodyModelPath)
    {
        // Found on the first read only: every pair is built with one, and only a worn one ever reads.
        string? modRoot = null, inside = null;
        List<PenumbraModMeta.Redirect>? redirects = null;
        return leaf =>
        {
            if (redirects == null)
            {
                string? root = Path.GetDirectoryName(Path.GetFullPath(bodyModelPath));
                while (root != null && !PenumbraModMeta.HasReadableManifest(root)) root = Path.GetDirectoryName(root);
                redirects = root != null ? PenumbraModMeta.ReadAllRedirects(root) : [];
                modRoot = root ?? "";
                // Only files inside the body mod: the bytes are copied into the garment's mod, which travels with an export.
                inside = Path.TrimEndingDirectorySeparator(Path.GetFullPath(modRoot.Length > 0 ? modRoot : ".")) +
                         Path.DirectorySeparatorChar;
            }
            if (modRoot!.Length == 0) return null;
            string gamePath = "chara/equipment/e0000/material/v0001/" + leaf;
            var files = redirects.Where(r => string.Equals(r.GamePath.Replace('\\', '/'), gamePath,
                                                           StringComparison.OrdinalIgnoreCase))
                                 .Select(r => Path.GetFullPath(Path.Combine(modRoot,
                                                                            r.File.Replace('\\', Path.DirectorySeparatorChar))))
                                 .Where(f => f.StartsWith(inside!, StringComparison.OrdinalIgnoreCase) && File.Exists(f))
                                 .Select(File.ReadAllBytes)
                                 .ToList();
            if (files.Count == 0) return null;
            return files.Skip(1).All(f => f.AsSpan().SequenceEqual(files[0])) ? files[0] : null;
        };
    }
}
