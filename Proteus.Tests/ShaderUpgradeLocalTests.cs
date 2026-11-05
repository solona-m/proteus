using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Lumina.Data.Files;
using Proteus.Services;
using Xunit;
using static Proteus.Services.ShaderUpgradePlan;

namespace Proteus.Tests;

/// <summary>Every legacy gear material in the real mods library is upgraded soundly or left with a reason.</summary>
[Trait("Speed", "Slow")]
public class ShaderUpgradeLibraryLocalTests
{
    [LocalDataFact(LocalData.Mods)]
    [Trait("Plan", "EDGE-dt-shader-upgrade-33")]
    public void Every_legacy_library_material_is_planned_soundly_or_left_with_a_reason()
    {
        // Planning only: nothing is ever written into the real library.
        var modsRoot = LocalData.Path(LocalData.Mods);
        var grey = new byte[64 * 64 * 4];
        for (int i = 0; i < grey.Length; i += 4) { grey[i] = grey[i + 1] = grey[i + 2] = 60; grey[i + 3] = 255; }
        var owners = new Dictionary<string, List<HairPortPlan.Owner>?>(StringComparer.OrdinalIgnoreCase);
        var inputs = new Inputs
        {
            ModsRoot = modsRoot, ManagedRoot = Path.Combine(modsRoot, "Proteus"), Read = File.ReadAllBytes,
            DecodeNative = (_, _) => (grey, 64, 64), Stat = _ => null,
            ReadOwners = root => owners.TryGetValue(root, out var o) ? o : owners[root] = HairPortPlan.ReadOwners(root),
            IsDeclined = (_, _) => false, MaskFileOk = (_, _) => false, Memo = new MaskMemo(),
        };

        int legacy = 0, planned = 0;
        var reasons = new Dictionary<LeftReason, int>();
        foreach (var file in Directory.EnumerateFiles(modsRoot, "mt_c*.mtrl", SearchOption.AllDirectories))
        {
            if (file.Contains(Path.DirectorySeparatorChar + "Proteus" + Path.DirectorySeparatorChar)) continue;
            byte[] bytes;
            try { bytes = File.ReadAllBytes(file); } catch (IOException) { continue; }
            if (CharacterShaderUpgrade.ShaderOf(bytes) != CharacterShaderUpgrade.Legacy) continue;
            legacy++;
            var (candidates, left) = Candidates([new Worn("chara/equipment/e0001/material/v0001/mt_c0201e0001_top_a.mtrl", file)], inputs);
            // The specular map resolves to the file the material's own owners ship, as Penumbra would serve it.
            var resolved = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in candidates.Where(c => c.SpecularGamePath != null))
            {
                var mine = inputs.ReadOwners(c.ModRoot) is { } all ? OwnersOf(all, c.Rel) : [];
                var rel = mine.Select(o => HairPortPlan.FileFor(o, c.SpecularGamePath!)).FirstOrDefault(r => r != null);
                resolved[c.SpecularGamePath!] = rel == null ? c.SpecularGamePath : Path.Combine(c.ModRoot, rel.Replace('/', Path.DirectorySeparatorChar));
            }
            var plan = Build(candidates, resolved, inputs, left);
            if (plan.PerMod is [var mod])
            {
                planned++;
                var w = Assert.Single(mod.Writes);
                Assert.True(IsSound(bytes, w.Bytes, w.Form, w.Mask?.GamePath), file);
            }
            else
            {
                var l = Assert.Single(plan.Left);
                reasons[l.Reason] = reasons.GetValueOrDefault(l.Reason) + 1;
            }
        }
        Assert.True(legacy > 0, "no legacy gear materials found in the library");
        Assert.True(planned > 0, $"none of {legacy} planned; left: {string.Join(", ", reasons)}");
    }
}

/// <summary>
/// The diffuse form against the game's own conversions of legacy diffuse materials to <c>character.shpk</c> (m0075,
/// m0885, m0886), and the derived mask of a real legacy specular map (m0001).
/// </summary>
public class ShaderUpgradeGameLocalTests
{
    private static readonly string[] Conversions =
    [
        "chara/monster/m0075/obj/body/b0001/material/v0001/mt_m0075b0001_b.mtrl",
        "chara/monster/m0075/obj/body/b0001/material/v0002/mt_m0075b0001_b.mtrl",
        "chara/monster/m0885/obj/body/b0001/material/v0001/mt_m0885b0001_a.mtrl",
        "chara/monster/m0886/obj/body/b0001/material/v0001/mt_m0886b0001_a.mtrl",
    ];

    private const string LegacyDiffuse = "chara/monster/m0001/obj/body/b0001/material/v0001/mt_m0001b0001_a.mtrl";

    [LocalDataFact(LocalData.GameData)]
    [Trait("Plan", "EDGE-dt-shader-upgrade-36")]
    public void Diffuse_upgrade_matches_the_games_own_conversions()
    {
        var game = new Lumina.GameData(LocalData.Path(LocalData.GameData));
        var legacy = game.GetFile(LegacyDiffuse)!.Data;
        Assert.Equal(LegacyForm.Diffuse, FormOf(legacy));
        var ours = WindNeutral(CharacterShaderUpgrade.Upgrade(legacy)!)!;
        var oursKeys = Keys(ours)!;
        var oursSamplers = Samplers(ours)!.Select(s => s.Id).Order().ToList();

        var declared = new List<HashSet<uint>>();
        var values = new List<byte[]>();
        foreach (var path in Conversions)
        {
            var conv = game.GetFile(path)!.Data;
            Assert.Equal(CharacterShaderUpgrade.Character, CharacterShaderUpgrade.ShaderOf(conv));
            var keys = Keys(conv)!;
            Assert.Equal(TextureModeCompatibility, keys[TextureModeKey]);
            Assert.Equal(oursKeys[TextureModeKey], keys[TextureModeKey]);
            // Every conversion carries the flow-map key (some with a flow mode of their own); ours, the gear standard.
            Assert.True(keys.ContainsKey(CharacterShaderUpgrade.FlowMapKey));
            Assert.Equal(CharacterShaderUpgrade.FlowMapStandard, oursKeys[CharacterShaderUpgrade.FlowMapKey]);
            // The same four samplers; a conversion with a flow mode adds its flow map's.
            Assert.Subset(Samplers(conv)!.Select(s => s.Id).ToHashSet(), oursSamplers.ToHashSet());

            // Row defaults: fields 3 and 11 at 1, field 7 at 0, the sheen 0.1/0.2/5.
            int t = GearMaterialWriter.ColorTableStart(conv), o = GearMaterialWriter.ColorTableStart(ours);
            foreach (var (field, want) in new[] { (3, 1f), (7, 0f), (11, 1f), (12, 0.1f), (13, 0.2f), (14, 5f) })
            {
                Assert.Equal((Half)want, BitConverter.ToHalf(conv, t + field * 2));
                Assert.Equal((Half)want, BitConverter.ToHalf(ours, o + field * 2));
            }

            declared.Add(TextureLoader.GetMtrlInfo(conv).constIds.ToHashSet());
            values.Add(conv);
        }

        // The constants EVERY conversion declares are what the conversion itself adds, and they agree (wind aside, which
        // ours sets to 0). One that only some declare, or that differs (0xAD94E254: 1 on m0075, 0 on m0886, absent on
        // m0885), is that material's own choice, not part of the recipe.
        var common = declared.Aggregate((a, b) => { a.IntersectWith(b); return a; });
        common.Remove(MeshVolumeService.VertexMovementScaleId);
        common.Remove(MeshVolumeService.VertexMovementMaxLengthId);
        Assert.NotEmpty(common);
        foreach (var id in common)
            Assert.All(values, v => Assert.Equal(Constant(values[0], id), Constant(v, id)));

        // The derived mask of a real legacy specular map.
        var sPath = MaskSlotPath(legacy)!;
        var tex = game.GetFile<TexFile>(sPath)!;
        int w = tex.Header.Width, h = tex.Header.Height;
        var bgra = tex.ImageData;
        var rgba = new byte[bgra.Length];
        double grey = 0;
        for (int i = 0; i < rgba.Length; i += 4)
        {
            rgba[i] = bgra[i + 2]; rgba[i + 1] = bgra[i + 1]; rgba[i + 2] = bgra[i]; rgba[i + 3] = bgra[i + 3];
            grey += Luma(rgba[i], rgba[i + 1], rgba[i + 2]);
        }
        grey /= w * h * 255.0;
        var (mask, mw, mh) = MaskFrom(rgba, w, h, MaskGloss.InverseSpecular);
        Assert.Equal(CapSize(w, h), (mw, mh));
        double r = 0, b = 0;
        int gMin = 255;
        for (int i = 0; i < mask.Length; i += 4) { r += mask[i]; b += mask[i + 2]; gMin = Math.Min(gMin, mask[i + 1]); }
        int n = mw * mh;
        Assert.True(b / n / 255.0 >= 0.99);
        Assert.True(gMin >= 1);
        Assert.Equal(grey, r / n / 255.0, 0.01);
    }
}
