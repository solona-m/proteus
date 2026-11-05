using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Proteus.Services;
using static Proteus.Services.VariantRepointPlan;

namespace Proteus.Tests;

/// <summary>
/// Shared inputs for the "Gear variants" tests: a temporary mods folder holding v4 mods built the way Penumbra writes
/// them, synthetic five-slot <c>.imc</c> files, a Penumbra meta blob builder, and a resolver that answers like
/// <c>ResolvePlayerPaths</c>: the highest-priority mod redirecting a path serves it (mods made first rank highest, every
/// option counted as on), else the game path itself.
/// </summary>
internal sealed class VariantRepointTestKit : IDisposable
{
    public static readonly Item Top = new('e', 6255, "top");
    public static readonly Item Earring = new('a', 53, "ear");
    public static readonly Item RightRing = new('a', 53, "rir");
    public static readonly Item LeftRing = new('a', 53, "ril");

    public readonly string ModsRoot = Path.Combine(Path.GetTempPath(), "proteus-vr-" + Guid.NewGuid().ToString("N")[..8]);
    public string ManagedRoot => Path.Combine(ModsRoot, SidecarDiscoveryService.ManagedModDir);

    /// <summary>Game files by game path (the synthetic <c>.imc</c>s).</summary>
    public Dictionary<string, byte[]> Game { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Paths served by something other than an installed mod's redirect (Proteus's output, outside), or forced.</summary>
    public Dictionary<string, string> Served { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Mods by priority, highest first (creation order unless reordered).</summary>
    public List<string> Priority { get; } = [];

    /// <summary>The collection's IMC; entries the tests add stand for other mods' edits.</summary>
    public Dictionary<PenumbraMetaImc.Key, ImcEntry> Collection { get; } = new();

    public HashSet<string> RefreshPending { get; } = new(StringComparer.OrdinalIgnoreCase);

    public VariantRepointTestKit() => Directory.CreateDirectory(ModsRoot);

    public void Dispose()
    {
        VariantRepointWriter.AfterStep = null;
        try { Directory.Delete(ModsRoot, recursive: true); } catch { /* temp */ }
    }

    public static ImcEntry E(int mat, int decal = 0, int attr = 0, int sound = 0, int vfx = 0, int anim = 0)
        => new((byte)mat, (byte)decal, (ushort)attr, (byte)sound, (byte)vfx, (byte)anim);

    /// <summary>A five-slot <c>.imc</c> for the item's set: <paramref name="sets"/>[s] for set s (0 = the default set).</summary>
    public void Imc(Item item, params ImcEntry[] sets) => ImcSlots(item.SetTag, (SlotIndex(item), sets));

    public void ImcSlots(string setTag, params (int Slot, ImcEntry[] Sets)[] slots)
    {
        int count = slots.Max(s => s.Sets.Length) - 1;
        var b = new byte[4 + (count + 1) * 5 * 6];
        BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)count);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), 31);
        foreach (var (slot, sets) in slots)
            for (int s = 0; s < sets.Length; s++)
            {
                int at = 4 + (s * 5 + slot) * 6;
                var e = sets[s];
                b[at] = e.MaterialId;
                b[at + 1] = e.DecalId;
                BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(at + 2), (ushort)(e.AttributeMask | (e.SoundId << 10)));
                b[at + 4] = e.VfxId;
                b[at + 5] = e.MaterialAnimationId;
            }
        Game[ImcEntrySource.ImcPathForSet(setTag)!] = b;
    }

    public static int SlotIndex(Item item) => item.Suffix switch
    {
        "met" or "ear" => 0, "top" or "nek" => 1, "glv" or "wrs" => 2, "dwn" or "rir" => 3, _ => 4,
    };

    public static string MaterialPath(Item item, int folder, string name = "a")
        => item.MaterialDir(folder) + $"mt_c0201{item.SetTag}_{item.Suffix}_{name}.mtrl";

    public static string ModelPath(Item item, string race = "0201")
        => $"chara/{(item.Kind == 'a' ? "accessory" : "equipment")}/{item.SetTag}/model/c{race}{item.SetTag}_{item.Suffix}.mdl";

    public delegate (string Option, Dictionary<string, string> Files, object[] Manips) OptionSpec();

    /// <summary>
    /// A v4 mod: default data files and manipulations, and groups (name, type, options of game path → file and their
    /// manipulations). Raw groups (Imc groups) are appended as given.
    /// </summary>
    public string Mod(string name, Dictionary<string, string>? defaults = null, IEnumerable<object>? defaultManips = null,
                      IEnumerable<(string Group, string Type, (string Option, Dictionary<string, string> Files, object[] Manips)[] Options)>? groups = null,
                      IEnumerable<object>? rawGroups = null, string version = "1.0")
    {
        var root = Path.Combine(ModsRoot, name);
        Directory.CreateDirectory(root);
        var gs = (groups ?? []).Select(g => g.Type == "Combining"
            ? (object)new
            {
                Name = g.Group, Description = "", Priority = 0, Type = "Combining", DefaultSettings = 0,
                Options = g.Options.Select(o => new { Name = o.Option, Description = "" }),
                Containers = g.Options.Select(o => new { Files = o.Files, FileSwaps = new Dictionary<string, string>(),
                                                         Manipulations = o.Manips }),
            }
            : new
            {
                Name = g.Group, Description = "", Priority = 0, Type = g.Type, DefaultSettings = 0,
                Options = g.Options.Select(o => new { Name = o.Option, Description = "", Files = o.Files,
                                                      FileSwaps = new Dictionary<string, string>(), Manipulations = o.Manips }),
            }).Concat(rawGroups ?? []).ToList();
        var meta = new Dictionary<string, object>
        {
            ["FileVersion"] = 4, ["Name"] = name, ["Author"] = "Someone", ["Version"] = version,
            ["Groups"] = gs,
            ["DefaultData"] = new { Files = defaults ?? new Dictionary<string, string>(), FileSwaps = new Dictionary<string, string>(),
                                    Manipulations = (defaultManips ?? []).ToArray() },
        };
        File.WriteAllText(Path.Combine(root, "meta.json"), JsonSerializer.Serialize(meta));
        if (!Priority.Contains(root)) Priority.Add(root);
        return root;
    }

    /// <summary>The standard case: a mod dressing <paramref name="item"/> at one folder in its default data.</summary>
    public string Dress(string name, Item item, int folder = 1, string material = "a")
        => Mod(name, new Dictionary<string, string>
        {
            [ModelPath(item)] = $"{item.Suffix}.mdl",
            [MaterialPath(item, folder, material)] = $"{item.Suffix}_{material}.mtrl",
        });

    /// <summary>A mod with a Single "Colour" group: Red and Blue each ship the item's material at folder 1, own files.</summary>
    public string Colours(string name, Item item, string material = "a")
        => Mod(name, new Dictionary<string, string> { [ModelPath(item)] = $"{item.Suffix}.mdl" },
               groups: [("Colour", "Single", [("Red", new() { [MaterialPath(item, 1, material)] = "red/m.mtrl" }, []),
                                              ("Blue", new() { [MaterialPath(item, 1, material)] = "blue/m.mtrl" }, [])])]);

    public string LegacyMod(string name, Item item)
    {
        var root = Path.Combine(ModsRoot, name);
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "meta.json"), "{\"FileVersion\":3,\"Name\":\"" + name + "\"}");
        File.WriteAllText(Path.Combine(root, "default_mod.json"), JsonSerializer.Serialize(new
        {
            Files = new Dictionary<string, string> { [MaterialPath(item, 1)] = "a.mtrl", [ModelPath(item)] = "top.mdl" },
            FileSwaps = new Dictionary<string, string>(), Manipulations = Array.Empty<object>(),
        }));
        Priority.Add(root);
        return root;
    }

    public WornItem Worn(Item item, int variant, string modRoot)
        => new(item, variant, ModelPath(item), Path.Combine(modRoot, $"{item.Suffix}.mdl"));

    public Inputs Inputs() => new()
    {
        ModsRoot = ModsRoot,
        ManagedRoot = ManagedRoot,
        ReadGameFile = p => Game.TryGetValue(p, out var b) ? b : null,
    };

    /// <summary>What Penumbra resolves each path to: a forced answer, else the highest-priority mod redirecting it, else the game path.</summary>
    public Dictionary<string, string?> Resolve(IEnumerable<string> requests)
        => requests.ToDictionary(r => r, r => (string?)ResolveOne(r), StringComparer.OrdinalIgnoreCase);

    public string ResolveOne(string path)
    {
        if (Served.TryGetValue(path, out var f)) return f;
        foreach (var root in Priority)
            foreach (var x in PenumbraModMeta.ReadContainerRedirects(root))
                if (PenumbraModMeta.SameFile(x.GamePath, path)) return Path.Combine(root, x.File.Replace('/', Path.DirectorySeparatorChar));
        return path;
    }

    /// <summary>The collection's IMC: every mod's own Imc manipulations (legacy entries included), then the tests' entries.</summary>
    public Dictionary<PenumbraMetaImc.Key, ImcEntry> EffectiveNow()
    {
        var all = new Dictionary<PenumbraMetaImc.Key, ImcEntry>();
        foreach (var dir in Priority.AsEnumerable().Reverse())
            foreach (var (edit, isDefault, _) in ManifestImcEdits(dir))
                if (isDefault)
                    all[new PenumbraMetaImc.Key(edit.Item.Kind, edit.Item.SetId, edit.Item.SlotLabel, edit.Variant)] = edit.Entry;
        foreach (var (k, v) in Collection) all[k] = v;
        return all;
    }

    /// <summary>Plan the worn pieces as the watcher does: reconcile, candidates, the collection's entries, resolve, build.</summary>
    public Plan Plan(IEnumerable<WornItem> worn, bool metaUnreadable = false, Inputs? inputs = null)
    {
        var inp = inputs ?? Inputs();
        foreach (var w in worn)
            if (w.ModelResolved != null && HatCompatService.InMods(w.ModelResolved, ModsRoot, out var root, out _))
                VariantRepointWriter.Reconcile(root);
        var (candidates, left) = Candidates(worn, inp);
        var effective = metaUnreadable ? null : EffectiveNow();
        var resolved = effective == null ? new Dictionary<string, string?>() : Resolve(ResolveRequests(candidates, effective));
        return Build(candidates, effective, resolved, RefreshPending, inp, left);
    }

    /// <summary>Plan and write: the outcome per mod, and everything left.</summary>
    public (List<VariantRepointWriter.Outcome> Outcomes, List<Left> Left) Run(IEnumerable<WornItem> worn,
                                                                             Func<bool>? mayWrite = null)
    {
        var plan = Plan(worn);
        var outcomes = plan.PerMod.Select(m => VariantRepointWriter.Apply(m.ModRoot, m.Items, mayWrite ?? (() => true))).ToList();
        return (outcomes, plan.Left);
    }

    /// <summary>The redirects of one container of a mod, game path → file.</summary>
    public static Dictionary<string, string> FilesOf(string modRoot, PenumbraModMeta.ContainerRef at)
        => PenumbraModMeta.ReadContainerRedirects(modRoot).Where(r => r.Container.SameAs(at))
                          .ToDictionary(r => r.GamePath, r => r.File, StringComparer.OrdinalIgnoreCase);

    public static readonly PenumbraModMeta.ContainerRef Default = new("");

    public static JsonObject Manifest(string modRoot) => JsonNode.Parse(File.ReadAllText(Path.Combine(modRoot, "meta.json")))!.AsObject();

    public static void WriteManifest(string modRoot, JsonObject manifest)
        => File.WriteAllText(Path.Combine(modRoot, "meta.json"), manifest.ToJsonString());

    /// <summary>Edit one container's Files object in the manifest (default data when <paramref name="group"/> is null).</summary>
    public static void EditFiles(string modRoot, Action<JsonObject> edit, string? group = null, string? option = null, int? index = null)
    {
        var m = Manifest(modRoot);
        JsonObject owner;
        if (group == null) owner = m["DefaultData"]!.AsObject();
        else
        {
            var g = m["Groups"]!.AsArray().First(x => x!["Name"]!.GetValue<string>() == group)!.AsObject();
            owner = index is { } i ? g["Containers"]![i]!.AsObject()
                  : g["Options"]!.AsArray().First(o => o!["Name"]!.GetValue<string>() == option)!.AsObject();
        }
        edit(owner["Files"]!.AsObject());
        WriteManifest(modRoot, m);
    }

    /// <summary>An Imc manipulation as Proteus (build 1428) and Penumbra write it.</summary>
    public static object Imc(Item item, int variant, ImcEntry e)
        => PenumbraManipulations.ImcManipulation(item.Kind, item.SetId, variant, item.EquipSlot, e);

    /// <summary>An Imc group as <c>PenumbraModMeta.WriteImcGroup</c> (and so <c>MeshToggleService</c>) writes it.</summary>
    public static Dictionary<string, object> ImcGroup(string name, Item item, bool allVariants = true, int variant = 0,
                                                      bool onlyAttributes = true, int material = 1)
        => new()
        {
            ["Type"] = "Imc", ["Name"] = name, ["Description"] = "", ["Priority"] = 0, ["DefaultSettings"] = 1,
            ["AllVariants"] = allVariants, ["OnlyAttributes"] = onlyAttributes,
            ["Identifier"] = new Dictionary<string, object>
            {
                ["ObjectType"] = item.ObjectType, ["PrimaryId"] = item.SetId, ["Variant"] = variant, ["EquipSlot"] = item.EquipSlot,
            },
            ["DefaultEntry"] = new Dictionary<string, object>
            {
                ["MaterialId"] = material, ["DecalId"] = 0, ["VfxId"] = 0, ["MaterialAnimationId"] = 0, ["AttributeMask"] = 0, ["SoundId"] = 0,
            },
            ["Options"] = new[] { new Dictionary<string, object> { ["Name"] = "Part A", ["AttributeMask"] = 1 } },
        };

    /// <summary>A Proteus part-toggle record naming a group for an item.</summary>
    public static void ToggleRecord(string modRoot, Item item, string groupName)
    {
        var dir = Path.Combine(modRoot, SidecarDiscoveryService.SidecarSubdir);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, MeshToggleService.RecordFile), JsonSerializer.Serialize(new
        {
            Items = new[] { new { GroupName = groupName, SetId = item.SetId, Slot = item.EquipSlot, Files = Array.Empty<string>(),
                                  Toggles = new Dictionary<string, string>() } },
        }));
    }

    /// <summary>A version-1 record as build 1428 wrote it, for legacy IMC entries already in the mod's default data.</summary>
    public static void LegacyRecord(string modRoot, Item item, int folder, params (int Variant, ImcEntry Entry)[] entries)
    {
        var dir = Path.Combine(modRoot, SidecarDiscoveryService.SidecarSubdir);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, VariantRepointWriter.RecordFile), JsonSerializer.Serialize(new
        {
            Version = 1, Fingerprint = VariantRepointPlan.Fingerprint(modRoot),
            Items = new[]
            {
                new
                {
                    ObjectType = item.ObjectType, SetId = item.SetId, Slot = item.EquipSlot, MaterialId = folder, Pending = false,
                    Variants = entries.Select(e => new { Variant = e.Variant, Entry = VariantRepointWriter.EntryJson.From(e.Entry) }),
                },
            },
            Declined = Array.Empty<string>(),
        }));
    }

    // ── Penumbra's meta blob ────────────────────────────────────────────────

    /// <summary>A <c>GetPlayerMetaManipulations</c> blob holding IMC records, in version 1 or 2.</summary>
    public static string Blob(IEnumerable<(byte ObjectType, int Set, int Variant, byte EquipSlot, ImcEntry Entry)> records,
                              int version = 2, bool truncate = false)
    {
        var recs = records.ToList();
        using var raw = new MemoryStream();
        raw.WriteByte((byte)version);
        raw.Write(Encoding.ASCII.GetBytes(version == 1 ? "META0001" : "META0002"));
        Span<byte> four = stackalloc byte[4];
        if (version == 2)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(four, ((uint)'I' << 24) | ((uint)'M' << 16) | ((uint)'C' << 8));
            raw.Write(four);
        }
        BinaryPrimitives.WriteInt32LittleEndian(four, recs.Count);
        raw.Write(four);
        foreach (var r in recs)
        {
            var b = new byte[14];
            BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)r.Set);
            b[2] = (byte)r.Variant;
            b[3] = r.ObjectType;
            b[6] = r.EquipSlot;
            b[8] = r.Entry.MaterialId;
            b[9] = r.Entry.DecalId;
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(10), (ushort)(r.Entry.AttributeMask | (r.Entry.SoundId << 10)));
            b[12] = r.Entry.VfxId;
            b[13] = r.Entry.MaterialAnimationId;
            raw.Write(b);
        }
        var bytes = raw.ToArray();
        if (truncate) bytes = bytes[..^5];
        using var packed = new MemoryStream();
        using (var gz = new GZipStream(packed, CompressionLevel.Fastest, leaveOpen: true)) gz.Write(bytes);
        return Convert.ToBase64String(packed.ToArray());
    }
}
