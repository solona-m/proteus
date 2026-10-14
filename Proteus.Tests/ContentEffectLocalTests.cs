using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>The real pack the item-effect import was built for: Frost Veil Plus+, v3 layout, a0002 bracelet.</summary>
public class ContentEffectLocalTests
{
    [LocalDataFact(LocalData.FrostVeilPack)]
    public void Frost_veil_imports_as_an_effect()   // [AC-item-vfx-host-1] [AC-item-vfx-host-3] [EDGE-item-vfx-host-9]
    {
        var preview = ContentImportService.Inspect(LocalData.Path(LocalData.FrostVeilPack));
        var unit = Assert.Single(preview.Units);
        Assert.True(unit.EffectOnly);
        Assert.Equal(15, unit.Effect!.Variant);
        Assert.Equal(1, unit.Effect.VfxId);
        Assert.Equal("chara/accessory/a0002/vfx/eff/va0001.avfx", unit.Effect.GamePath);
        Assert.Equal(5, unit.Effect.Sources.Count);   // the default, then the four Style options

        var dir = Path.Combine(Path.GetTempPath(), "proteus-frostveil-" + Guid.NewGuid().ToString("N"));
        try
        {
            ContentImportService.WriteMod(dir, "Frost Veil (Proteus)", "Someone", preview);

            var owners = new[] { "default_mod.json" }
                .Concat(Directory.GetFiles(dir, "group_*.json").Select(Path.GetFileName))
                .Select(f => JsonNode.Parse(File.ReadAllText(Path.Combine(dir, f!)))!.AsObject())
                .SelectMany(o => o["Options"] is JsonArray a ? a.Select(x => x!.AsObject()) : [o])
                .ToList();
            var keys = owners.SelectMany(o => o["Files"] is JsonObject f ? f.Select(p => p.Key) : []).ToList();

            Assert.DoesNotContain(keys, k => k.StartsWith("chara/accessory/a0002/", StringComparison.OrdinalIgnoreCase));
            // The effect's own textures stay Penumbra's: the .avfx asks for them by these paths.
            Assert.Equal(17, owners[0]["Files"]!.AsObject()
                .Count(p => p.Key.StartsWith("vfx/FroVeil/", StringComparison.OrdinalIgnoreCase)));
            var manips = owners.SelectMany(o => o["Manipulations"] is JsonArray m ? m.Select(x => x!.AsObject()) : [])
                .ToList();
            Assert.DoesNotContain(manips, m => (string?)m["Type"] == "Eqdp");
            Assert.All(manips.Where(m => (string?)m["Type"] == "Imc"),
                m => Assert.Equal(0, (int)m["Manipulation"]!["Entry"]!["VfxId"]!));
            Assert.Contains(manips, m => (string?)m["Type"] == "GlobalEqp");
            Assert.Null(JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "meta.json")))!["Groups"]);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
