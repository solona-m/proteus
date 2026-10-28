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

    /// <summary>One entry of a pack's default data: a game path, the archive entry backing it, and whether
    /// that entry is actually written. A manifest naming a file the archive doesn't hold is a real shape a
    /// pack arrives in, and the importer has to say so rather than throw.</summary>
    internal sealed record Redirect(string GamePath, string Entry, bool Present = true);

    /// <summary>
    /// A pack that ships no geometry at all — the emissive-skin shape. Everything goes in the default data,
    /// which is where these packs put it: the body materials they rewire, and the art those materials point
    /// at, on VIRTUAL paths under <c>chara/&lt;body&gt;/</c> that no vanilla shader asks for.
    /// <para/>
    /// The entry bytes are a stub. Nothing in the importer looks at a texture's content itself — the decode
    /// is a delegate on both the preview and the write, exactly so this can run offline.
    /// </summary>
    internal static SyntheticPack NoGeometry(string name, string author, params Redirect[] files)
    {
        var sb = new StringBuilder();
        sb.Append("{\"FileVersion\":4,\"Name\":").Append(Quote(name));
        sb.Append(",\"Author\":").Append(Quote(author));
        sb.Append(",\"Description\":\"\",\"Version\":\"1.0\",\"Website\":\"\",\"ModTags\":[],");
        sb.Append("\"DefaultData\":{\"Files\":{");
        sb.Append(string.Join(",", files.Select(f => $"{Quote(f.GamePath)}:{Quote(f.Entry)}")));
        sb.Append("},\"FileSwaps\":{},\"Manipulations\":[]},\"Groups\":[]}");

        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "proteus-synth-" + Guid.NewGuid().ToString("N") + ".pmp");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            Write(zip, "meta.json", Encoding.UTF8.GetBytes(sb.ToString()));
            foreach (var entry in files.Where(f => f.Present).Select(f => f.Entry).Distinct())
                Write(zip, entry, [0]);
        }
        return new SyntheticPack(path);
    }

    /// <summary>
    /// The Frost Veil shape: an accessory whose model is an invisible placeholder (one triangle with its three corners
    /// in one spot) and whose whole look is an animated effect. The manifest JSON is copied from the real pack —
    /// including <c>"SetId": "2"</c> as a STRING in its Eqdp edits, and both <c>AttributeAndSound</c> and
    /// <c>AttributeMask</c> in its IMC entry — because a reader that only takes numbers matches nothing there.
    /// </summary>
    /// <param name="legacy">Write the v3 layout (default_mod.json + group_NNN files), as Frost Veil itself ships.</param>
    /// <param name="geometry">A real triangle instead of the placeholder.</param>
    /// <param name="effect">False writes the IMC entry with <c>VfxId 0</c>: a placeholder with no effect.</param>
    /// <param name="imcInDefault">The IMC edit in the default data rather than the Items option.</param>
    /// <param name="avfxPresent">False names the effect files in the manifest without writing them.</param>
    /// <param name="materialPresent">False ships no material for the model's mesh.</param>
    /// <param name="avfxRedirected">False redirects no <c>.avfx</c> at all: the IMC turns on one of the game's own effects.</param>
    /// <param name="neighbours">Also ship what is NOT the piece's: another slot's material in the same set, a set texture
    /// named for no slot, and a second effect (variant 3, <c>VfxId 2</c>) on the same item.</param>
    internal static SyntheticPack EffectBearing(
        bool legacy = false, bool geometry = false, bool effect = true, bool imcInDefault = false,
        bool avfxPresent = true, bool materialPresent = true, bool avfxRedirected = true, bool neighbours = false)
    {
        const string Model = "items/model.mdl";
        var corner = (0f, 1.077f, 0f);
        var model = SyntheticModel.Build([], new SyntheticModel.Mesh("/mt_c0101a0002_wrs_a.mtrl",
            geometry ? new SyntheticModel.Sub(0) : new SyntheticModel.Sub(0, Corners: [corner, corner, corner])));

        var items = new List<(string GamePath, string Entry)>
        {
            ("chara/accessory/a0002/model/c0101a0002_wrs.mdl", Model),
            ("chara/accessory/a0002/texture/v01_c0101a0002_wrs_d.tex", "items/v01_d.tex"),
        };
        if (materialPresent)
        {
            items.Add(("chara/accessory/a0002/material/v0001/mt_c0101a0002_wrs_a.mtrl", "items/v0001.mtrl"));
            items.Add(("chara/accessory/a0002/material/v0015/mt_c0101a0002_wrs_a.mtrl", "items/v0015.mtrl"));
        }

        string imc = "{\"Type\":\"Imc\",\"Manipulation\":{\"Entry\":{\"AttributeAndSound\":1023,\"MaterialId\":15,"
                   + "\"DecalId\":0,\"VfxId\":" + (effect ? 1 : 0) + ",\"MaterialAnimationId\":0,\"AttributeMask\":1023,"
                   + "\"SoundId\":0},\"ObjectType\":\"Accessory\",\"PrimaryId\":2,\"SecondaryId\":0,\"Variant\":15,"
                   + "\"EquipSlot\":\"Wrists\",\"BodySlot\":\"Unknown\"}}";
        string Eqdp(string race) => "{\"Type\":\"Eqdp\",\"Manipulation\":{\"Entry\":0,\"Gender\":\"Male\",\"Race\":"
                                  + Quote(race) + ",\"SetId\":\"2\",\"Slot\":\"Wrists\"}}";
        const string GlobalEqp = "{\"Type\":\"GlobalEqp\",\"Manipulation\":{\"Type\":\"DoNotHideBracelets\",\"Condition\":2}}";

        if (neighbours)
        {
            items.Add(("chara/accessory/a0002/material/v0001/mt_c0101a0002_nek_a.mtrl", "items/nek.mtrl"));
            items.Add(("chara/accessory/a0002/vfx/texture/sparkle.atex", "items/sparkle.atex"));
        }

        var itemManips = new List<string> { Eqdp("Highlander"), Eqdp("Hrothgar") };
        if (neighbours)
            itemManips.Add(imc.Replace("\"VfxId\":" + (effect ? 1 : 0), "\"VfxId\":2").Replace("\"Variant\":15", "\"Variant\":3"));
        var defaultManips = new List<string> { GlobalEqp };
        (imcInDefault ? defaultManips : itemManips).Insert(0, imc);

        var defaults = new List<(string GamePath, string Entry)>
        {
            ("vfx/FroVeil/FroVeilDustT.atex", "vfx/dust.atex"),
            ("vfx/FrostVeilVariant1.avfx", "vfx/variant1.avfx"),
        };
        if (avfxRedirected) defaults.Insert(1, ("chara/accessory/a0002/vfx/eff/va0001.avfx", "vfx/va0001.avfx"));

        string Files(IEnumerable<(string GamePath, string Entry)> files)
            => "{" + string.Join(",", files.Select(f => $"{Quote(f.GamePath)}:{Quote(f.Entry.Replace('/', '\\'))}")) + "}";
        string Option(string name, IEnumerable<(string, string)>? files = null, IEnumerable<string>? manips = null)
            => "{\"Name\":" + Quote(name) + ",\"Description\":\"\""
             + (files != null ? ",\"Files\":" + Files(files) : "")
             + (manips != null ? ",\"Manipulations\":[" + string.Join(",", manips) + "]" : "") + "}";
        string Group(string name, int priority, params string[] options)
            => "{\"Version\":0,\"Name\":" + Quote(name) + ",\"Description\":\"\",\"Image\":\"\",\"Page\":0,"
             + "\"Priority\":" + priority + ",\"Type\":\"Single\",\"DefaultSettings\":0,\"Options\":["
             + string.Join(",", options) + "]}";

        const string Va = "chara/accessory/a0002/vfx/eff/va0001.avfx";
        var groups = new[]
        {
            avfxRedirected
                ? Group("Style", 0,
                    Option("Frost"),
                    Option("Arcane Infusion", [(Va, "vfx/variant1.avfx")]),
                    Option("Ice Candies", [(Va, "vfx/variant2.avfx")]))
                : Group("Style", 0, Option("Frost")),
            Group("Glitter", 1,
                Option("On"),
                Option("Off", [("vfx/FroVeil/FroVeilDustT.atex", "glitter/off.atex")])),
            Group("Items", 0, Option("Astral Bracelet", items, itemManips)),
        };

        string defaultData = "{\"Version\":0,\"Files\":" + Files(defaults) + ",\"Manipulations\":["
                           + string.Join(",", defaultManips) + "]}";
        string metaHead = "\"Name\":\"[BM] Frost Veil Plus+\",\"Author\":\"Bijeon Milae\",\"Description\":\"\","
                        + "\"Image\":\"\",\"Version\":\"1.0.0\",\"Website\":\"\"";

        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "proteus-synth-" + Guid.NewGuid().ToString("N") + ".pmp");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            if (legacy)
            {
                Write(zip, "meta.json", Encoding.UTF8.GetBytes("{\"FileVersion\":3," + metaHead + "}"));
                Write(zip, "default_mod.json", Encoding.UTF8.GetBytes(defaultData));
                for (int i = 0; i < groups.Length; i++)
                    Write(zip, $"group_{i + 1:D3}_{(i == 0 ? "style" : i == 1 ? "glitter" : "items")}.json",
                          Encoding.UTF8.GetBytes(groups[i]));
            }
            else
            {
                Write(zip, "meta.json", Encoding.UTF8.GetBytes(
                    "{\"FileVersion\":4," + metaHead + ",\"DefaultData\":" + defaultData
                  + ",\"Groups\":[" + string.Join(",", groups) + "]}"));
            }

            Write(zip, Model, model);
            Write(zip, "items/v01_d.tex", [0]);
            if (materialPresent)
            {
                Write(zip, "items/v0001.mtrl", [0]);
                Write(zip, "items/v0015.mtrl", [0]);
            }
            if (neighbours)
            {
                Write(zip, "items/nek.mtrl", [0]);
                Write(zip, "items/sparkle.atex", [0]);
            }
            Write(zip, "vfx/dust.atex", [0]);
            Write(zip, "glitter/off.atex", [0]);
            if (avfxPresent)
            {
                Write(zip, "vfx/va0001.avfx", [1]);
                Write(zip, "vfx/variant1.avfx", [2]);
                Write(zip, "vfx/variant2.avfx", [3]);
            }
        }
        return new SyntheticPack(path);
    }

    /// <summary>
    /// One option of <see cref="ImcInOptions"/>. <paramref name="Mask"/> null is an empty option (a toggle's "Show"
    /// side); otherwise it writes one <c>Imc</c> entry per variant in <paramref name="Variants"/> (default just 1) with
    /// that mask, plus a same-set <c>Eqp</c> as real TexTools-converted toggles carry. <paramref name="Extra"/> is raw
    /// manipulation JSON appended as is.
    /// </summary>
    internal sealed record ImcOption(string Name, int? Mask, int Priority = 0, int[]? Variants = null,
                                     string? Extra = null, int? VariantOneMask = null);

    /// <summary>An <see cref="ImcOption"/>, named so a group's option list needs no target-typed <c>new</c>.</summary>
    internal static ImcOption O(string name, int? mask, int Priority = 0, int[]? Variants = null, string? Extra = null,
                                int? VariantOneMask = null)
        => new(name, mask, Priority, Variants, Extra, VariantOneMask);

    /// <summary>One Single or Multi group of <see cref="ImcInOptions"/>.</summary>
    internal sealed record ImcGroup(string Name, string Type, int Priority, ulong DefaultSettings,
                                    params ImcOption[] Options);

    /// <summary>The set <see cref="ImcInOptions"/>' Body piece is on.</summary>
    internal const int ImcSet = 6100;

    /// <summary>
    /// A pack whose Body piece (<c>e6100</c>) is switched by <c>Imc</c> edits inside ordinary Single/Multi options and
    /// default data — the shape Penumbra's TexTools import produces, files under <c>Group/Option/chara/...</c> entries.
    /// The model has one mesh each for <c>atr_tv_a</c>, <c>atr_tv_b</c>, <c>atr_dv_a</c> and <c>atr_ude</c>.
    /// </summary>
    /// <param name="defaultMask">An <c>Imc</c> entry in default data, or none.</param>
    /// <param name="legacy">Write the v3 layout: <c>default_mod.json</c> plus one <c>group_NNN_*.json</c> per group.</param>
    internal static SyntheticPack ImcInOptions(IReadOnlyList<ImcGroup> groups, int? defaultMask = null,
                                               bool legacy = false)
    {
        string[] attrs = ["atr_tv_a", "atr_tv_b", "atr_dv_a", "atr_ude"];
        var meshes = attrs
            .Select((_, i) => new SyntheticModel.Mesh($"/mt_c0201e{ImcSet}_top_{(char)('a' + i)}.mtrl",
                                                      new SyntheticModel.Sub(1u << i)))
            .ToArray();
        var model = SyntheticModel.Build(attrs, meshes);

        string modelPath = $"chara/equipment/e{ImcSet}/model/c0201e{ImcSet}_top.mdl";
        var files = new List<(string GamePath, string Entry)> { (modelPath, "base/" + modelPath) };
        foreach (var mesh in meshes)
        {
            var gp = $"chara/equipment/e{ImcSet}/material/v0001{mesh.Material}";
            files.Add((gp, "base/" + gp));
        }

        static string Imc(int variant, int mask) =>
            "{\"Type\":\"Imc\",\"Manipulation\":{\"Entry\":{\"MaterialId\":1,\"DecalId\":0,\"VfxId\":0,"
          + "\"MaterialAnimationId\":0,\"AttributeAndSound\":" + mask + ",\"AttributeMask\":" + mask
          + ",\"SoundId\":0},\"ObjectType\":\"Equipment\",\"PrimaryId\":" + ImcSet + ",\"SecondaryId\":0,"
          + "\"Variant\":" + variant + ",\"EquipSlot\":\"Body\",\"BodySlot\":\"Unknown\"}}";
        const string Eqp = "{\"Type\":\"Eqp\",\"Manipulation\":{\"Entry\":12884901888,\"SetId\":" + "6100"
                         + ",\"Slot\":\"Body\"}}";

        string Option(ImcOption o)
        {
            var manips = new List<string>();
            if (o.Mask is { } mask)
            {
                foreach (var v in o.Variants ?? [1])
                    manips.Add(Imc(v, v == 1 && o.VariantOneMask is { } one ? one : mask));
                manips.Add(Eqp);
            }
            if (o.Extra != null) manips.Add(o.Extra);
            return "{\"Name\":" + Quote(o.Name) + ",\"Description\":\"\",\"Priority\":" + o.Priority
                 + ",\"Files\":{},\"FileSwaps\":{},\"Manipulations\":[" + string.Join(",", manips) + "]}";
        }

        string Group(ImcGroup g, int index) =>
            "{\"Version\":0,\"Name\":" + Quote(g.Name) + ",\"Description\":\"\",\"Priority\":" + g.Priority
          + ",\"Type\":" + Quote(g.Type) + ",\"DefaultSettings\":" + g.DefaultSettings
          + ",\"Options\":[" + string.Join(",", g.Options.Select(Option)) + "]}";

        string defaultData = "{\"Files\":{"
            + string.Join(",", files.Select(f => $"{Quote(f.GamePath)}:{Quote(f.Entry)}"))
            + "},\"FileSwaps\":{},\"Manipulations\":["
            + (defaultMask is { } dm ? Imc(1, dm) : "") + "]}";

        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "proteus-synth-" + Guid.NewGuid().ToString("N") + ".pmp");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            if (legacy)
            {
                Write(zip, "meta.json", Encoding.UTF8.GetBytes(
                    "{\"FileVersion\":3,\"Name\":\"Synthetic\",\"Author\":\"Tests\",\"Description\":\"\"}"));
                Write(zip, "default_mod.json", Encoding.UTF8.GetBytes(defaultData));
                for (int i = 0; i < groups.Count; i++)
                    Write(zip, $"group_{i + 1:D3}_{groups[i].Name.ToLowerInvariant().Replace(' ', '_')}.json",
                          Encoding.UTF8.GetBytes(Group(groups[i], i)));
            }
            else
            {
                Write(zip, "meta.json", Encoding.UTF8.GetBytes(
                    "{\"FileVersion\":4,\"Name\":\"Synthetic\",\"Author\":\"Tests\",\"Description\":\"\","
                  + "\"DefaultData\":" + defaultData + ",\"Groups\":["
                  + string.Join(",", groups.Select(Group)) + "]}"));
            }
            Write(zip, "base/" + modelPath, model);
            foreach (var f in files.Skip(1)) Write(zip, f.Entry, [0]);
        }
        return new SyntheticPack(path);
    }

    /// <summary>
    /// <see cref="ImcToggled"/>'s Denim Shorts shape (set 6058 Legs, <c>atr_dv_a</c>/<c>atr_dv_b</c>, options
    /// "atr_dv_a Hide"/"atr_dv_b Hide"), with what an installed-mod import has to tell apart around its <c>Imc</c> group.
    /// <c>ImcToggled</c> ends in a <c>params</c> array, so these could not be added to it.
    /// </summary>
    /// <param name="imcDefaultSettings">The Imc group's <c>DefaultSettings</c> bitmask.</param>
    /// <param name="disableOption">Append Penumbra's "Disable" option and let the group be disabled.</param>
    /// <param name="otherItemGroup">A second Imc group after the first, for an item nobody imports (e0042 Body).</param>
    /// <param name="leadingGroup">A group of this name and type before the Imc group, with one empty option "A".</param>
    /// <param name="legacy">Write the v3 layout: <c>default_mod.json</c> plus one <c>group_NNN_*.json</c> per group.</param>
    internal static SyntheticPack ImcGroupPack(
        string groupName = "Toggles", ulong imcDefaultSettings = 0, bool disableOption = false,
        string? otherItemGroup = null, (string Name, string Type)? leadingGroup = null, bool legacy = false,
        (string Name, string Type)? trailingGroup = null)
    {
        string[] attributes = ["atr_dv_a", "atr_dv_b"];
        var meshes = attributes
            .Select((_, i) => new SyntheticModel.Mesh($"/mt_c0101e6058_dwn_{(char)('a' + i)}.mtrl",
                                                      new SyntheticModel.Sub(1u << i)))
            .ToArray();
        var model = SyntheticModel.Build(attributes, meshes);

        const string ModelEntry = "model.mdl";
        var files = new List<(string GamePath, string Entry)> { ("chara/equipment/e6058/model/c0101e6058_dwn.mdl", ModelEntry) };
        foreach (var mesh in meshes)
        {
            var leaf = mesh.Material.TrimStart('/');
            files.Add(($"chara/equipment/e6058/material/v0001/{leaf}", leaf));
        }

        static string Identifier(int set, string slot)
            => "\"Identifier\":{\"ObjectType\":\"Equipment\",\"PrimaryId\":" + set + ",\"Variant\":1,\"EquipSlot\":"
             + Quote(slot) + "},\"DefaultEntry\":{\"MaterialId\":1,\"DecalId\":0,\"VfxId\":0,"
             + "\"MaterialAnimationId\":0,\"AttributeMask\":3,\"SoundId\":0}";

        string Plain((string Name, string Type) g)
            => "{\"Type\":" + Quote(g.Type) + ",\"Name\":" + Quote(g.Name) + ",\"DefaultSettings\":0,"
             + (g.Type == "Imc" ? Identifier(42, "Body") + "," : "")
             + (g.Type == "Combining" ? "\"Containers\":[{}]," : "")
             + "\"Options\":[{\"Name\":\"A\"}]}";

        var groups = new List<string>();
        if (leadingGroup is { } lead) groups.Add(Plain(lead));

        var options = attributes.Select((a, i) => "{\"Name\":" + Quote(a + " Hide") + ",\"AttributeMask\":" + (1 << i) + "}")
            .ToList();
        if (disableOption) options.Add("{\"Name\":\"Disable\",\"AttributeMask\":0,\"IsDisableSubMod\":true}");
        groups.Add("{\"Type\":\"Imc\",\"Name\":" + Quote(groupName) + ",\"DefaultSettings\":" + imcDefaultSettings + ","
                 + (disableOption ? "\"CanBeDisabled\":true," : "") + Identifier(6058, "Legs") + ","
                 + "\"Options\":[" + string.Join(",", options) + "]}");

        if (otherItemGroup != null)
            groups.Add("{\"Type\":\"Imc\",\"Name\":" + Quote(otherItemGroup) + ",\"DefaultSettings\":0,"
                     + Identifier(42, "Body") + ",\"Options\":[{\"Name\":\"Hide A\",\"AttributeMask\":1}]}");
        if (trailingGroup is { } trail) groups.Add(Plain(trail));

        string defaultData = "{\"Files\":{" + string.Join(",", files.Select(f => $"{Quote(f.GamePath)}:{Quote(f.Entry)}"))
                           + "},\"FileSwaps\":{},\"Manipulations\":[]}";

        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "proteus-synth-" + Guid.NewGuid().ToString("N") + ".pmp");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            if (legacy)
            {
                Write(zip, "meta.json", Encoding.UTF8.GetBytes(
                    "{\"FileVersion\":3,\"Name\":\"Synthetic\",\"Author\":\"Tests\",\"Description\":\"\"}"));
                Write(zip, "default_mod.json", Encoding.UTF8.GetBytes(defaultData));
                for (int i = 0; i < groups.Count; i++)
                    Write(zip, $"group_{i + 1:D3}_g{i}.json", Encoding.UTF8.GetBytes(groups[i]));
            }
            else
                Write(zip, "meta.json", Encoding.UTF8.GetBytes(
                    "{\"FileVersion\":4,\"Name\":\"Synthetic\",\"Author\":\"Tests\",\"Description\":\"\","
                  + "\"DefaultData\":" + defaultData + ",\"Groups\":[" + string.Join(",", groups) + "]}"));
            Write(zip, ModelEntry, model);
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
