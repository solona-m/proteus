using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Proteus.Gui;
using Proteus.Services;
using Proteus.TestHarness;
using Xunit;

namespace Proteus.Tests.TestHarness;

/// <summary>
/// The Studio's "Port to all races" through the test harness, over a real loopback server: a women's hair mod (c0201
/// hairstyle 5, option Cut / Long) on the cross-sex fixtures' deformer and game files.
/// </summary>
public class HarnessHairPortTests : IDisposable
{
    private const string Hair = "chara/human/c0201/obj/hair/h0005/model/c0201h0005_hir.mdl";
    private readonly HarnessFixture f = new();
    public void Dispose() => f.Dispose();

    private string Root => Path.Combine(f.ModsRoot, "Hair");

    private async Task<(int Status, JsonElement Body)> PostHair(string fields)
        => await f.Post("/actions/hair-port", "{" + fields + "}");

    /// <summary>Post a hair job and wait for it to end.</summary>
    private async Task<JsonElement> Job(string fields)
    {
        var (status, body) = await PostHair(fields);
        Assert.True(status == 202, body.ToString());
        return await f.WaitJob(body.GetProperty("job").GetString()!);
    }

    private static string Port(params string[] races)
        => $"\"mod\":\"Hair\",\"hair\":\"{Hair}\"" + (races.Length > 0 ? ",\"races\":[" + string.Join(",", races.Select(r => $"\"{r}\"")) + "]" : "");

    private static JsonElement Result(JsonElement job) => job.GetProperty("result");

    private static List<string> Codes(JsonElement array, string field = "race")
        => array.EnumerateArray().Select(e => e.GetProperty(field).GetString()!).ToList();

    private async Task<JsonElement> View(string mod = "Hair", string hair = Hair)
    {
        var (status, body) = await f.Get($"/hair-port?mod={Uri.EscapeDataString(mod)}&hair={Uri.EscapeDataString(hair)}");
        Assert.True(status == 200, body.ToString());
        return body;
    }

    private static JsonElement Row(JsonElement view, string race)
        => view.GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("race").GetString() == race);

    private bool NoBackups() => !Directory.Exists(Path.Combine(f.Temp.Path, "config", "test-harness", "backups", "Hair"))
                                || !Directory.EnumerateFileSystemEntries(Path.Combine(f.Temp.Path, "config", "test-harness", "backups", "Hair")).Any();

    // ── The rows ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-hair-port-qa-1")]
    [Trait("Plan", "EDGE-hair-port-qa-27")]
    public async Task HairPortView_lists_the_Studio_rows()
    {
        using var mod = f.AddHairMod();
        CrossSexFixtures.HandPort(mod, [101, 701], CrossSexFixtures.Game(f.Game.GameFiles));
        HairPortWriter.Undo(mod.Root, e => e.TargetGamePath.Contains("c0701"), decline: true);
        HairPortScan.RememberWarning(mod.Root, CrossSexFixtures.Target, "it does not sit on c0101's head");
        HairPortScan.RememberWarning(mod.Root, HairPortPlan.HairPath(401, 5), "stale warning");
        var before = FolderState.Of(mod.Root);

        var view = await View();
        var (source, rows) = HairPortPanel.ReadRows(mod.Root, Hair, null, CrossSexFixtures.Game(f.Game.GameFiles));
        Assert.Equal(rows.Select(r => HairPortJob.RaceCode(r.Race)), Codes(view.GetProperty("rows")));
        Assert.Equal((source, "c0201", 5), (view.GetProperty("source").GetString(), view.GetProperty("sourceRace").GetString(),
                                            view.GetProperty("id").GetInt32()));
        Assert.Equal(HairPortPanel.OtherSexNote(5), view.GetProperty("otherSexNote").GetString());
        var ticks = HairPortPanel.DefaultTicks(rows).ToHashSet();
        foreach (var r in rows)
        {
            var row = Row(view, HairPortJob.RaceCode(r.Race));
            Assert.Equal((r.OtherSex, r.OwnVersion, r.OwnSkeleton, HairPortPanel.CanTick(r), ticks.Contains(r.Race)),
                         (row.GetProperty("otherSex").GetBoolean(), row.GetProperty("ownVersion").GetBoolean(),
                          row.GetProperty("ownSkeleton").GetBoolean(), row.GetProperty("tickable").GetBoolean(),
                          row.GetProperty("defaultTicked").GetBoolean()));
            Assert.Equal(HairPortPanel.StateText(r), row.GetProperty("stateText").GetString());
        }
        Assert.DoesNotContain(rows, r => r.OtherSex && ticks.Contains(r.Race));
        Assert.Equal("ported", Row(view, "c0101").GetProperty("state").GetString());
        Assert.True(Row(view, "c0701").GetProperty("declined").GetBoolean());
        // The warning is the row's only while its port is there.
        Assert.Contains("c0101", Row(view, "c0101").GetProperty("warning").GetString());
        Assert.Equal("", Row(view, "c0401").GetProperty("warning").GetString());
        var port = view.GetProperty("ports").EnumerateArray().Single();
        Assert.Equal(("c0101", "Cut", "Long", false), (port.GetProperty("race").GetString(), port.GetProperty("group").GetString(),
                                                         port.GetProperty("option").GetString(), port.GetProperty("auto").GetBoolean()));
        FolderState.AssertSame(before, mod.Root);
    }

    [Fact]
    [Trait("Plan", "EDGE-hair-port-qa-3")]
    public async Task HairPortView_of_a_port_stands_for_its_author()
    {
        using var mod = f.AddHairMod();
        CrossSexFixtures.HandPort(mod, [101], CrossSexFixtures.Game(f.Game.GameFiles));
        var view = await View(hair: CrossSexFixtures.Target);
        Assert.Equal(Hair, view.GetProperty("source").GetString());

        var job = await Job($"\"mod\":\"Hair\",\"hair\":\"{CrossSexFixtures.Target}\",\"remove\":true");
        Assert.Equal("done", job.GetProperty("state").GetString());
        Assert.Equal(1, Result(job).GetProperty("removed").GetInt32());
        Assert.Empty(HairPortRecord.Read(mod.Root).Ports);
    }

    // ── Porting ──────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [Trait("Plan", "AC-hair-port-qa-2")]
    [Trait("Plan", "AC-hair-port-qa-9")]
    [Trait("Plan", "EDGE-hair-port-qa-2")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HairPort_job_writes_what_the_Studio_writes(bool mixedCase)
    {
        using var mod = f.AddHairMod();
        using var twin = CrossSexFixtures.WomensMod();
        string spelled = Hair;
        if (mixedCase)
        {
            spelled = "Chara/Human/c0201/Obj/Hair/h0005/Model/c0201h0005_hir.mdl";
            foreach (var m in new[] { mod, twin })
                CrossSexFixtures.EditMeta(m, n =>
                {
                    var files = CrossSexFixtures.Option(n, "Long")["Files"]!.AsObject();
                    files.Remove(Hair);
                    files[spelled] = "long.mdl";
                });
        }
        var before = FolderState.Of(mod.Root);
        FrozenStudio.Port(twin.Root, spelled, [401, 101], CrossSexFixtures.Pbd(), CrossSexFixtures.Game(f.Game.GameFiles));

        // Asked with backslashes and another case: found, and ported as the manifest spells it.
        string asked = Hair.ToUpperInvariant().Replace('/', '\\').Replace("\\", "\\\\");
        var job = await Job($"\"mod\":\"Hair\",\"hair\":\"{asked}\",\"races\":[\"c0401\",\"c0101\"]");
        Assert.True(job.GetProperty("state").GetString() == "done", job.ToString());
        Assert.Equal("hair-port", job.GetProperty("kind").GetString());
        var result = Result(job);
        Assert.Equal("ported", result.GetProperty("summary").GetString());
        Assert.Equal(["c0101", "c0401"], Codes(result.GetProperty("ported")).Order());
        Assert.Equal(2, result.GetProperty("saved").GetInt32());
        Assert.Equal(spelled, result.GetProperty("source").GetString());
        Assert.Equal(string.Format(Proteus.Localization.Strings.HairPort.DoneFmt, 2), result.GetProperty("resultLine").GetString());
        Assert.Equal(FrozenStudio.Tree(twin.Root), FrozenStudio.Tree(mod.Root));

        // Backed up before the save: the backup holds the mod as it was.
        // (The job reports its backup's path scrubbed of the user name: look it up by id.)
        var backup = f.Backups.Find("Hair", job.GetProperty("backup").GetProperty("id").GetString()!).Backup!;
        FolderState.AssertSame(before, backup.FilesPath);
        Assert.Equal(1, f.Game.AfterChanges);
        Assert.DoesNotContain(f.Game.Order, o => o == "ReadGameFile@framework");
        Assert.Equal("ported", Row(await View(), "c0101").GetProperty("state").GetString());
    }

    [Fact]
    [Trait("Plan", "AC-hair-port-qa-2")]
    public async Task HairPort_cross_sex_physics_reports_the_carried_skeleton()
    {
        using var mod = f.AddHairMod(physics: true);
        var job = await Job(Port("c0101"));
        var ported = Result(job).GetProperty("ported").EnumerateArray().Single();
        // The woman's own skeleton 5, carried — never the man's skeleton of that number.
        Assert.Equal(5, ported.GetProperty("skeletonFrom").GetInt32());
        Assert.True(ported.GetProperty("est").GetInt32() > 0);
    }

    [Theory]
    [Trait("Plan", "AC-hair-port-qa-3")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HairPort_without_races_ports_the_default_ticks(bool staleCrossSexPort)
    {
        using var mod = f.AddHairMod();
        if (staleCrossSexPort)
        {
            CrossSexFixtures.HandPort(mod, [101], CrossSexFixtures.Game(f.Game.GameFiles));
            mod.Put("long.mdl", CrossSexFixtures.Cap(201, radius: 0.101f));
        }
        var rows = HairPortPanel.ReadRows(mod.Root, Hair, null, CrossSexFixtures.Game(f.Game.GameFiles)).Rows;
        var job = await Job(Port());
        var result = Result(job);
        Assert.Equal(HairPortPanel.DefaultTicks(rows).Select(HairPortJob.RaceCode), Codes2(result.GetProperty("asked")));
        var ported = Codes(result.GetProperty("ported"));
        Assert.Equal(staleCrossSexPort, ported.Contains("c0101"));
        Assert.DoesNotContain(ported, r => r is "c0301" or "c0701" or "c1101" or "c1301");
    }

    private static List<string> Codes2(JsonElement array) => array.EnumerateArray().Select(e => e.GetString()!).ToList();

    [Fact]
    [Trait("Plan", "AC-hair-port-qa-4")]
    [Trait("Plan", "EDGE-hair-port-qa-6")]
    [Trait("Plan", "EDGE-hair-port-qa-8")]
    [Trait("Plan", "EDGE-hair-port-qa-9")]
    public async Task HairPort_skips_what_the_Studio_cannot_tick()
    {
        using var mod = f.AddHairMod();
        var game = CrossSexFixtures.Game(f.Game.GameFiles);
        CrossSexFixtures.HandPort(mod, [401, 701], game);
        CrossSexFixtures.AddFile(mod, "Long", HairPortPlan.HairPath(801, 5), "miqo.mdl", CrossSexFixtures.Cap(801));
        f.Game.GameFiles.Remove(HairPortPlan.HairPath(1201, 5));
        var before = FolderState.Of(mod.Root);

        // The source race, a covered race, a current port, no such hairstyle, and an other-sex row the mod has no
        // own version against but that is ported and current.
        var job = await Job(Port("c0201", "c0801", "c0401", "c1201", "c0701"));
        var result = Result(job);
        Assert.Equal("nothing-to-port", result.GetProperty("summary").GetString());
        var skipped = result.GetProperty("skippedRaces").EnumerateArray()
                            .ToDictionary(s => s.GetProperty("race").GetString()!, s => s.GetProperty("reason").GetString());
        Assert.Equal("not-listed", skipped["c0201"]);
        Assert.Equal("covered", skipped["c0801"]);
        Assert.Equal("ported", skipped["c0401"]);
        Assert.Equal("no-style", skipped["c1201"]);
        Assert.Equal("ported", skipped["c0701"]);
        Assert.Equal(JsonValueKind.Null, job.GetProperty("backup").ValueKind);
        Assert.Equal(0, f.Game.AfterChanges);
        FolderState.AssertSame(before, mod.Root);

        // A mod with its own men's model: every other man is its own version for that sex.
        CrossSexFixtures.AddFile(mod, "Long", HairPortPlan.HairPath(301, 5), "men.mdl", CrossSexFixtures.Cap(301));
        var own = Result(await Job(Port("c0101", "c1101")));
        var reasons = own.GetProperty("skippedRaces").EnumerateArray()
                         .ToDictionary(s => s.GetProperty("race").GetString()!, s => s.GetProperty("reason").GetString());
        Assert.Equal("other-sex-authored", reasons["c0101"]);
        Assert.Equal("other-sex-authored", reasons["c1101"]);
    }

    [Fact]
    [Trait("Plan", "EDGE-hair-port-qa-8")]
    public async Task HairPort_skips_own_version_and_own_skeleton_rows()
    {
        using var mod = f.AddHairMod(physics: true);
        var game = CrossSexFixtures.Game(f.Game.GameFiles);
        CrossSexFixtures.HandPort(mod, [101, 1101], game);
        CrossSexFixtures.AddEst(mod, "Long", "1101", 5, 77);           // own skeleton for the Lalafell man
        mod.Put("long.mdl", CrossSexFixtures.Physics(201, radius: 0.101f));
        var view = await View();
        Assert.True(Row(view, "c1101").GetProperty("ownSkeleton").GetBoolean());
        var reasons = Result(await Job(Port("c1101"))).GetProperty("skippedRaces").EnumerateArray()
                      .ToDictionary(s => s.GetProperty("race").GetString()!, s => s.GetProperty("reason").GetString());
        Assert.Equal("own-skeleton", reasons["c1101"]);

        CrossSexFixtures.AddFile(mod, "Long", HairPortPlan.HairPath(301, 5), "men.mdl", CrossSexFixtures.Physics(301));
        reasons = Result(await Job(Port("c0101"))).GetProperty("skippedRaces").EnumerateArray()
                  .ToDictionary(s => s.GetProperty("race").GetString()!, s => s.GetProperty("reason").GetString());
        Assert.Equal("own-version", reasons["c0101"]);
    }

    // ── Removing and restoring ────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-hair-port-qa-5")]
    [Trait("Plan", "EDGE-hair-port-qa-15")]
    public async Task HairRemove_takes_the_ports_out_after_a_backup()
    {
        using var mod = f.AddHairMod();
        await Job(Port("c0401", "c0101"));
        var job = await Job($"\"mod\":\"Hair\",\"hair\":\"{Hair}\",\"remove\":true");
        Assert.Equal(("hair-remove", "done"), (job.GetProperty("kind").GetString(), job.GetProperty("state").GetString()));
        Assert.Equal(2, Result(job).GetProperty("removed").GetInt32());
        Assert.NotEqual(JsonValueKind.Null, job.GetProperty("backup").ValueKind);
        var view = await View();
        Assert.Equal("open", Row(view, "c0101").GetProperty("state").GetString());
        Assert.Equal("open", Row(view, "c0401").GetProperty("state").GetString());

        var again = await Job($"\"mod\":\"Hair\",\"hair\":\"{Hair}\",\"remove\":true");
        Assert.Equal("nothing-to-remove", Result(again).GetProperty("summary").GetString());
        Assert.Equal(JsonValueKind.Null, again.GetProperty("backup").ValueKind);
    }

    [Theory]
    [Trait("Plan", "AC-hair-port-qa-6")]
    [InlineData("port-by-id")]
    [InlineData("remove-by-id")]
    [InlineData("by-mod")]
    public async Task Restore_puts_hair_jobs_back(string how)
    {
        using var mod = f.AddHairMod();
        var fresh = FolderState.Of(mod.Root);
        var port = await Job(Port("c0101"));
        var afterPort = FolderState.Of(mod.Root);
        string restoreBody = how switch
        {
            "port-by-id" => $"{{\"job\":\"{port.GetProperty("id").GetString()}\"}}",
            "remove-by-id" => $"{{\"job\":\"{(await Job($"\"mod\":\"Hair\",\"hair\":\"{Hair}\",\"remove\":true")).GetProperty("id").GetString()}\"}}",
            _ => "{\"mod\":\"Hair\"}",
        };
        var (status, body) = await f.Post("/actions/restore", restoreBody);
        Assert.True(status == 202, body.ToString());
        var restored = await f.WaitJob(body.GetProperty("job").GetString()!);
        Assert.True(restored.GetProperty("state").GetString() == "done", restored.ToString());
        FolderState.AssertSame(how == "remove-by-id" ? afterPort : fresh, mod.Root);
        if (how == "by-mod")
        {
            var backup = f.Backups.Find("Hair", port.GetProperty("backup").GetProperty("id").GetString()!).Backup!;
            Assert.Equal(BackupKind.HairPort, backup.Meta.Kind);
            Assert.Contains("\"hair-port\"", File.ReadAllText(Path.Combine(backup.Path, ModBackup.MetaFile)));
            Assert.Equal(backup.Id, restored.GetProperty("restored").GetProperty("from").GetProperty("id").GetString());
        }
    }

    [Fact]
    [Trait("Plan", "EDGE-hair-port-qa-22")]
    public async Task Restore_by_mod_orders_refit_and_hair_port_backups()
    {
        using var mod = f.AddHairMod();
        var (_, refit) = await f.Post("/actions/refit", """{"mod":"Hair","bodies":["rue"]}""");
        var refitJob = await f.WaitJob(refit.GetProperty("job").GetString()!);
        var hairJob = await Job(Port("c0101"));
        var (_, body) = await f.Post("/actions/restore", "{\"mod\":\"Hair\"}");
        var restored = await f.WaitJob(body.GetProperty("job").GetString()!);
        var info = restored.GetProperty("restored");
        Assert.Equal(refitJob.GetProperty("backup").GetProperty("id").GetString(), info.GetProperty("from").GetProperty("id").GetString());
        Assert.Contains(hairJob.GetProperty("backup").GetProperty("id").GetString(),
                        info.GetProperty("skippedNewer").EnumerateArray().Select(e => e.GetString()));
    }

    // ── /state ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-hair-port-qa-7")]
    [Trait("Plan", "EDGE-hair-port-qa-24")]
    public async Task State_reports_the_hair_port_block()
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string modRoot = Path.Combine(profile, "mods", "Hair");
        f.Game.HairPortView = new HairPortInfo(
            new HairPortWatcher.View(Worn: CrossSexFixtures.Target, ModRoot: modRoot, Ported: true, PortsInMod: 2, Busy: false,
                                     Warning: $"see {modRoot}"),
            new HairPortWatcher.ScanView(Running: true, Done: 1, Total: 3, Race: 101,
                Rows: [new HairPortScan.Row(5, modRoot, "Hair", "Hair (name)", CrossSexFixtures.Target, HairPortScan.RowState.AlreadyFitted),
                       new HairPortScan.Row(6, modRoot, "Hair", "Hair (name)", HairPortPlan.HairPath(101, 6), HairPortScan.RowState.Refused, "no")]),
            CheckOwed: true, AutoFit: true, Earless: false);
        var (_, state) = await f.Get("/state");
        var h = state.GetProperty("hairPort");
        Assert.True(h.GetProperty("busy").GetBoolean());
        Assert.True(h.GetProperty("autoFit").GetBoolean());
        var worn = h.GetProperty("worn");
        Assert.Equal(("Hair", true, 2), (worn.GetProperty("modDir").GetString(), worn.GetProperty("ported").GetBoolean(),
                                         worn.GetProperty("portsInMod").GetInt32()));
        var check = h.GetProperty("check");
        Assert.Equal((true, true, "c0101"), (check.GetProperty("running").GetBoolean(), check.GetProperty("owed").GetBoolean(),
                                             check.GetProperty("race").GetString()));
        Assert.Equal(["already-fitted", "refused"], check.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("state").GetString()));
        string text = h.ToString();
        Assert.DoesNotContain("modRoot", text);
        Assert.DoesNotContain(profile, text, StringComparison.OrdinalIgnoreCase);
    }

    // ── Refusals ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-hair-port-qa-8")]
    [Trait("Plan", "EDGE-hair-port-qa-1")]
    [Trait("Plan", "EDGE-hair-port-qa-5")]
    [Trait("Plan", "EDGE-hair-port-qa-7")]
    [Trait("Plan", "EDGE-hair-port-qa-26")]
    public async Task Hair_routes_validate_before_anything_is_written()
    {
        using var mod = f.AddHairMod();
        var before = FolderState.Of(mod.Root);
        var r = await f.Client.PostAsync("/actions/hair-port", new System.Net.Http.StringContent("{}"));
        Assert.Equal(415, (int)r.StatusCode);
        Assert.Equal(400, (await f.Post("/actions/hair-port", "{nope")).Status);
        string[] bad =
        [
            "\"hair\":\"" + Hair + "\"",                                          // no mod
            "\"mod\":\"Hair\"",                                                    // no hair
            "\"mod\":\"Hair\",\"hair\":\"chara/equipment/e0100/model/c0201e0100_top.mdl\"",
            "\"mod\":\"Hair\",\"hair\":\"chara/human/c0201/obj/hair/h0005/model/c0101h0005_hir.mdl\"",
            "\"mod\":\"Hair\",\"hair\":\"\"", "\"mod\":\"Hair\",\"hair\":5",
            Port() + ",\"races\":[]", Port() + ",\"races\":[101]", Port() + ",\"races\":[\"0101\"]", Port() + ",\"races\":[\"c0104\"]",
            Port("c0101") + ",\"remove\":true", Port() + ",\"remove\":false", Port() + ",\"remove\":\"yes\"", Port() + ",\"force\":true",
        ];
        foreach (var fields in bad)
        {
            var (status, body) = await PostHair(fields);
            Assert.True(status == 400 && body.GetProperty("error").GetProperty("code").GetString() == "invalid-field", fields + " → " + body);
        }
        Assert.Equal(405, (int)(await f.Client.GetAsync("/actions/hair-port")).StatusCode);
        Assert.Equal(405, (int)(await f.Client.PostAsync("/hair-port", HarnessFixture.Json("{}"))).StatusCode);
        foreach (var q in new[] { "?mod=Hair", $"?hair={Hair}", "?mod=Hair&hair=chara/equipment/e0100/model/c0201e0100_top.mdl" })
            Assert.Equal(400, (await f.Get("/hair-port" + q)).Status);

        var (races, _) = HarnessRouter.ParseRaces(JsonDocument.Parse("""["C0101","c0101","c0401"]""").RootElement);
        Assert.Equal([101, 401], races!.Select(x => (int)x));
        Assert.True(f.Jobs.TryReserve());
        f.Jobs.Release();
        Assert.True(NoBackups());
        FolderState.AssertSame(before, mod.Root);
    }

    [Theory]
    [Trait("Plan", "AC-hair-port-qa-8")]
    [Trait("Plan", "EDGE-hair-port-qa-16")]
    [InlineData("worn")]
    [InlineData("running")]
    [InlineData("owed")]
    public async Task Hair_port_refuses_while_the_hair_watcher_is_busy_or_owes_a_check(string what)
    {
        using var mod = f.AddHairMod();
        f.Game.HairPortView = new HairPortInfo(new HairPortWatcher.View(Busy: what == "worn"),
                                               new HairPortWatcher.ScanView(Running: what == "running"), what == "owed", true, false);
        var (status, body) = await PostHair(Port("c0101"));
        Assert.Equal((409, "hair-port-busy"), (status, body.GetProperty("error").GetProperty("code").GetString()));
        Assert.True(f.Jobs.TryReserve());
        f.Jobs.Release();
        Assert.True(NoBackups());

        f.Game.HairPortView = new HairPortInfo(new HairPortWatcher.View(), new HairPortWatcher.ScanView(), false, true, false);
        f.Game.View = new AutoRefitWatcher.View(Busy: true);
        Assert.Equal(409, (await PostHair(Port("c0101"))).Status);
        f.Game.View = new AutoRefitWatcher.View();
        Assert.True(f.Jobs.TryReserve());
        Assert.Equal("busy", (await PostHair(Port("c0101"))).Body.GetProperty("error").GetProperty("code").GetString());
        f.Jobs.Release();
    }

    [Fact]
    [Trait("Plan", "AC-hair-port-qa-8")]
    [Trait("Plan", "AC-hair-port-qa-6")]
    [Trait("Plan", "EDGE-hair-port-qa-16")]
    [Trait("Plan", "EDGE-hair-port-qa-23")]
    public async Task Refit_and_restore_wait_only_for_a_working_watcher_on_a_hair_mod()
    {
        f.AddMod();
        using var mod = f.AddHairMod();
        var outfitRefit = await f.WaitJob((await f.Post("/actions/refit", """{"mod":"Outfit","bodies":["rue"]}""")).Body.GetProperty("job").GetString()!);
        var hairRefit = await f.WaitJob((await f.Post("/actions/refit", """{"mod":"Hair","bodies":["rue"]}""")).Body.GetProperty("job").GetString()!);
        Assert.Equal("done", hairRefit.GetProperty("state").GetString());

        async Task<int> Restore(string modDir)
        {
            var (status, body) = await f.Post("/actions/restore", $"{{\"mod\":\"{modDir}\"}}");
            if (status == 202) await f.WaitJob(body.GetProperty("job").GetString()!);
            return status;
        }
        void Watcher(bool worn, bool running, bool owed)
            => f.Game.HairPortView = new HairPortInfo(new HairPortWatcher.View(Busy: worn), new HairPortWatcher.ScanView(Running: running),
                                                      owed, true, false);

        Watcher(false, false, true);
        Assert.Equal(202, await Restore("Outfit"));
        Watcher(false, true, true);
        Assert.Equal(202, await Restore("Outfit"));
        Assert.Equal(202, (await f.Post("/actions/refit", """{"mod":"Outfit","bodies":["rue"]}""")).Status);
        await Task.Delay(300);
        while (f.Jobs.Current is { Ended: false }) await Task.Delay(20);

        // A hair mod: an owed check never blocks a restore; a check under way does.
        Watcher(false, false, true);
        Assert.Equal(202, await Restore("Hair"));
        var before = FolderState.Of(mod.Root);
        Watcher(false, true, false);
        var (status, body) = await f.Post("/actions/restore", "{\"mod\":\"Hair\"}");
        Assert.Equal((409, "hair-port-busy"), (status, body.GetProperty("error").GetProperty("code").GetString()));
        Assert.Equal(409, (await f.Post("/actions/refit", """{"mod":"Hair","bodies":["rue"]}""")).Status);
        Watcher(true, false, false);
        Assert.Equal(409, (await f.Post("/actions/restore", "{\"mod\":\"Hair\"}")).Status);
        FolderState.AssertSame(before, mod.Root);
        Assert.True(f.Jobs.TryReserve());
        f.Jobs.Release();

        // A hair mod known by its record alone.
        File.WriteAllText(Path.Combine(f.ModsRoot, "Outfit", "meta.json"), "{\"FileVersion\":3}");
        Directory.CreateDirectory(Path.Combine(f.ModsRoot, "Outfit", "Proteus"));
        File.WriteAllText(Path.Combine(f.ModsRoot, "Outfit", "Proteus", HairPortRecord.RecordFile), "{\"Ports\":[]}");
        Watcher(false, true, false);
        Assert.Equal(409, (await f.Post("/actions/restore", "{\"mod\":\"Outfit\"}")).Status);
        Assert.Contains("IsHairMod@worker", f.Game.Order);
        Assert.DoesNotContain("IsHairMod@framework", f.Game.Order);
        _ = outfitRefit;
    }

    [Fact]
    [Trait("Plan", "AC-hair-port-qa-8")]
    [Trait("Plan", "EDGE-hair-port-qa-17")]
    [Trait("Plan", "EDGE-hair-port-qa-25")]
    public async Task Hair_routes_resolve_the_folder_like_the_actions()
    {
        using var mod = f.AddHairMod();
        Assert.Equal(404, (await f.Get($"/hair-port?mod={Uri.EscapeDataString("Hair (display)")}&hair={Hair}")).Status);
        Assert.Equal(404, (await PostHair($"\"mod\":\"Hair (display)\",\"hair\":\"{Hair}\"")).Status);
        Assert.Equal("Hair", (await View(mod: "HAIR")).GetProperty("modDir").GetString());
        var (status, body) = await PostHair($"\"mod\":\"HAIR\",\"hair\":\"{Hair}\",\"races\":[\"c0101\"]");
        Assert.Equal((202, "Hair"), (status, body.GetProperty("modDir").GetString()));
        await f.WaitJob(body.GetProperty("job").GetString()!);
        foreach (var unsafeName in new[] { "..\\x", "../x", "..", "Hair/../Hair" })
        {
            Assert.Contains((await f.Get($"/hair-port?mod={Uri.EscapeDataString(unsafeName)}&hair={Hair}")).Status, new[] { 400, 404 });
            Assert.Contains((await PostHair($"\"mod\":{JsonSerializer.Serialize(unsafeName)},\"hair\":\"{Hair}\"")).Status, new[] { 400, 404 });
        }
        f.Game.ModFolders[f.Game.ManagedModDir] = "Proteus";
        Directory.CreateDirectory(Path.Combine(f.ModsRoot, f.Game.ManagedModDir));
        Assert.Equal(400, (await f.Get($"/hair-port?mod={f.Game.ManagedModDir}&hair={Hair}")).Status);
        Assert.Equal(400, (await PostHair($"\"mod\":\"{f.Game.ManagedModDir}\",\"hair\":\"{Hair}\"")).Status);

        using var spaced = CrossSexFixtures.WomensMod(root: Path.Combine(f.ModsRoot, "Long Hair ✿"));
        f.Game.ModFolders["Long Hair ✿"] = "x";
        Assert.Equal("Long Hair ✿", (await View(mod: "Long Hair ✿")).GetProperty("modDir").GetString());

        f.Game.PenumbraUp = false;
        Assert.Equal(503, (await f.Get($"/hair-port?mod=Hair&hair={Hair}")).Status);
        Assert.Equal(503, (await PostHair(Port("c0101"))).Status);
    }

    [Theory]
    [Trait("Plan", "AC-hair-port-qa-8")]
    [Trait("Plan", "EDGE-hair-port-qa-4")]
    [Trait("Plan", "EDGE-hair-port-qa-18")]
    [Trait("Plan", "EDGE-hair-port-qa-19")]
    [Trait("Plan", "EDGE-hair-port-qa-20")]
    [InlineData("not-in-mod")]
    [InlineData("legacy")]
    [InlineData("manifest")]
    [InlineData("record")]
    [InlineData("npc")]
    public async Task Hair_jobs_refuse_unusable_mods_before_writing(string what)
    {
        using var mod = f.AddHairMod();
        string hair = Hair;
        switch (what)
        {
            case "not-in-mod": hair = HairPortPlan.HairPath(201, 9); break;
            case "legacy": f.Game.Legacy.Add("Hair"); break;
            case "manifest": File.WriteAllText(Path.Combine(mod.Root, "meta.json"), "{ not json"); break;
            case "record": mod.Put("Proteus/" + HairPortRecord.RecordFile, [0x7b, 0x7b]); break;
            case "npc":
                hair = HairPortPlan.HairPath(104, 5);
                CrossSexFixtures.AddFile(mod, "Long", hair, "npc.mdl", CrossSexFixtures.Cap(104));
                break;
        }
        var before = FolderState.Of(mod.Root);
        var (getStatus, view) = await f.Get($"/hair-port?mod=Hair&hair={Uri.EscapeDataString(hair)}");
        var job = await Job($"\"mod\":\"Hair\",\"hair\":\"{hair}\"");
        Assert.Equal(JsonValueKind.Null, job.GetProperty("backup").ValueKind);
        FolderState.AssertSame(before, mod.Root);
        string? code = job.GetProperty("error").ValueKind == JsonValueKind.Null ? null : job.GetProperty("error").GetProperty("code").GetString();
        switch (what)
        {
            case "not-in-mod": Assert.Equal((404, "hair-not-in-mod"), (getStatus, code)); break;
            case "legacy": Assert.Equal((409, "legacy-mod"), (getStatus, code)); break;
            case "manifest" or "record": Assert.Equal((409, "mod-unreadable"), (getStatus, code)); break;
            case "npc":
                Assert.Equal(200, getStatus);
                Assert.Empty(view.GetProperty("rows").EnumerateArray());
                Assert.Equal("nothing-to-port", Result(job).GetProperty("summary").GetString());
                break;
        }
    }

    [Theory]
    [Trait("Plan", "EDGE-hair-port-qa-10")]
    [Trait("Plan", "EDGE-hair-port-qa-12")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Nothing_to_write_backs_nothing_up(bool noPbd)
    {
        using var mod = f.AddHairMod();
        string hair = Hair;
        if (noPbd) f.Game.GameFiles.Remove(Proteus.Interop.LiveCharacter.PbdGamePath);
        else
        {
            // Hairstyle 130 to one man alone: the men's material is every man's, so the Studio refuses it.
            hair = HairPortPlan.HairPath(201, 130);
            CrossSexFixtures.AddFile(mod, "Long", hair, "h130.mdl", CrossSexFixtures.Cap(201, 130));
            foreach (var (k, v) in CrossSexFixtures.GameFiles(null, [130])) f.Game.GameFiles[k] = v;
        }
        var before = FolderState.Of(mod.Root);
        var job = await Job($"\"mod\":\"Hair\",\"hair\":\"{hair}\",\"races\":[\"c0101\"]");
        Assert.Equal(JsonValueKind.Null, job.GetProperty("backup").ValueKind);
        FolderState.AssertSame(before, mod.Root);
        if (noPbd)
        {
            Assert.Equal("run-failed", job.GetProperty("error").GetProperty("code").GetString());
            Assert.Equal(HairPortWatcher.NoPbd, job.GetProperty("error").GetProperty("message").GetString());
        }
        else
        {
            var result = Result(job);
            Assert.Equal("nothing-ported", result.GetProperty("summary").GetString());
            Assert.Contains("c1101", result.GetProperty("refused").EnumerateArray().Single().GetProperty("reason").GetString());
            Assert.Equal(0, f.Game.AfterChanges);
        }
    }

    [Fact]
    [Trait("Plan", "EDGE-hair-port-qa-11")]
    public async Task Strip_only_is_backed_up_and_saved()
    {
        using var mod = f.AddHairMod(physics: true);
        var game = CrossSexFixtures.Game(f.Game.GameFiles);
        // A carried port to the Lalafell man, whose slot the game since gives to another of his hairstyles, beside an
        // open race the build refuses: the refused race's old port is stripped.
        CrossSexFixtures.HandPort(mod, [1101], game);
        f.Game.GameFiles[HairSkeletonTable.GamePath] = HairPortFixtures.EstBytes(
            [(201, 5, 5), (101, 5, 5), (401, 5, 5), (301, 5, 5), (1101, 9, 5)]);
        var entry = HairPortRecord.Read(mod.Root).Ports.Single();
        Assert.True(entry.SkeletonFrom > 0);
        mod.Put("long.mdl", CrossSexFixtures.Physics(201, radius: 0.101f));
        // Bones the Lalafell man's carry cannot bring now: the build refuses him and strips the harmful carry.
        f.Game.GameFiles[HairPortPlan.SkeletonPath(201, 5)] = CrossSexFixtures.Skeleton("j_ex_h0005_ke_z");

        var job = await Job(Port("c1101"));
        var result = Result(job);
        Assert.True(job.GetProperty("state").GetString() == "done", job.ToString());
        Assert.Equal("nothing-ported", result.GetProperty("summary").GetString());
        Assert.Equal(["c1101"], Codes(result.GetProperty("stripped")));
        Assert.NotEqual(JsonValueKind.Null, job.GetProperty("backup").ValueKind);
        Assert.Equal(1, f.Game.AfterChanges);
    }

    [Fact]
    [Trait("Plan", "EDGE-hair-port-qa-13")]
    public async Task Cancelled_hair_port_writes_nothing()
    {
        using var mod = f.AddHairMod();
        var before = FolderState.Of(mod.Root);
        using var gate = new ManualResetEventSlim();
        f.Game.OnLegacyCheck = () => gate.Wait(5000);
        var (_, body) = await PostHair(Port("c0101"));
        await Task.Delay(100);
        f.Jobs.CancelRunning();
        gate.Set();
        var job = await f.WaitJob(body.GetProperty("job").GetString()!);
        Assert.Equal("cancelled", job.GetProperty("state").GetString());
        FolderState.AssertSame(before, mod.Root);
    }

    [Theory]
    [Trait("Plan", "EDGE-hair-port-qa-14")]
    [InlineData("save")]
    [InlineData("remove")]
    public async Task Writer_refusals_pass_through_unjudged(string what)
    {
        using var mod = f.AddHairMod();
        CrossSexFixtures.HandPort(mod, [101, 401], CrossSexFixtures.Game(f.Game.GameFiles));
        var file = HairPortRecord.Read(mod.Root).Ports.First(e => e.TargetGamePath.Contains("c0101")).File;
        mod.Put($"Proteus/{MeshVolumeService.BackupSubdir}/{file}", [1]);   // the user edited that port since
        if (what == "save")
        {
            // A port edited since cannot be remade: the Studio's refusal, passed on as it says it — nothing written.
            mod.Put("long.mdl", CrossSexFixtures.Cap(201, radius: 0.101f));
            var before = FolderState.Of(mod.Root);
            var job = await Job(Port("c0101"));
            var result = Result(job);
            Assert.Equal("nothing-ported", result.GetProperty("summary").GetString());
            Assert.Equal(ModelBackupOrder.BlockedMessage(Path.Combine(mod.Root, "Proteus", MeshVolumeService.BackupSubdir, file))
                             .Split(' ')[0],
                         result.GetProperty("refused").EnumerateArray().Single().GetProperty("reason").GetString()!.Split(' ')[0]);
            Assert.Equal(JsonValueKind.Null, job.GetProperty("backup").ValueKind);
            Assert.Equal(0, f.Game.AfterChanges);
            FolderState.AssertSame(before, mod.Root);
        }
        else
        {
            var job = await Job($"\"mod\":\"Hair\",\"hair\":\"{Hair}\",\"remove\":true");
            var result = Result(job);
            Assert.Equal(1, result.GetProperty("removed").GetInt32());
            Assert.Equal(["c0101"], Codes(result.GetProperty("remaining")));
            Assert.NotEqual("", result.GetProperty("message").GetString());
        }
    }

    [Fact]
    [Trait("Plan", "EDGE-hair-port-qa-21")]
    public async Task HairPort_over_the_cap_writes_nothing()
    {
        using var capped = new HarnessFixture(refitCap: 1);
        using var mod = capped.AddHairMod();
        var before = FolderState.Of(mod.Root);
        var (_, body) = await capped.Post("/actions/hair-port", "{" + Port("c0101") + "}");
        var job = await capped.WaitJob(body.GetProperty("job").GetString()!);
        Assert.Equal("backup-too-large", job.GetProperty("error").GetProperty("code").GetString());
        FolderState.AssertSame(before, mod.Root);
    }
}
