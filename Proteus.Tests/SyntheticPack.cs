using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace Proteus.Tests;

/// <summary>
/// A minimal Penumbra v4 <c>.pmp</c>, written to a temp file.
/// <para/>
/// Same reason as <see cref="SyntheticModel"/>: the import tests for attribute-driven packs and for the
/// race-only warning were gated on <c>if (!File.Exists(...)) return;</c> against a pack at an absolute path
/// on one machine's Desktop. When that pack was moved, both tests began passing without asserting anything —
/// while the behaviour they cover had live bugs in it.
/// <para/>
/// It writes the shape those tests need and nothing more: one model redirect at a chosen race code, its
/// materials, and option groups that select by <c>Atr</c> manipulation rather than by redirecting files —
/// which is exactly the pack shape a reader looking only at <c>Files</c> mistakes for "selects nothing".
/// </summary>
internal sealed class SyntheticPack : IDisposable
{
    /// <summary>One of the pack's own checkboxes: a name, and the model attribute it switches on.</summary>
    internal sealed record Toggle(string Option, string Attribute);

    /// <summary>Where the pack was written. Deleted with the instance.</summary>
    internal string Path { get; }

    private SyntheticPack(string path) => Path = path;

    public void Dispose()
    {
        try { File.Delete(Path); } catch { /* a temp file that outlives the test harms nothing */ }
    }

    /// <summary>
    /// A pack whose pieces are shown and hidden by an <c>Imc</c> group — the Denim Shorts shape. The model
    /// carries <paramref name="attributes"/> in that order, and the group's options each clear one bit,
    /// starting at bit 0.
    /// <para/>
    /// The default mask has every bit the attributes occupy, so nothing is hidden until an option is
    /// selected. That is what makes the options read as "hide".
    /// </summary>
    internal static SyntheticPack ImcToggled(
        string raceCode, string groupName, params string[] attributes)
    {
        var meshes = attributes
            .Select((_, i) => new SyntheticModel.Mesh($"/mt_c{raceCode}e6058_dwn_{(char)('a' + i)}.mtrl",
                                                      new SyntheticModel.Sub(1u << i)))
            .ToArray();
        var model = SyntheticModel.Build(attributes, meshes);

        const string ModelEntry = "model.mdl";
        string modelGamePath = $"chara/equipment/e6058/model/c{raceCode}e6058_dwn.mdl";

        var files = new List<(string GamePath, string Entry)> { (modelGamePath, ModelEntry) };
        foreach (var mesh in meshes)
        {
            var leaf = mesh.Material.TrimStart('/');
            files.Add(($"chara/equipment/e6058/material/v0001/{leaf}", leaf));
        }

        int defaultMask = (1 << attributes.Length) - 1;
        var sb = new StringBuilder();
        sb.Append("{\"FileVersion\":4,\"Name\":\"Synthetic\",\"Author\":\"Tests\",\"Description\":\"\",");
        sb.Append("\"DefaultData\":{\"Files\":{");
        sb.Append(string.Join(",", files.Select(f => $"{Quote(f.GamePath)}:{Quote(f.Entry)}")));
        sb.Append("},\"FileSwaps\":{},\"Manipulations\":[]},");
        sb.Append("\"Groups\":[{\"Type\":\"Imc\",\"Name\":");
        sb.Append(Quote(groupName));
        sb.Append(",\"Identifier\":{\"ObjectType\":\"Equipment\",\"PrimaryId\":6058,\"Variant\":1,");
        sb.Append("\"EquipSlot\":\"Legs\"},\"DefaultEntry\":{\"MaterialId\":1,\"DecalId\":0,\"VfxId\":0,");
        sb.Append("\"MaterialAnimationId\":0,\"AttributeMask\":").Append(defaultMask).Append(",\"SoundId\":0},");
        sb.Append("\"Options\":[");
        sb.Append(string.Join(",", attributes.Select((a, i) =>
            "{\"Name\":" + Quote(a + " Hide") + ",\"AttributeMask\":" + (1 << i) + "}")));
        sb.Append("]}]}");

        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "proteus-synth-" + Guid.NewGuid().ToString("N") + ".pmp");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            Write(zip, "meta.json", Encoding.UTF8.GetBytes(sb.ToString()));
            Write(zip, ModelEntry, model);
            foreach (var mesh in meshes) Write(zip, mesh.Material.TrimStart('/'), [0]);
        }
        return new SyntheticPack(path);
    }

    /// <summary>
    /// A pack whose garment has "ex" bones — the Cerise kimono shape. One model on the <c>met</c> slot in a
    /// single-select group, and an <c>Est</c> manipulation declaring the extra skeleton its bones live in.
    /// <para/>
    /// Mirrors what real packs do, which the implementation has to survive: the manipulation is repeated
    /// once per race (a real pack lists eight), it names the set the PACK replaces rather than anything the
    /// wearer has on, and <paramref name="alsoZeroEntry"/> adds the <c>Entry: 0</c> record that most
    /// EST-bearing packs carry and that must never be written anywhere.
    /// </summary>
    internal static SyntheticPack EstBearing(
        string groupName, string optionName, string estSlot, int entry, bool alsoZeroEntry = false)
    {
        const int PackSet = 6085;
        var model = SyntheticModel.Build([],
            new SyntheticModel.Mesh("/mt_c0201e6085_met_a.mtrl", new SyntheticModel.Sub(0)));

        const string ModelEntry = "model.mdl";
        string modelGamePath = $"chara/equipment/e{PackSet}/model/c0201e{PackSet}_met.mdl";
        const string MaterialLeaf = "mt_c0201e6085_met_a.mtrl";

        string Est(int setId, int e, string race) =>
            "{\"Type\":\"Est\",\"Manipulation\":{\"Gender\":\"Female\",\"Race\":" + Quote(race)
          + ",\"SetId\":" + setId + ",\"Slot\":" + Quote(estSlot) + ",\"Entry\":" + e + "}}";

        // Eight races, as a real pack writes it — the reader must collapse them, since a composite dresses
        // one character and the race it needs is that character's.
        var manips = new List<string>();
        foreach (var race in new[] { "Midlander", "Highlander", "Elezen", "Miqote",
                                     "Roegadyn", "Lalafell", "AuRa", "Viera" })
            manips.Add(Est(PackSet, entry, race));
        if (alsoZeroEntry) manips.Add(Est(PackSet, 0, "Midlander"));

        var sb = new StringBuilder();
        sb.Append("{\"FileVersion\":4,\"Name\":\"Synthetic\",\"Author\":\"Tests\",\"Description\":\"\",");
        sb.Append("\"DefaultData\":{\"Files\":{");
        sb.Append(Quote($"chara/equipment/e{PackSet}/material/v0001/{MaterialLeaf}"));
        sb.Append(':').Append(Quote(MaterialLeaf));
        sb.Append("},\"FileSwaps\":{},\"Manipulations\":[]},");
        sb.Append("\"Groups\":[{\"Type\":\"Single\",\"Name\":").Append(Quote(groupName));
        sb.Append(",\"Options\":[{\"Name\":").Append(Quote(optionName));
        sb.Append(",\"Files\":{").Append(Quote(modelGamePath)).Append(':').Append(Quote(ModelEntry));
        sb.Append("},\"Manipulations\":[").Append(string.Join(",", manips)).Append("]}]}]}");

        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "proteus-synth-" + Guid.NewGuid().ToString("N") + ".pmp");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            Write(zip, "meta.json", Encoding.UTF8.GetBytes(sb.ToString()));
            Write(zip, ModelEntry, model);
            Write(zip, MaterialLeaf, [0]);
        }
        return new SyntheticPack(path);
    }

    /// <summary>
    /// A pack holding one model on the <c>met</c> slot at <paramref name="raceCode"/>, whose pieces are
    /// revealed by <paramref name="toggles"/> — one option per toggle, in a single multi-select group named
    /// <paramref name="groupName"/>. Each toggle's attribute tags its own material's submesh, so the gates
    /// the importer records map option → material one to one.
    /// </summary>
    internal static SyntheticPack AttributeDriven(
        string raceCode, string groupName, params Toggle[] toggles)
    {
        var attrs = toggles.Select(t => t.Attribute).ToList();
        var meshes = toggles
            .Select((t, i) => new SyntheticModel.Mesh($"/mt_c{raceCode}e5505_met_{(char)('a' + i)}.mtrl",
                                                      new SyntheticModel.Sub(1u << i)))
            .ToArray();
        var model = SyntheticModel.Build(attrs, meshes);

        const string ModelEntry = "model.mdl";
        string modelGamePath = $"chara/equipment/e5505/model/c{raceCode}e5505_met.mdl";

        var files = new List<(string GamePath, string Entry)> { (modelGamePath, ModelEntry) };
        foreach (var mesh in meshes)
        {
            var leaf = mesh.Material.TrimStart('/');
            files.Add(($"chara/equipment/e5505/material/v0001/{leaf}", leaf));
        }

        var sb = new StringBuilder();
        sb.Append("{\"FileVersion\":4,\"Name\":\"Synthetic\",\"Author\":\"Tests\",\"Description\":\"\",");
        sb.Append("\"Version\":\"1.0\",\"Website\":\"\",\"ModTags\":[],");
        sb.Append("\"DefaultData\":{\"Files\":{");
        sb.Append(string.Join(",", files.Select(f => $"{Quote(f.GamePath)}:{Quote(f.Entry)}")));
        sb.Append("},\"FileSwaps\":{},\"Manipulations\":[]},");

        // The group's options redirect NO files — they only flip attributes. A pack really does ship this
        // way, and it is the case the importer used to read as an empty option.
        sb.Append("\"Groups\":[{\"Name\":");
        sb.Append(Quote(groupName));
        sb.Append(",\"Description\":\"\",\"Priority\":0,\"Type\":\"Multi\",\"DefaultSettings\":0,\"Options\":[");
        sb.Append(string.Join(",", toggles.Select(t =>
            "{\"Name\":" + Quote(t.Option) + ",\"Description\":\"\",\"Priority\":0,"
          + "\"Files\":{},\"FileSwaps\":{},\"Manipulations\":[{\"Type\":\"Atr\",\"Manipulation\":{"
          + "\"Entry\":true,\"Attribute\":" + Quote(t.Attribute)
          + ",\"Slot\":\"Head\",\"Id\":5505,\"Gender\":\"Female\",\"Race\":\"Miqote\"}}]}")));
        sb.Append("]}]}");

        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "proteus-synth-" + Guid.NewGuid().ToString("N") + ".pmp");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            Write(zip, "meta.json", Encoding.UTF8.GetBytes(sb.ToString()));
            Write(zip, ModelEntry, model);
            // The materials only have to EXIST — the importer binds a mesh to one by file name, and never
            // opens it during a preview.
            foreach (var mesh in meshes) Write(zip, mesh.Material.TrimStart('/'), [0]);
        }
        return new SyntheticPack(path);
    }

    private static void Write(ZipArchive zip, string name, byte[] bytes)
    {
        using var st = zip.CreateEntry(name).Open();
        st.Write(bytes, 0, bytes.Length);
    }

    private static string Quote(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
