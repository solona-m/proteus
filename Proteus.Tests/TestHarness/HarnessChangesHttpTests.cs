using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using NSubstitute;
using Proteus.Interop;
using Proteus.Services;
using Proteus.TestHarness;
using Xunit;

namespace Proteus.Tests.TestHarness;

[Collection(HarnessTimingCollection.Name)]
public class HarnessChangesHttpTests : IDisposable
{
    private readonly HarnessFixture fx = new();
    private FakeGame Game => fx.Game;
    private Guid Coll => Game.Collection!.Value;

    public HarnessChangesHttpTests()
    {
        fx.AddMod("Equinox");
    }

    public void Dispose() => fx.Dispose();

    private static string Code(JsonElement body) => body.GetProperty("error").GetProperty("code").GetString()!;
    private static string Message(JsonElement body) => body.GetProperty("error").GetProperty("message").GetString()!;
    private int Count(string member) => Game.Calls.Count(c => c.Member == member);

    // [AC-harness-equip-1]
    [Fact]
    public async Task Equip_ByItem_WearsIt_ResponseAndStateAgree()
    {
        Game.Gear["Body"] = new Worn(111, 3, 0);
        var (status, body) = await fx.Post("/actions/equip", """{"slot":"body","item":12345}""");
        Assert.Equal(200, status);
        Assert.Equal("Body", body.GetProperty("slot").GetString());
        Assert.Equal(12345UL, body.GetProperty("sent").GetUInt64());
        Assert.Equal(12345UL, body.GetProperty("item").GetUInt64());
        Assert.Equal("[0,0]", body.GetProperty("dyes").GetRawText());
        Assert.Equal(111UL, body.GetProperty("previous").GetProperty("item").GetUInt64());
        Assert.Equal(111UL, body.GetProperty("original").GetProperty("item").GetUInt64());
        Assert.Equal(1, Count(nameof(IHarnessGame.SetGear)));
        Assert.All(Game.Calls.Where(c => c.Member == nameof(IHarnessGame.SetGear)), c => Assert.Equal(fx.Dispatcher.ThreadId, c.Thread));

        var (_, state) = await fx.Get("/state");
        var bodySlot = state.GetProperty("gear").EnumerateArray().Single(g => g.GetProperty("slot").GetString() == "Body");
        Assert.Equal(12345UL, bodySlot.GetProperty("item").GetUInt64());
        Assert.Single(state.GetProperty("harnessChanges").GetProperty("gear").EnumerateArray());
    }

    // [AC-harness-equip-1] [EDGE-harness-equip-3] [EDGE-harness-equip-4]
    [Fact]
    public async Task Equip_ItemInvalid_400_NoRecord()
    {
        Game.SetGearAnswer = (_, _) => "ItemInvalid";
        var (status, body) = await fx.Post("/actions/equip", """{"slot":"Body","item":99999}""");
        Assert.Equal((400, "item-invalid"), (status, Code(body)));
        Assert.True(fx.Dispatcher.Run(() => fx.Changes.Empty));
    }

    // [AC-harness-equip-2]
    [Fact]
    public async Task Equip_ByModel_SendsCustomId_ReportsWorn()
    {
        Game.Normalise = (_, _) => 40000;
        var (status, body) = await fx.Post("/actions/equip", """{"slot":"Body","set":317,"variant":2}""");
        Assert.Equal(200, status);
        Assert.Equal(283682589901117UL, Game.GearSets.Single().Id);
        Assert.Equal(283682589901117UL, body.GetProperty("sent").GetUInt64());
        Assert.Equal(40000UL, body.GetProperty("item").GetUInt64());
        Assert.Equal(317, body.GetProperty("set").GetInt32());
        Assert.Equal(2, body.GetProperty("variant").GetInt32());
    }

    // [AC-harness-equip-2] [EDGE-harness-equip-39]
    [Fact]
    public async Task Equip_ImcOnlyVariant_CustomIdWorn()
    {
        await fx.Post("/actions/equip", """{"slot":"Body","set":317,"variant":9}""");
        var (_, state) = await fx.Get("/state");
        var b = state.GetProperty("gear").EnumerateArray().Single(g => g.GetProperty("slot").GetString() == "Body");
        Assert.Equal(317, b.GetProperty("set").GetInt32());
        Assert.Equal(9, b.GetProperty("variant").GetInt32());
    }

    // [AC-harness-equip-4]
    [Fact]
    public async Task Hold_Enabled_HeldWithOptions_PermanentUntouched()
    {
        var options = new Dictionary<string, List<string>> { ["Colour"] = ["Red"] };
        Game.Settings["Equinox"] = new(PenumbraBridge.SettingsReadKind.Ok, new(true, 530, options, false, false));
        var (status, body) = await fx.Post("/actions/mod-settings", """{"mod":"equinox","enabled":false}""");
        Assert.Equal(200, status);
        Assert.Equal("Equinox", body.GetProperty("mod").GetString());
        Assert.True(body.GetProperty("before").GetProperty("enabled").GetBoolean());
        Assert.True(body.GetProperty("before").GetProperty("configured").GetBoolean());
        Assert.True(body.GetProperty("redrawRequested").GetBoolean());
        var held = Game.Temporary[(Coll, "Equinox")];
        Assert.Equal(HarnessChanges.HoldSource, held.Source);
        Assert.False(held.Enabled);
        Assert.Equal(HarnessChanges.HoldKey, Game.TemporaryKeys[(Coll, "Equinox")]);
        Assert.Equal(options, Game.HoldSets.Single().Options);
        // Only temporary-setting calls: the fake has no permanent writer, and none of its other members was called.
        Assert.DoesNotContain(Game.Calls, c => c.Member is nameof(IHarnessGame.AfterModChange));
    }

    // [AC-harness-equip-5]
    [Fact]
    public async Task Hold_HeldElsewhere_409_UntouchedByPutBack()
    {
        var glam = new PenumbraBridge.TemporaryHold(true, false, "Glamourer", true, 7);
        Game.Temporary[(Coll, "Equinox")] = glam;
        Game.TemporaryKeys[(Coll, "Equinox")] = 12;
        var (status, body) = await fx.Post("/actions/mod-settings", """{"mod":"Equinox","enabled":false}""");
        Assert.Equal((409, "held-elsewhere"), (status, Code(body)));
        await fx.Post("/actions/put-back", "{}");
        Assert.Equal(glam, Game.Temporary[(Coll, "Equinox")]);
    }

    // [AC-harness-equip-6]
    [Fact]
    public async Task PutBack_UndoesAll_ThenEmpty()
    {
        var gearBefore = new Dictionary<string, Worn>(Game.Gear);
        await fx.Post("/actions/equip", """{"slot":"Body","item":500}""");
        await fx.Post("/actions/equip", """{"slot":"RFinger","item":600}""");
        await fx.Post("/actions/mod-settings", """{"mod":"Equinox","priority":1000}""");
        var (status, body) = await fx.Post("/actions/put-back", "{}");
        Assert.Equal(200, status);
        Assert.Equal(["restored", "restored"], body.GetProperty("gear").EnumerateArray().Select(g => g.GetProperty("result").GetString()));
        Assert.Equal("cleared", body.GetProperty("holds")[0].GetProperty("result").GetString());
        Assert.Equal("ok", body.GetProperty("sweep").GetString());
        Assert.True(body.GetProperty("redrawRequested").GetBoolean());
        Assert.Equal(gearBefore, Game.Gear);
        Assert.Empty(Game.Temporary);
        var (_, again) = await fx.Post("/actions/put-back", "{}");
        Assert.Empty(again.GetProperty("gear").EnumerateArray());
        Assert.Empty(again.GetProperty("holds").EnumerateArray());
    }

    // [AC-harness-equip-8]
    [Fact]
    public async Task Changes_RefusedWhileBusy_PutBackNot()
    {
        await fx.Post("/actions/equip", """{"slot":"Body","item":500}""");
        Assert.True(fx.Jobs.TryReserve());
        var (s1, b1) = await fx.Post("/actions/equip", """{"slot":"Legs","item":500}""");
        Assert.Equal((409, "busy"), (s1, Code(b1)));
        var (s2, b2) = await fx.Post("/actions/mod-settings", """{"mod":"Equinox","enabled":false}""");
        Assert.Equal((409, "busy"), (s2, Code(b2)));
        var (p1, pb1) = await fx.Post("/actions/put-back", "{}");
        Assert.Equal(200, p1);
        Assert.Equal("restored", pb1.GetProperty("gear")[0].GetProperty("result").GetString());
        fx.Jobs.Release();

        await fx.Post("/actions/equip", """{"slot":"Body","item":500}""");
        Game.Activity = new AutoRefitWatcher.ActivitySnapshot(false, 1, false, false);
        var (s3, b3) = await fx.Post("/actions/equip", """{"slot":"Legs","item":500}""");
        Assert.Equal((409, "auto-refit-busy"), (s3, Code(b3)));
        var (p2, pb2) = await fx.Post("/actions/put-back", "{}");
        Assert.Equal(200, p2);
        Assert.Equal("restored", pb2.GetProperty("gear")[0].GetProperty("result").GetString());
    }

    // [AC-harness-equip-8] [EDGE-harness-equip-14]
    [Fact]
    public async Task Equip_CarrierSlot_409()
    {
        Game.Carriers.Add("RFinger");
        var (status, body) = await fx.Post("/actions/equip", """{"slot":"RFinger","item":500}""");
        Assert.Equal((409, "carrier-slot"), (status, Code(body)));
        Assert.Equal(0, Count(nameof(IHarnessGame.SetGear)));
        Assert.Equal(200, (await fx.Post("/actions/equip", """{"slot":"LFinger","item":500}""")).Status);
    }

    // [AC-harness-equip-9]
    [Fact]
    public async Task State_ReportsGearAndHarnessChanges()
    {
        await fx.Post("/actions/equip", """{"slot":"Body","set":317,"variant":2}""");
        await fx.Post("/actions/equip", """{"slot":"Legs","item":12345}""");
        await fx.Post("/actions/mod-settings", """{"mod":"Equinox","enabled":false}""");
        var (_, state) = await fx.Get("/state");
        var gear = state.GetProperty("gear").EnumerateArray().ToList();
        Assert.Equal(HarnessGear.Slots, gear.Select(g => g.GetProperty("slot").GetString()));
        Assert.Equal(317, gear[1].GetProperty("set").GetInt32());
        Assert.Equal(JsonValueKind.Null, gear[3].GetProperty("set").ValueKind);
        var hc = state.GetProperty("harnessChanges");
        Assert.False(hc.GetProperty("stopping").GetBoolean());
        var records = hc.GetProperty("gear").EnumerateArray().ToList();
        Assert.Equal(317, records[0].GetProperty("requested").GetProperty("set").GetInt32());
        Assert.Equal(2, records[0].GetProperty("requested").GetProperty("variant").GetInt32());
        Assert.True(records[0].GetProperty("verified").GetBoolean());
        Assert.Equal(JsonValueKind.Null, records[1].GetProperty("requested").ValueKind);
        Assert.False(hc.GetProperty("holds")[0].GetProperty("before").GetProperty("configured").GetBoolean());
    }

    // [AC-harness-equip-9]
    [Theory]
    [InlineData("/actions/equip")]
    [InlineData("/actions/mod-settings")]
    [InlineData("/actions/put-back")]
    public async Task Routes_Listed_405AndAllow(string path)
    {
        var r = await fx.Client.GetAsync(path);
        Assert.Equal(405, (int)r.StatusCode);
        Assert.Equal("POST", r.Content.Headers.Allow.Single());
    }

    // [AC-harness-equip-11] [EDGE-harness-equip-38]
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Hold_PriorityOnly_FailedRead_503_NothingWritten(bool throws)
    {
        if (throws) Game.ReadThrows = true;
        else Game.Settings["Equinox"] = new(PenumbraBridge.SettingsReadKind.Failed, null);
        var (status, body) = await fx.Post("/actions/mod-settings", """{"mod":"Equinox","priority":1000}""");
        Assert.Equal((503, "penumbra-unavailable"), (status, Code(body)));
        Assert.Empty(Game.HoldSets);
        Assert.True(fx.Dispatcher.Run(() => fx.Changes.Empty));
    }

    // [AC-harness-equip-12] [EDGE-harness-equip-43]
    [Fact]
    public async Task HeldElsewhere_SourceScrubbed()
    {
        fx.Names.Add("Hidden Person", NameKind.Character);
        string user = Environment.UserName;
        Game.Temporary[(Coll, "Equinox")] = new PenumbraBridge.TemporaryHold(true, false,
            $"Glamourer: Hidden Person outfit C:\\Users\\{user}\\x", true, 1);
        var (status, body) = await fx.Post("/actions/mod-settings", """{"mod":"Equinox","enabled":false}""");
        Assert.Equal(409, status);
        Assert.DoesNotContain("Hidden Person", Message(body));
        Assert.DoesNotContain($"\\Users\\{user}\\", Message(body));
        var logged = string.Join("\n", fx.Log.ReceivedCalls().SelectMany(c => c.GetArguments()).Select(a => a switch
        {
            object[] arr => string.Join(" ", arr),
            _ => a?.ToString(),
        }));
        Assert.DoesNotContain("Hidden Person", logged);
        Assert.DoesNotContain($"\\Users\\{user}\\", logged);
    }

    // [EDGE-harness-equip-1]
    [Theory]
    [InlineData("\"Glasses\"")]
    [InlineData("\"MainHand\"")]
    [InlineData("\"OffHand\"")]
    [InlineData("\"\"")]
    [InlineData("3")]
    public async Task Equip_BadSlot_400(string slot)
    {
        var (status, body) = await fx.Post("/actions/equip", $$"""{"slot":{{slot}},"item":500}""");
        Assert.Equal((400, "invalid-field"), (status, Code(body)));
        Assert.Empty(Game.Calls);
    }

    // [EDGE-harness-equip-2] [EDGE-harness-equip-47]
    [Theory]
    [InlineData("""{"slot":"Body","item":5,"set":1,"variant":1}""")]
    [InlineData("""{"slot":"Body","set":1}""")]
    [InlineData("""{"slot":"Body","variant":1}""")]
    [InlineData("""{"slot":"Body","item":-1}""")]
    [InlineData("""{"slot":"Body","item":1.5}""")]
    [InlineData("""{"slot":"Body","set":70000,"variant":1}""")]
    [InlineData("""{"slot":"Body","set":0,"variant":1}""")]
    [InlineData("""{"slot":"Body","set":1,"variant":256}""")]
    [InlineData("""{"slot":"Body","item":"5"}""")]
    [InlineData("""{"slot":"Body","item":4294967296}""")]
    [InlineData("""{"slot":"Body","item":0}""")]
    [InlineData("""{"slot":"Body"}""")]
    [InlineData("""{"slot":"Body","item":5,"restore":true}""")]
    public async Task Equip_BadBody_400(string json)
    {
        var (status, body) = await fx.Post("/actions/equip", json);
        Assert.Equal((400, "invalid-field"), (status, Code(body)));
        Assert.Equal(0, Count(nameof(IHarnessGame.SetGear)));
    }

    // [EDGE-harness-equip-50]
    [Theory]
    [InlineData(4294966783UL)]
    [InlineData(4294967164UL)]
    [InlineData(4294967295UL)]
    public async Task Equip_ByItem_GlamourerPseudoIdRange_400(ulong item)
    {
        var (status, body) = await fx.Post("/actions/equip", $$"""{"slot":"Body","item":{{item}}}""");
        Assert.Equal((400, "invalid-field"), (status, Code(body)));
        Assert.Empty(Game.Calls);
    }

    // [EDGE-harness-equip-50]
    [Fact]
    public async Task Equip_ByItem_LastRealId_Accepted()
    {
        Assert.Equal(200, (await fx.Post("/actions/equip", """{"slot":"Body","item":4294966782}""")).Status);
        Assert.Equal(4294966782UL, Game.GearSets.Single().Id);
    }

    // [EDGE-harness-equip-51]
    [Theory]
    [InlineData('a')]
    [InlineData('b')]
    [InlineData('c')]
    [InlineData('d')]
    public async Task Hold_CheckOrder_Pinned(char scenario)
    {
        switch (scenario)
        {
            case 'a': Game.QueryFails = true; break;
            case 'b':
                Game.Temporary[(Coll, "Equinox")] = new PenumbraBridge.TemporaryHold(true, false, "Glamourer", true, 1);
                Game.Settings["Equinox"] = new(PenumbraBridge.SettingsReadKind.Failed, null);
                break;
            case 'c': Game.Settings["Equinox"] = new(PenumbraBridge.SettingsReadKind.Failed, null); break;
            case 'd': Game.SetHoldAnswer = "InvalidArgument"; break;
        }
        var (status, body) = await fx.Post("/actions/mod-settings", """{"mod":"Equinox","enabled":false}""");
        var expected = scenario switch
        {
            'a' => (503, "penumbra-unavailable"),
            'b' => (409, "held-elsewhere"),
            'c' => (503, "penumbra-unavailable"),
            _ => (409, "penumbra-refused"),
        };
        Assert.Equal(expected, (status, Code(body)));
        string[] order = [nameof(IHarnessGame.QueryHold), nameof(IHarnessGame.ReadSettings), nameof(IHarnessGame.SetHold)];
        var seen = Game.Calls.Select(c => c.Member).Where(order.Contains).ToList();
        int reached = scenario switch { 'a' => 1, 'b' => 1, 'c' => 2, _ => 3 };
        Assert.Equal(order.Take(reached), seen);
        Assert.Empty(Game.HoldSets);
    }

    // [EDGE-harness-equip-12]
    [Fact]
    public async Task Equip_GlamourerRefuses_409_RecordKept()
    {
        Game.SetGearAnswer = (_, _) => "InvalidKey";
        var (status, body) = await fx.Post("/actions/equip", """{"slot":"Body","item":500}""");
        Assert.Equal((409, "glamourer-refused"), (status, Code(body)));
        Assert.Contains("InvalidKey", Message(body));
        Assert.True(fx.Dispatcher.Run(() => fx.Changes.Empty));

        Game.SetGearAnswer = (_, _) => null;
        await fx.Post("/actions/equip", """{"slot":"Body","item":500}""");
        Game.SetGearAnswer = (_, _) => "InvalidKey";
        await fx.Post("/actions/equip", """{"slot":"Body","item":600}""");
        var (_, state) = await fx.Get("/state");
        Assert.Equal(500UL, state.GetProperty("harnessChanges").GetProperty("gear")[0].GetProperty("applied").GetProperty("item").GetUInt64());
    }

    // [EDGE-harness-equip-13]
    [Fact]
    public async Task Equip_NoPlayer_409_GlamourerDown_503()
    {
        Game.LoggedIn = false;
        var (s1, b1) = await fx.Post("/actions/equip", """{"slot":"Body","item":500}""");
        Assert.Equal((409, "not-logged-in"), (s1, Code(b1)));
        Game.LoggedIn = true;
        Game.GlamourerUp = false;
        var (s2, b2) = await fx.Post("/actions/equip", """{"slot":"Body","item":500}""");
        Assert.Equal((503, "glamourer-unavailable"), (s2, Code(b2)));
        Assert.True(fx.Dispatcher.Run(() => fx.Changes.Empty));
    }

    // [EDGE-harness-equip-15]
    [Fact]
    public async Task Busy_ReservedNotStarted_Refused()
    {
        Assert.True(fx.Jobs.TryReserve());
        Assert.Null(fx.Jobs.Current);
        var (status, body) = await fx.Post("/actions/equip", """{"slot":"Body","item":500}""");
        Assert.Equal((409, "busy"), (status, Code(body)));
        fx.Jobs.Release();
    }

    // [EDGE-harness-equip-25]
    [Theory]
    [InlineData("missing")]
    [InlineData("managed")]
    [InlineData("penumbra")]
    [InlineData("collection")]
    [InlineData("unsafe")]
    [InlineData("refused")]
    public async Task ModSettings_Errors(string scenario)
    {
        string mod = "Equinox";
        (int, string) expected;
        switch (scenario)
        {
            case "missing": mod = "Nope"; expected = (404, "mod-not-found"); break;
            case "managed":
                mod = SidecarDiscoveryService.ManagedModDir;
                Game.ModFolders[mod] = mod;
                expected = (400, "managed-mod");
                break;
            case "penumbra": Game.PenumbraUp = false; expected = (503, "penumbra-unavailable"); break;
            case "collection": Game.Collection = null; expected = (409, "no-collection"); break;
            case "unsafe": mod = "..\\\\x"; expected = (400, "invalid-field"); break;
            default: Game.SetHoldAnswer = "InvalidArgument"; expected = (409, "penumbra-refused"); break;
        }
        var (status, body) = await fx.Post("/actions/mod-settings", $$"""{"mod":"{{mod}}","enabled":false}""");
        Assert.Equal(expected, (status, Code(body)));
        Assert.True(fx.Dispatcher.Run(() => fx.Changes.Empty));
    }

    // [EDGE-harness-equip-26]
    [Theory]
    [InlineData("""{"mod":"Equinox"}""")]
    [InlineData("""{"mod":"Equinox","enabled":"no"}""")]
    [InlineData("""{"mod":"Equinox","priority":2147483648}""")]
    [InlineData("""{"mod":"Equinox","priority":1.5}""")]
    [InlineData("""{"mod":"Equinox","enabled":true,"clear":true}""")]
    public async Task ModSettings_BadBody_400(string json)
    {
        var (status, body) = await fx.Post("/actions/mod-settings", json);
        Assert.Equal((400, "invalid-field"), (status, Code(body)));
        Assert.Empty(Game.Calls);
    }

    // [EDGE-harness-equip-34]
    [Fact]
    public async Task Equip_FrameworkTimeout_NeverRunsLater()
    {
        fx.Dispatcher.Stall();
        var (status, body) = await fx.Post("/actions/equip", """{"slot":"Body","item":500}""");
        Assert.Equal((503, "framework-timeout"), (status, Code(body)));
        fx.Dispatcher.Resume();
        await fx.Get("/health");   // drains the queue behind it
        Assert.Empty(Game.GearSets);
        Assert.True(fx.Dispatcher.Run(() => fx.Changes.Empty));
    }

    // [EDGE-harness-equip-35]
    [Fact]
    public async Task State_GlamourerUnavailable_GearNulls_ChangesListed()
    {
        await fx.Post("/actions/equip", """{"slot":"Body","item":500}""");
        Game.GlamourerUp = false;
        var (status, state) = await fx.Get("/state");
        Assert.Equal(200, status);
        Assert.All(state.GetProperty("gear").EnumerateArray(), g => Assert.Equal(JsonValueKind.Null, g.GetProperty("item").ValueKind));
        Assert.Single(state.GetProperty("harnessChanges").GetProperty("gear").EnumerateArray());
    }

    // [EDGE-harness-equip-31]
    [Fact]
    public async Task Stopping_EquipHold503_PutBackRuns()
    {
        await fx.Post("/actions/equip", """{"slot":"Body","item":500}""");
        fx.Changes.BeginStop();
        var (s1, b1) = await fx.Post("/actions/equip", """{"slot":"Legs","item":500}""");
        Assert.Equal((503, "harness-stopping"), (s1, Code(b1)));
        var (s2, b2) = await fx.Post("/actions/mod-settings", """{"mod":"Equinox","enabled":false}""");
        Assert.Equal((503, "harness-stopping"), (s2, Code(b2)));
        Assert.Empty(Game.HoldSets);
        var (s3, b3) = await fx.Post("/actions/put-back", "{}");
        Assert.Equal(200, s3);
        Assert.Equal("restored", b3.GetProperty("gear")[0].GetProperty("result").GetString());
    }
}
