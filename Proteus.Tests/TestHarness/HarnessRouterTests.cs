using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Proteus.TestHarness;
using Xunit;

namespace Proteus.Tests.TestHarness;

public class HarnessRouterTests : IDisposable
{
    private readonly HarnessFixture f = new();
    public void Dispose() => f.Dispose();

    [Fact]
    public async Task EveryRoute_FrameworkMembersOnDispatcherThread()
    {
        // [AC-qa-harness-4] Every game-touching call every route makes runs on the framework thread.
        f.AddMod();
        await f.Get("/health");
        await f.Get("/state");
        await f.Get("/log");
        await f.Client.GetAsync("/screenshot");
        var (_, refit) = await f.Post("/actions/refit", """{"mod":"Outfit","bodies":["rue"]}""");
        var job = await f.WaitJob(refit.GetProperty("job").GetString()!);
        var (_, restore) = await f.Post("/actions/restore", $$"""{"job":"{{job.GetProperty("id").GetString()}}"}""");
        await f.WaitJob(restore.GetProperty("job").GetString()!);
        await f.Get($"/jobs/{job.GetProperty("id").GetString()}");

        var members = f.Game.Calls.Select(c => c.Member).Distinct().ToHashSet();
        foreach (var expected in new[] { "Health", "State", "NamesNow", "ViewportId", "Mods", "PrepareSizeSet", "AfterModChange" })
            Assert.Contains(expected, members);
        Assert.All(f.Game.Calls, c => Assert.Equal(f.Dispatcher.ThreadId, c.Thread));
    }

    [Fact]
    public async Task State_ModDir_SetOnlyUnderModsRoot()
    {
        // [AC-qa-harness-5]
        f.Game.Loaded = [(0, @"C:\elsewhere\a.mdl"), (1, $@"{f.ModsRoot}\Outfit\b.mdl")];
        f.Game.ResourceMap = new(StringComparer.OrdinalIgnoreCase)
        {
            [@"C:\elsewhere\a.mdl"] = new(["chara/a.mdl"]),
            [$@"{f.ModsRoot}\Outfit\b.mdl"] = new(["chara/b.mdl"]),
        };
        var models = (await f.Get("/state")).Body.GetProperty("models").EnumerateArray().ToList();
        Assert.Equal(JsonValueKind.Null, models.Single(m => m.GetProperty("slot").GetInt32() == 0).GetProperty("modDir").ValueKind);
        Assert.Equal("Outfit", models.Single(m => m.GetProperty("slot").GetInt32() == 1).GetProperty("modDir").GetString());
    }

    [Fact]
    public async Task State_NoCharacterBase_PenumbraOnlyEntriesSlotMinus1()
    {
        // [EDGE-qa-harness-25] Mid-redraw: nothing drawn, Penumbra's models listed with slot -1.
        f.Game.Loaded = [];
        f.Game.ResourceMap = new(StringComparer.OrdinalIgnoreCase) { [$@"{f.ModsRoot}\Outfit\b.mdl"] = new(["chara/b.mdl"]) };
        var m = (await f.Get("/state")).Body.GetProperty("models")[0];
        Assert.Equal(-1, m.GetProperty("slot").GetInt32());
        Assert.Equal(JsonValueKind.Null, m.GetProperty("loaded").ValueKind);
    }

    [Fact]
    public async Task State_Job_Report_PathsScrubbed()
    {
        // [EDGE-qa-harness-27] Paths in /state and job responses carry no Windows user name.
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        f.AddMod();
        f.Game.Loaded = [(1, $@"{profile}\AppData\Somewhere\b.mdl")];
        var (_, state) = await f.Get("/state");
        var (_, refit) = await f.Post("/actions/refit", """{"mod":"Outfit","bodies":["rue"]}""");
        var job = await f.WaitJob(refit.GetProperty("job").GetString()!);
        var texts = new[] { state.ToString(), job.ToString() };
        // The job's backup path is under the temp folder: only a user path when TEMP is under the profile.
        bool tempUnderProfile = f.ModsRoot.StartsWith(profile, StringComparison.OrdinalIgnoreCase);
        foreach (var text in tempUnderProfile ? texts : texts[..1])
        {
            Assert.DoesNotContain(profile.Replace("\\", "\\\\"), text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(profile, text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("<USER>", text);
        }
    }

    [Fact]
    public async Task Refit_DisplayName_404()
    {
        // [EDGE-qa-harness-31] Folder names only.
        f.AddMod();
        var (status, b) = await f.Post("/actions/refit", """{"mod":"Outfit (display)","bodies":["rue"]}""");
        Assert.Equal(404, status);
        Assert.Equal("mod-not-found", b.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task FolderMatch_CaseInsensitive()
    {
        // [EDGE-qa-harness-31]
        f.AddMod();
        var (status, b) = await f.Post("/actions/refit", """{"mod":"OUTFIT","bodies":["rue"]}""");
        Assert.Equal(202, status);
        Assert.Equal("Outfit", b.GetProperty("modDir").GetString());
        await f.WaitJob(b.GetProperty("job").GetString()!);
    }

    [Fact]
    public async Task Refit_ManagedMod_400()
    {
        // [EDGE-qa-harness-32] Proteus's own output mod is never refitted.
        f.AddMod("Proteus");
        var (status, b) = await f.Post("/actions/refit", """{"mod":"Proteus","bodies":["rue"]}""");
        Assert.Equal(400, status);
        Assert.Equal("managed-mod", b.GetProperty("error").GetProperty("code").GetString());
    }

    private async Task<(int Status, string Code)> Error(string path, string json)
    {
        var (status, b) = await f.Post(path, json);
        return (status, b.ValueKind == JsonValueKind.Object && b.TryGetProperty("error", out var e) ? e.GetProperty("code").GetString()! : "");
    }

    [Fact]
    public async Task Restore_BadBodies_400()
    {
        // [EDGE-qa-harness-33] job with mod, nothing at all, an unknown field, an unsafe folder name.
        Assert.Equal((400, "invalid-field"), await Error("/actions/restore", """{"job":"abc","mod":"Outfit"}"""));
        Assert.Equal((400, "invalid-field"), await Error("/actions/restore", "{}"));
        Assert.Equal((400, "invalid-field"), await Error("/actions/restore", """{"mod":"Outfit","force":true}"""));
        Assert.Equal((400, "invalid-field"), await Error("/actions/restore", """{"mod":".."}"""));
        Assert.Equal((400, "invalid-field"), await Error("/actions/restore", """{"mod":5}"""));
        Assert.True(f.Jobs.TryReserve());
        f.Jobs.Release();
    }

    [Fact]
    public async Task Restore_ModGoneFromPenumbra_404()
    {
        // [EDGE-qa-harness-31] A backup whose mod Penumbra no longer lists is not restored into a folder it doesn't know.
        string mod = f.AddMod();
        f.Backups.Create(mod, "Outfit", BackupKind.Refit, "session-a", f.Jobs.NextSeq(), null, System.Threading.CancellationToken.None);
        f.Game.ModFolders.Remove("Outfit");
        Assert.Equal((404, "mod-not-found"), await Error("/actions/restore", """{"mod":"Outfit"}"""));
    }

    [Fact]
    public async Task Restore_ManagedMod_400()
    {
        // [EDGE-qa-harness-32]
        string mod = f.AddMod("Proteus");
        f.Backups.Create(mod, "Proteus", BackupKind.Refit, "session-a", f.Jobs.NextSeq(), null, System.Threading.CancellationToken.None);
        Assert.Equal((400, "managed-mod"), await Error("/actions/restore", """{"mod":"Proteus"}"""));
    }

    [Fact]
    public async Task Restore_ManagedModWithoutBackup_400NotNoBackup()
    {
        // [EDGE-qa-harness-32] In-game QA: the managed mod was refused as no-backup; it is refused as itself.
        Assert.Equal((400, "managed-mod"), await Error("/actions/restore", """{"mod":"proteus"}"""));
    }

    [Fact]
    public async Task Refit_MissingFields_400()
    {
        // [EDGE-qa-harness-33]
        f.AddMod();
        Assert.Equal((400, "invalid-field"), await Error("/actions/refit", """{"bodies":["rue"]}"""));
        Assert.Equal((400, "invalid-field"), await Error("/actions/refit", """{"mod":"Outfit"}"""));
        Assert.Equal((400, "invalid-field"), await Error("/actions/refit", """{"mod":"Outfit","bodies":["all"]}"""));
        Assert.Equal((400, "invalid-field"), await Error("/actions/refit", """{"mod":"Outfit","bodies":["rue,bibo"]}"""));
        Assert.Equal((400, "invalid-field"), await Error("/actions/refit", """{"mod":"Outfit","bodies":[5]}"""));
    }

    [Fact]
    public void ParseBodies_DeduplicatesAndNormalises()
    {
        // [EDGE-qa-harness-33] Case and repeats don't matter; the table's keys come back in the order asked.
        using var doc = JsonDocument.Parse("""["Rue","rue","TBSE@women","tbse@WOMEN","bibo"]""");
        var (keys, error) = HarnessRouter.ParseBodies(doc.RootElement);
        Assert.Null(error);
        Assert.Equal(["rue", "tbse@women", "bibo"], keys);
    }

    [Fact]
    [Trait("Plan", "AC-dt-shader-upgrade-10")]
    public void Routes_AreTheContract()
    {
        Assert.Equal(["/health", "/state", "/log", "/screenshot", "/actions/refit", "/actions/restore",
                      "/actions/shader-upgrade", "/actions/variant-repoint", "/jobs/{id}"],
                     HarnessRouter.Routes.Select(r => r.Path));
    }

    [Fact]
    [Trait("Plan", "AC-dt-shader-upgrade-10")]
    public async Task State_reports_the_shader_upgrade_block()
    {
        f.Game.ShaderUpgradeView = new Proteus.Services.ShaderUpgradeWatcher.View(
            Enabled: true, Waiting: "none", MaskGloss: "mid",
            Upgraded: [new("chara/equipment/e0001/material/v0001/mt_c0201e0001_top_a.mtrl", "Dress", "red/top.mtrl", "diffuse",
                           "chara/equipment/e0001/texture/dt_ab_mask.tex", "Shader Upgrade/ab_mask.tex")],
            Left: [new("chara/equipment/e0002/material/v0001/mt_c0201e0002_dwn_a.mtrl", "Skirt/b.mtrl", "specular-from-other-mod")],
            MemoHits: 2, Decodes: 1,
            LastRun: new("undo-worn", 1, 2, 2, 0, 0, true, true, DateTime.UtcNow));
        var (status, b) = await f.Get("/state");
        Assert.Equal(200, status);
        var su = b.GetProperty("shaderUpgrade");
        Assert.True(su.GetProperty("enabled").GetBoolean());
        Assert.Equal("mid", su.GetProperty("maskGloss").GetString());
        var up = su.GetProperty("upgraded")[0];
        Assert.Equal("Dress", up.GetProperty("modDir").GetString());
        Assert.Equal("diffuse", up.GetProperty("form").GetString());
        Assert.Equal("Shader Upgrade/ab_mask.tex", up.GetProperty("mask").GetProperty("file").GetString());
        Assert.Equal("specular-from-other-mod", su.GetProperty("left")[0].GetProperty("reason").GetString());
        Assert.Equal(2, su.GetProperty("masks").GetProperty("memoHits").GetInt32());
        Assert.Equal("undo-worn", su.GetProperty("lastRun").GetProperty("kind").GetString());
    }

    [Fact]
    [Trait("Plan", "AC-dt-shader-upgrade-10")]
    [Trait("Plan", "EDGE-dt-shader-upgrade-69")]
    public async Task Shader_upgrade_action_validates_and_starts_undo_worn_and_upgrade_now()
    {
        var (status, b) = await f.Post("/actions/shader-upgrade", """{"enabled":true,"maskGloss":"low"}""");
        Assert.Equal(200, status);
        Assert.True(b.GetProperty("enabled").GetBoolean());
        Assert.Equal("low", b.GetProperty("maskGloss").GetString());

        (status, b) = await f.Post("/actions/shader-upgrade", """{"undoWorn":true,"decline":false}""");
        Assert.Equal(202, status);
        (status, _) = await f.Post("/actions/shader-upgrade", """{"upgradeWornNow":true}""");
        Assert.Equal(202, status);
        Assert.Equal([(true, false), (false, true)], f.Game.ShaderUpgradeRuns);

        f.Game.ShaderUpgradeBusy = true;
        Assert.Equal((409, "busy"), await Error("/actions/shader-upgrade", """{"undoWorn":true}"""));
        f.Game.ShaderUpgradeBusy = false;

        Assert.Equal((400, "invalid-field"), await Error("/actions/shader-upgrade", "{}"));
        Assert.Equal((400, "invalid-field"), await Error("/actions/shader-upgrade", """{"undo":"all"}"""));
        Assert.Equal((400, "invalid-field"), await Error("/actions/shader-upgrade", """{"decline":false}"""));
        Assert.Equal((400, "invalid-field"), await Error("/actions/shader-upgrade", """{"undoWorn":true,"upgradeWornNow":true}"""));
        Assert.Equal((400, "invalid-field"), await Error("/actions/shader-upgrade", """{"enabled":"yes"}"""));
        Assert.Equal((400, "invalid-field"), await Error("/actions/shader-upgrade", """{"maskGloss":"shiny"}"""));
        Assert.Equal((400, "bad-json"), await Error("/actions/shader-upgrade", "{not json"));
        var (getStatus, _) = await f.Get("/actions/shader-upgrade");
        Assert.Equal(405, getStatus);
        Assert.Contains(f.Game.Order, o => o == "StartShaderUpgradeRun@framework");
    }

    [Fact]
    [Trait("Plan", "AC-imc-all-variants-10")]
    public async Task Variant_repoint_state_block_shape()
    {
        f.Game.VariantRepointView = new Proteus.Services.VariantRepointWatcher.View(
            Enabled: true, Waiting: "none", AwaitingRedraw: true,
            Worn: [new("This Old Thing", "e6255 Body", 2, 1, "v0001"), new(null, "a0053 Earrings", 1, null, null)],
            Repointed: [new("This Old Thing", "e6255 Body", 1, [0, 2])],
            Left: [new("Skirt", "e6256 Legs", "several-variants")],
            LastRun: new("repoint", 1, 2, 0, 0, 0, true, true, DateTime.UtcNow));
        var (status, b) = await f.Get("/state");
        Assert.Equal(200, status);
        var vr = b.GetProperty("variantRepoint");
        Assert.True(vr.GetProperty("enabled").GetBoolean());
        Assert.True(vr.GetProperty("awaitingRedraw").GetBoolean());
        Assert.Equal("none", vr.GetProperty("waiting").GetString());
        var worn = vr.GetProperty("worn")[0];
        Assert.Equal("This Old Thing", worn.GetProperty("modDir").GetString());
        Assert.Equal("e6255 Body", worn.GetProperty("item").GetString());
        Assert.Equal(2, worn.GetProperty("variant").GetInt32());
        Assert.Equal(1, worn.GetProperty("effectiveMaterialId").GetInt32());
        Assert.Equal("v0001", worn.GetProperty("loadedFolder").GetString());
        Assert.Equal(JsonValueKind.Null, vr.GetProperty("worn")[1].GetProperty("modDir").ValueKind);
        var rp = vr.GetProperty("repointed")[0];
        Assert.Equal(1, rp.GetProperty("materialId").GetInt32());
        Assert.Equal(2, rp.GetProperty("variants").GetArrayLength());
        Assert.Equal("several-variants", vr.GetProperty("left")[0].GetProperty("reason").GetString());
        var run = vr.GetProperty("lastRun");
        Assert.Equal("repoint", run.GetProperty("kind").GetString());
        Assert.Equal(2, run.GetProperty("entriesWritten").GetInt32());
        Assert.False(run.GetProperty("nothingRecorded").GetBoolean());
    }

    [Fact]
    [Trait("Plan", "AC-imc-all-variants-10")]
    public async Task Variant_repoint_action_validates_and_starts()
    {
        var (status, b) = await f.Post("/actions/variant-repoint", """{"enabled":true}""");
        Assert.Equal(200, status);
        Assert.True(b.GetProperty("enabled").GetBoolean());
        Assert.True(f.Game.VariantRepointEnabled);

        (status, _) = await f.Post("/actions/variant-repoint", """{"undoWorn":true,"decline":false}""");
        Assert.Equal(202, status);
        (status, _) = await f.Post("/actions/variant-repoint", """{"repointWornNow":true}""");
        Assert.Equal(202, status);
        Assert.Equal([(true, false), (false, true)], f.Game.VariantRepointRuns);

        f.Game.VariantRepointBusy = true;
        Assert.Equal((409, "busy"), await Error("/actions/variant-repoint", """{"undoWorn":true}"""));
        f.Game.VariantRepointBusy = false;
        f.Game.VariantRepointOthersBusy = true;   // the shader upgrade or auto refit
        Assert.Equal((409, "busy"), await Error("/actions/variant-repoint", """{"repointWornNow":true}"""));
        f.Game.VariantRepointOthersBusy = false;

        Assert.Equal((400, "invalid-field"), await Error("/actions/variant-repoint", "{}"));
        Assert.Equal((400, "invalid-field"), await Error("/actions/variant-repoint", """{"undo":"all"}"""));
        Assert.Equal((400, "invalid-field"), await Error("/actions/variant-repoint", """{"decline":false}"""));
        Assert.Equal((400, "invalid-field"), await Error("/actions/variant-repoint", """{"undoWorn":true,"repointWornNow":true}"""));
        Assert.Equal((400, "invalid-field"), await Error("/actions/variant-repoint", """{"enabled":"yes"}"""));
        Assert.Equal((400, "invalid-field"), await Error("/actions/variant-repoint", """{"undoWorn":false}"""));
        Assert.Equal((400, "bad-json"), await Error("/actions/variant-repoint", "{not json"));
        var (getStatus, _) = await f.Get("/actions/variant-repoint");
        Assert.Equal(405, getStatus);
        Assert.Contains(f.Game.Order, o => o == "StartVariantRepointRun@framework");
    }
}
