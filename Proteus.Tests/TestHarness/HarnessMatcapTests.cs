using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using NSubstitute;
using Proteus.Interop;
using Proteus.Services;
using Proteus.Services.Matcap;
using Proteus.TestHarness;
using Xunit;

namespace Proteus.Tests.TestHarness;

/// <summary>
/// The matcap routes over the real server: request parsing and refusals, a backed-up apply through the panel's entry
/// points on a real (synthetic) mod, and put-back of the library setting and the seeded image.
/// </summary>
[Collection(HarnessTimingCollection.Name)]
public class HarnessMatcapTests : IDisposable
{
    private readonly HarnessFixture f = new();
    private readonly FakeStudio studio;
    private readonly string images;
    private readonly MatcapIndex index = new(null);
    private readonly LedgerPenumbra penumbra;
    private readonly TextureLoader loader = new(Substitute.For<Dalamud.Plugin.Services.IDataManager>(),
                                                Substitute.For<Dalamud.Plugin.Services.IPluginLog>());

    /// <summary>Library writes land in the fake game's permanent settings, which the binding ledger reads.</summary>
    private sealed class LedgerPenumbra(FakeGame game, string root) : IMatcapPenumbra
    {
        public (Guid Id, string Name)? PlayerCollection() => (game.Collection!.Value, "Mine");
        public PenumbraBridge.ModSettingsSnapshot? Settings(Guid c, string mod) =>
            game.Permanent.TryGetValue(mod, out var p)
                ? new PenumbraBridge.ModSettingsSnapshot(p.Enabled, p.Priority, p.Options.ToDictionary(k => k.Key, k => k.Value), !p.Own, false)
                : null;
        public PenumbraBridge.TemporaryHold? Hold(Guid c, string mod) => new PenumbraBridge.TemporaryHold(false, false, "", false, 0);
        public bool SetEnabled(Guid c, string mod, bool enabled)
        {
            var p = game.Permanent.GetValueOrDefault(mod) ?? PermanentSetting.Inherits;
            game.Permanent[mod] = p with { Enabled = enabled, Own = true };
            return true;
        }
        public bool SetOption(Guid c, string mod, string group, string option)
        {
            var p = game.Permanent.GetValueOrDefault(mod) ?? PermanentSetting.Inherits;
            var opts = p.Options.ToDictionary(k => k.Key, k => k.Value);
            opts[group] = [option];
            game.Permanent[mod] = p with { Options = opts, Own = true };
            return true;
        }
        public IReadOnlyDictionary<string, string?>? Resolve(IReadOnlyList<string> gamePaths) => null;
        public string? ModsRoot() => root;
    }

    public HarnessMatcapTests()
    {
        studio = f.Game.UseStudio();
        images = Path.Combine(f.Temp.Path, "matcap-images");
        Directory.CreateDirectory(images);
        penumbra = new LedgerPenumbra(f.Game, f.ModsRoot);
        studio.MatcapPort = () => new MatcapHarnessPort(index, images, penumbra,
            new MatcapStudio.Environment(p => p == MatcapMaterial.VanillaTemplatePath ? SyntheticMatcapMod.Reflection() : null,
                                         loader, false, Path.Combine(f.Temp.Path, "matcap-temp")),
            f.ModsRoot, new Dictionary<string, string>(f.Game.ModFolders));
    }

    public void Dispose() => f.Dispose();

    private static string Code(JsonElement body) => body.GetProperty("error").GetProperty("code").GetString()!;

    /// <summary>A gear mod whose top has two submeshes, published at the e6255 top path.</summary>
    private string AddGearMod(string dir = "Gear")
    {
        var model = SyntheticModel.Build([],
            [new SyntheticModel.Mesh("/mt_c0201e6255_top_a.mtrl", new SyntheticModel.Sub(0, TrianglesPerIsland: 3),
                                     new SyntheticModel.Sub(0, TrianglesPerIsland: 2, OffsetY: 10f))],
            SyntheticModel.V6, (IReadOnlyList<string>?)null);
        var root = SyntheticMatcapMod.TargetMod(f.ModsRoot, dir, new Dictionary<string, (string, byte[])>
        {
            [MatcapFixture.ModelPath] = (MatcapFixture.ModelRel, model),
        });
        f.Game.ModFolders[dir] = dir;
        return root;
    }

    [Theory]
    [Trait("Plan", "AC-matcaps-10")]
    [Trait("Plan", "EDGE-matcap-pack-16")]
    [InlineData("""{"model":"x.mdl","image":"a.png","parts":["1.1"]}""", "mod and model")]
    [InlineData("""{"mod":"Gear","model":"x.mdl","image":"a.png","library":"L","group":"g","option":"o","parts":["1.1"]}""", "not both")]
    [InlineData("""{"mod":"Gear","model":"x.mdl","library":"L","parts":["1.1"]}""", "group and option")]
    [InlineData("""{"mod":"Gear","model":"x.mdl","undo":true,"parts":["1.1"]}""", "undo and forget")]
    [InlineData("""{"mod":"Gear","model":"x.mdl","image":"a.png"}""", "parts")]
    [InlineData("""{"mod":"Gear","model":"x.mdl","parts":[]}""", "non-empty")]
    [InlineData("""{"mod":"Gear","model":"x.mdl","nope":1}""", "unknown field")]
    [InlineData("""{"mod":"Gear","model":"x.mdl","image":"a.png","parts":["1.1"],"pack":"yes"}""", "pack must be true or false")]
    [InlineData("""{"mod":"Gear","model":"x.mdl","parts":["1.1"],"pack":true}""", "pack goes with image")]
    public async Task Route_parsing_and_refusals(string json, string fragment)
    {
        AddGearMod();
        var (status, body) = await f.Post("/actions/matcap", json);
        Assert.Equal(400, status);
        Assert.Equal("invalid-field", Code(body));
        Assert.Contains(fragment, body.GetProperty("error").GetProperty("message").GetString());

        var (s2, b2) = await f.Post("/actions/matcap-image", """{"name":"../evil.png"}""");
        Assert.Equal(400, s2);
        Assert.Equal("invalid-field", Code(b2));
    }

    [Fact]
    [Trait("Plan", "EDGE-matcaps-22")]
    public async Task Pending_studio_edit_is_studio_busy()
    {
        AddGearMod();
        studio.Pending = true;
        var (status, body) = await f.Post("/actions/matcap",
            """{"mod":"Gear","model":"model/top.mdl","parts":["1.2"],"image":"qa-x.png"}""");
        Assert.Equal(409, status);
        Assert.Equal("studio-busy", Code(body));
        // Nothing was backed up or written: refused in the request's framework hop.
        var backups = Path.Combine(f.Temp.Path, "config", "test-harness", "backups", "Gear");
        Assert.False(Directory.Exists(backups));
        Assert.False(MatcapRecord.Exists(Path.Combine(f.ModsRoot, "Gear")));
    }

    [Fact]
    [Trait("Plan", "AC-matcaps-10")]
    public async Task Job_backs_up_and_calls_MatcapStudio()
    {
        var root = AddGearMod();
        var (s1, seeded) = await f.Post("/actions/matcap-image", """{"name":"qa-blue.png","color":"#3060ff"}""");
        Assert.Equal(200, s1);
        Assert.True(File.Exists(Path.Combine(images, "qa-blue.png")));

        var (s2, queued) = await f.Post("/actions/matcap", """{"mod":"Gear","model":"model/top.mdl","parts":["1.2"],"image":"qa-blue.png"}""");
        Assert.Equal(202, s2);
        var job = await f.WaitJob(queued.GetProperty("job").GetString()!);
        Assert.Equal("done", job.GetProperty("state").GetString());
        Assert.Equal("matcap", job.GetProperty("kind").GetString());
        Assert.NotEqual(JsonValueKind.Null, job.GetProperty("backup").ValueKind);
        var result = job.GetProperty("result");
        Assert.Equal("applied", result.GetProperty("summary").GetString());
        Assert.StartsWith("/mt_c0201e6255_top_a_mcap", result.GetProperty("material").GetString());

        // The panel's record and files, written by the same entry points.
        var record = MatcapRecord.Read(root);
        Assert.Equal("qa-blue.png", Assert.Single(record.Materials).Source.Image);

        // Undo as a job puts the model back.
        var (s3, undoQueued) = await f.Post("/actions/matcap", """{"mod":"Gear","model":"model/top.mdl","undo":true}""");
        Assert.Equal(202, s3);
        var undo = await f.WaitJob(undoQueued.GetProperty("job").GetString()!);
        Assert.Equal("undone", undo.GetProperty("result").GetProperty("summary").GetString());
        Assert.False(MatcapRecord.Exists(root));

        // The listing shows the image and the model's parts.
        var (s4, view) = await f.Get("/matcaps?mod=Gear&model=model/top.mdl");
        Assert.Equal(200, s4);
        Assert.Contains(view.GetProperty("images").EnumerateArray(), i => i.GetProperty("name").GetString() == "qa-blue.png");
        Assert.Contains(view.GetProperty("model").GetProperty("parts").EnumerateArray(), p => p.GetProperty("label").GetString() == "1.2");
    }

    [Fact]
    [Trait("Plan", "AC-matcaps-10")]
    public async Task PutBack_restores_library_setting_and_deletes_seeded_image()
    {
        AddGearMod();
        SyntheticMatcapMod.Library(f.ModsRoot, "Lib", "Test Library", "https://x.test", ["chara/common/texture/testlib_a.tex"], ["Gem Red", "Pearl"]);
        f.Game.ModFolders["Lib"] = "Lib";
        f.Game.Permanent["Lib"] = new PermanentSetting(true, 0,
            new Dictionary<string, List<string>> { ["Texture Slot A"] = ["Gem Red"] }, true);
        await f.Post("/actions/matcap-image", """{"name":"qa-red.png","color":"#ff2020"}""");
        var (sv, _) = await f.Get("/matcaps?refresh=1");
        Assert.Equal(200, sv);

        var (s, queued) = await f.Post("/actions/matcap",
            """{"mod":"Gear","model":"model/top.mdl","parts":["1.2"],"library":"Lib","group":"Texture Slot A","option":"Pearl"}""");
        Assert.Equal(202, s);
        var job = await f.WaitJob(queued.GetProperty("job").GetString()!);
        Assert.Equal("applied", job.GetProperty("result").GetProperty("summary").GetString());
        Assert.Equal("Pearl", f.Game.Permanent["Lib"].Options["Texture Slot A"].Single());

        var (sp, report) = await f.Post("/actions/put-back", "{}");
        Assert.Equal(200, sp);
        var matcap = report.GetProperty("matcap");
        Assert.Contains(matcap.GetProperty("settings").EnumerateArray(), e => e.GetProperty("mod").GetString() == "Lib" && e.GetProperty("result").GetString() == "restored");
        Assert.Contains(matcap.GetProperty("images").EnumerateArray(), e => e.GetProperty("result").GetString() == "deleted");
        Assert.Equal("Gem Red", f.Game.Permanent["Lib"].Options["Texture Slot A"].Single());
        Assert.False(File.Exists(Path.Combine(images, "qa-red.png")));
    }

    [Fact]
    [Trait("Plan", "EDGE-matcaps-39")]
    public async Task Seeded_image_changed_is_left()
    {
        await f.Post("/actions/matcap-image", """{"name":"qa-green.png","color":"#20ff20"}""");
        File.AppendAllText(Path.Combine(images, "qa-green.png"), "edited by someone");
        var (_, report) = await f.Post("/actions/put-back", "{}");
        Assert.Contains(report.GetProperty("matcap").GetProperty("images").EnumerateArray(),
                        e => e.GetProperty("result").GetString() == "changed");
        Assert.True(File.Exists(Path.Combine(images, "qa-green.png")));
        var (s2, again) = await f.Post("/actions/matcap-image", """{"name":"qa-green.png"}""");
        Assert.Equal(409, s2);
        Assert.Equal("exists", Code(again));
    }

    [Fact]
    [Trait("Plan", "EDGE-matcaps-12")]
    public async Task Library_that_is_off_needs_confirm_and_writes_nothing()
    {
        var root = AddGearMod();
        SyntheticMatcapMod.Library(f.ModsRoot, "Lib", "Test Library", "", ["chara/common/texture/testlib_a.tex"], ["Gem Red", "Pearl"]);
        f.Game.ModFolders["Lib"] = "Lib";
        f.Game.Permanent["Lib"] = new PermanentSetting(false, 0);
        await f.Get("/matcaps?refresh=1");
        var before = File.ReadAllBytes(Path.Combine(root, "model", "top.mdl"));
        var (_, queued) = await f.Post("/actions/matcap",
            """{"mod":"Gear","model":"model/top.mdl","parts":["1.2"],"library":"Lib","group":"Texture Slot A","option":"Pearl"}""");
        var job = await f.WaitJob(queued.GetProperty("job").GetString()!);
        Assert.Equal("failed", job.GetProperty("state").GetString());
        Assert.Equal("needs-confirm", job.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(root, "model", "top.mdl")));
        Assert.False(f.Game.Permanent["Lib"].Enabled);
    }

    // ── Apply changes a matcap (matcap-preview) ──────────────────────────────────────────────────────────────────────

    /// <summary>Apply qa-red on 1.2, then the labels of the parts on a matcap and of the rest, from GET /matcaps.</summary>
    private async Task<(string Root, List<string> OnMatcap, List<string> Plain)> AppliedOnce()
    {
        var root = AddGearMod();
        await f.Post("/actions/matcap-image", """{"name":"qa-red.png","color":"#ff2020"}""");
        await f.Post("/actions/matcap-image", """{"name":"qa-blue.png","color":"#2040ff"}""");
        var (_, q) = await f.Post("/actions/matcap", """{"mod":"Gear","model":"model/top.mdl","parts":["1.2"],"image":"qa-red.png"}""");
        var job = await f.WaitJob(q.GetProperty("job").GetString()!);
        Assert.Equal("applied", job.GetProperty("result").GetProperty("summary").GetString());
        var (_, view) = await f.Get("/matcaps?mod=Gear&model=model/top.mdl");
        var parts = view.GetProperty("model").GetProperty("parts").EnumerateArray().Where(p => p.GetProperty("island").GetInt32() < 0).ToList();
        return (root,
                parts.Where(p => p.GetProperty("onMatcap").GetBoolean()).Select(p => p.GetProperty("label").GetString()!).ToList(),
                parts.Where(p => !p.GetProperty("onMatcap").GetBoolean() && !p.GetProperty("skin").GetBoolean()).Select(p => p.GetProperty("label").GetString()!).ToList());
    }

    [Fact]
    [Trait("Plan", "AC-matcap-preview-9")]
    public async Task Apply_on_matcap_parts_changes_it()
    {
        var (root, onMatcap, _) = await AppliedOnce();
        Assert.NotEmpty(onMatcap);
        var model = File.ReadAllBytes(Path.Combine(root, "model", "top.mdl"));
        var (s, q) = await f.Post("/actions/matcap", JsonSerializer.Serialize(new { mod = "Gear", model = "model/top.mdl", parts = onMatcap, image = "qa-blue.png" }));
        Assert.Equal(202, s);
        var job = await f.WaitJob(q.GetProperty("job").GetString()!);
        Assert.Equal("matcap", job.GetProperty("kind").GetString());
        var result = job.GetProperty("result");
        Assert.Equal("changed", result.GetProperty("summary").GetString());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("files").ValueKind);
        Assert.Equal(JsonValueKind.Null, result.GetProperty("drawn").ValueKind);
        Assert.Single(result.GetProperty("materials").EnumerateArray());
        Assert.Equal("qa-blue.png", Assert.Single(MatcapRecord.Read(root).Materials).Source.Image);
        Assert.Equal(model, File.ReadAllBytes(Path.Combine(root, "model", "top.mdl")));
    }

    [Fact]
    [Trait("Plan", "AC-matcap-preview-9")]
    public async Task Entry_field_is_refused()
    {
        AddGearMod();
        var (s, body) = await f.Post("/actions/matcap", """{"mod":"Gear","model":"model/top.mdl","entry":"/x_mcapabcdef1.mtrl","image":"a.png"}""");
        Assert.Equal(400, s);
        Assert.Equal("invalid-field", Code(body));
        Assert.Contains("unknown field", body.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    [Trait("Plan", "AC-matcap-preview-9")]
    public async Task Get_reports_matcap_parts_tickable()
    {
        await AppliedOnce();
        var (_, view) = await f.Get("/matcaps?mod=Gear&model=model/top.mdl");
        Assert.All(view.GetProperty("model").GetProperty("parts").EnumerateArray().Where(p => p.GetProperty("onMatcap").GetBoolean()),
                   p => Assert.True(p.GetProperty("tickable").GetBoolean()));
    }

    [Fact]
    [Trait("Plan", "EDGE-matcap-preview-2")]
    public async Task Mixed_parts_already_matcap_writes_nothing()
    {
        var (root, onMatcap, plain) = await AppliedOnce();
        var before = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Where(p => !p.Contains("meta.json"))
                              .ToDictionary(p => p, p => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p))));
        var (_, q) = await f.Post("/actions/matcap", JsonSerializer.Serialize(new { mod = "Gear", model = "model/top.mdl", parts = new[] { onMatcap[0], plain[0] }, image = "qa-blue.png" }));
        var job = await f.WaitJob(q.GetProperty("job").GetString()!);
        Assert.Equal("refused", job.GetProperty("result").GetProperty("summary").GetString());
        Assert.Equal("already-matcap", job.GetProperty("result").GetProperty("code").GetString());
        var after = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Where(p => !p.Contains("meta.json"))
                             .ToDictionary(p => p, p => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p))));
        Assert.Equal(before.OrderBy(k => k.Key), after.OrderBy(k => k.Key));
    }

    [Fact]
    [Trait("Plan", "EDGE-matcap-preview-5")]
    [Trait("Plan", "EDGE-matcap-preview-3")]
    public async Task Change_refusal_summary_is_refused()
    {
        var (root, onMatcap, _) = await AppliedOnce();
        SyntheticMatcapMod.Library(f.ModsRoot, "Lib", "Test Library", "", ["chara/common/texture/testlib_a.tex"], ["Gem Red", "Pearl"]);
        f.Game.ModFolders["Lib"] = "Lib";
        f.Game.Permanent["Lib"] = new PermanentSetting(true, 0);
        await f.Get("/matcaps?refresh=1");
        var (_, q) = await f.Post("/actions/matcap", JsonSerializer.Serialize(new { mod = "Gear", model = "model/top.mdl", parts = onMatcap, library = "Lib", group = "Texture Slot A", option = "Pearl" }));
        var job = await f.WaitJob(q.GetProperty("job").GetString()!);
        Assert.Equal("refused", job.GetProperty("result").GetProperty("summary").GetString());
        Assert.Equal(MatcapWriter.CodeCrossKind, job.GetProperty("result").GetProperty("code").GetString());
        Assert.Equal("qa-red.png", Assert.Single(MatcapRecord.Read(root).Materials).Source.Image);
    }

    [Fact]
    [Trait("Plan", "AC-matcap-preview-4")]
    [Trait("Plan", "EDGE-matcap-preview-6")]
    public async Task Change_to_off_library_needs_confirm_and_writes_nothing()
    {
        var root = AddGearMod();
        SyntheticMatcapMod.Library(f.ModsRoot, "Lib", "Test Library", "", ["chara/common/texture/testlib_a.tex"], ["Gem Red", "Pearl"]);
        f.Game.ModFolders["Lib"] = "Lib";
        f.Game.Permanent["Lib"] = new PermanentSetting(true, 0);
        await f.Get("/matcaps?refresh=1");
        var (_, q1) = await f.Post("/actions/matcap", """{"mod":"Gear","model":"model/top.mdl","parts":["1.2"],"library":"Lib","group":"Texture Slot A","option":"Gem Red"}""");
        Assert.Equal("applied", (await f.WaitJob(q1.GetProperty("job").GetString()!)).GetProperty("result").GetProperty("summary").GetString());
        var (_, view) = await f.Get("/matcaps?mod=Gear&model=model/top.mdl");
        var onMatcap = view.GetProperty("model").GetProperty("parts").EnumerateArray()
            .Where(p => p.GetProperty("onMatcap").GetBoolean() && p.GetProperty("island").GetInt32() < 0).Select(p => p.GetProperty("label").GetString()).ToList();

        f.Game.Permanent["Lib"] = f.Game.Permanent["Lib"] with { Enabled = false };
        var record = File.ReadAllText(MatcapRecord.PathOf(root));
        var (_, q2) = await f.Post("/actions/matcap", JsonSerializer.Serialize(new { mod = "Gear", model = "model/top.mdl", parts = onMatcap, library = "Lib", group = "Texture Slot A", option = "Pearl" }));
        var job = await f.WaitJob(q2.GetProperty("job").GetString()!);
        Assert.Equal("failed", job.GetProperty("state").GetString());
        Assert.Equal("needs-confirm", job.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(record, File.ReadAllText(MatcapRecord.PathOf(root)));
        Assert.False(f.Game.Permanent["Lib"].Enabled);
    }

    // ── Proteus's own matcaps (matcap-pack) ──────────────────────────────────────────────────────────────────────────

    /// <summary>A scripted pack: records how it was asked, lists real PNGs from its own folder.</summary>
    private sealed class FakePack(string dir) : IMatcapPack
    {
        public readonly List<(bool Retry, bool EvenIfReady)> Calls = [];
        public MatcapPackStatus Status { get; set; } = new(MatcapPackState.Downloading, 3, 39, "");
        public long Version => 1;
        public IReadOnlyList<MatcapImage> Available() =>
            Directory.Exists(dir)
                ? Directory.GetFiles(dir).Order(StringComparer.OrdinalIgnoreCase)
                    .Select(p => new MatcapImage(Path.GetFileName(p), p, false, FromPack: true)).ToList()
                : [];
        public void EnsureAsync(bool retryFailed = false, bool evenIfReady = false)
        {
            lock (Calls) Calls.Add((retryFailed, evenIfReady));
        }
    }

    private FakePack UsePack(bool penumbra = true, params string[] packImages)
    {
        var dir = Path.Combine(f.Temp.Path, "pack");
        Directory.CreateDirectory(dir);
        foreach (var n in packImages) File.WriteAllBytes(Path.Combine(dir, n), HarnessRouter.SpherePng((200, 160, 40)));
        var pack = new FakePack(dir);
        studio.MatcapPort = () => new MatcapHarnessPort(index, images, this.penumbra,
            new MatcapStudio.Environment(p => p == MatcapMaterial.VanillaTemplatePath ? SyntheticMatcapMod.Reflection() : null,
                                         loader, false, Path.Combine(f.Temp.Path, "matcap-temp")),
            penumbra ? f.ModsRoot : null, penumbra ? new Dictionary<string, string>(f.Game.ModFolders) : null, Pack: pack);
        return pack;
    }

    [Fact]
    [Trait("Plan", "AC-matcap-pack-9")]
    public async Task Get_starts_the_pack_and_reports_its_state()
    {
        var pack = UsePack();
        var (s, view) = await f.Get("/matcaps");
        Assert.Equal(200, s);
        Assert.Equal([(false, false)], pack.Calls);
        var p = view.GetProperty("pack");
        Assert.Equal("downloading", p.GetProperty("state").GetString());
        Assert.Equal(3, p.GetProperty("have").GetInt32());
        Assert.Equal(39, p.GetProperty("total").GetInt32());
        Assert.Equal(JsonValueKind.String, p.GetProperty("detail").ValueKind);
    }

    [Fact]
    [Trait("Plan", "AC-matcap-pack-9")]
    public async Task Get_retry_rearms_the_pack()
    {
        var pack = UsePack();
        await f.Get("/matcaps?retry=1");
        await f.Get("/matcaps?retry=0");
        await f.Get("/matcaps");
        Assert.Equal([(true, true), (false, false), (false, false)], pack.Calls);
    }

    [Fact]
    [Trait("Plan", "AC-matcap-pack-5")]
    [Trait("Plan", "EDGE-matcap-pack-1")]
    public async Task Get_lists_global_and_proteus_images_with_source()
    {
        AddGearMod();
        File.WriteAllBytes(Path.Combine(images, "Gold Metal.png"), HarnessRouter.SpherePng((10, 10, 10)));
        UsePack(true, "Gold Metal.png");
        var (s, view) = await f.Get("/matcaps?mod=Gear&model=model/top.mdl");
        Assert.Equal(200, s);
        var gold = view.GetProperty("images").EnumerateArray().Where(i => i.GetProperty("name").GetString() == "Gold Metal.png").ToList();
        Assert.Equal(["global", "proteus"], gold.Select(i => i.GetProperty("source").GetString()));
        var modelImages = view.GetProperty("model").GetProperty("images").EnumerateArray().ToList();
        Assert.Contains(modelImages, i => i.GetProperty("name").GetString() == "Gold Metal.png" && i.GetProperty("fromPack").GetBoolean());
        Assert.Contains(modelImages, i => i.GetProperty("name").GetString() == "Gold Metal.png" && !i.GetProperty("fromPack").GetBoolean());
    }

    [Fact]
    [Trait("Plan", "EDGE-matcap-pack-10")]
    public async Task Get_without_Penumbra_still_starts_the_pack()
    {
        var pack = UsePack(penumbra: false);
        var (s, body) = await f.Get("/matcaps");
        Assert.Equal(503, s);
        Assert.Equal("penumbra-unavailable", Code(body));
        Assert.Single(pack.Calls);
        Assert.Equal("downloading", body.GetProperty("pack").GetProperty("state").GetString());
    }

    [Fact]
    [Trait("Plan", "AC-matcap-pack-4")]
    public async Task Apply_with_a_pack_image_records_its_name()
    {
        var root = AddGearMod();
        UsePack(true, "Gold Metal.png");
        var (s, queued) = await f.Post("/actions/matcap", """{"mod":"Gear","model":"model/top.mdl","parts":["1.2"],"image":"Gold Metal.png"}""");
        Assert.Equal(202, s);
        var job = await f.WaitJob(queued.GetProperty("job").GetString()!);
        Assert.Equal("applied", job.GetProperty("result").GetProperty("summary").GetString());
        Assert.Equal("Gold Metal.png", Assert.Single(MatcapRecord.Read(root).Materials).Source.Image);
    }

    [Fact]
    [Trait("Plan", "AC-matcap-pack-9")]
    public async Task Pack_true_picks_the_proteus_copy_over_a_user_image()
    {
        var root = AddGearMod();
        // Different bytes per source, so the baked texture's hash tells which one was used.
        File.WriteAllBytes(Path.Combine(images, "Gold Metal.png"), HarnessRouter.SpherePng((10, 200, 10)));
        UsePack(true, "Gold Metal.png");
        string Sha(string path) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)))[..8].ToLowerInvariant();
        var packSha = Sha(Path.Combine(f.Temp.Path, "pack", "Gold Metal.png"));
        var userSha = Sha(Path.Combine(images, "Gold Metal.png"));
        Assert.NotEqual(packSha, userSha);

        var (_, q1) = await f.Post("/actions/matcap", """{"mod":"Gear","model":"model/top.mdl","parts":["1.2"],"image":"Gold Metal.png","pack":true}""");
        await f.WaitJob(q1.GetProperty("job").GetString()!);
        Assert.Equal(packSha, Assert.Single(MatcapRecord.Read(root).Materials).Source.ImageSha8);

        var (_, u) = await f.Post("/actions/matcap", """{"mod":"Gear","model":"model/top.mdl","undo":true}""");
        await f.WaitJob(u.GetProperty("job").GetString()!);
        var (_, q2) = await f.Post("/actions/matcap", """{"mod":"Gear","model":"model/top.mdl","parts":["1.2"],"image":"Gold Metal.png"}""");
        await f.WaitJob(q2.GetProperty("job").GetString()!);
        Assert.Equal(userSha, Assert.Single(MatcapRecord.Read(root).Materials).Source.ImageSha8);
    }

    [Fact]
    [Trait("Plan", "EDGE-matcap-pack-16")]
    public async Task Pack_true_with_a_user_only_name_is_unknown_image()
    {
        AddGearMod();
        File.WriteAllBytes(Path.Combine(images, "mine.png"), HarnessRouter.SpherePng((10, 10, 200)));
        UsePack(true, "Gold Metal.png");
        var (_, q) = await f.Post("/actions/matcap", """{"mod":"Gear","model":"model/top.mdl","parts":["1.2"],"image":"mine.png","pack":true}""");
        var job = await f.WaitJob(q.GetProperty("job").GetString()!);
        Assert.Equal("unknown-image", job.GetProperty("error").GetProperty("code").GetString());
    }
}
