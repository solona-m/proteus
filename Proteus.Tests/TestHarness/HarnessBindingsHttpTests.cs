using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Proteus.TestHarness;
using Xunit;

namespace Proteus.Tests.TestHarness;

/// <summary>The design-binding routes over HTTP: shapes, validation, the settle wait and its busy gate.</summary>
[Collection(HarnessTimingCollection.Name)]
public class HarnessBindingsHttpTests : IDisposable
{
    private readonly HarnessFixture fx = new();
    private FakeGame Game => fx.Game;
    private static readonly Guid Design = Guid.Parse("dddddddd-0000-0000-0000-000000000001");

    public HarnessBindingsHttpTests()
    {
        fx.AddMod("Panties");
        Game.Proteus.Add("Panties");
        Game.Permanent["Panties"] = new PermanentSetting(true, 2);
        Game.DesignNames[Design] = "Hidden Person's look";
        Game.Live[("Panties", "Lace", "High")] = [new ColorTableRowPreset { Row = 0, SubRowA = new ColorTableSubRowPreset { Diffuse = "#FFFFFF" } }];
        fx.Names.Add(Game.CharacterName, NameKind.Character);
    }

    public void Dispose() => fx.Dispose();

    private static string Code(JsonElement body) => body.GetProperty("error").GetProperty("code").GetString()!;

    private string ApplyBody(bool allow = false) => $$"""{"design":"{{Design}}"{{(allow ? ",\"allowAutoWriters\":true" : "")}}}""";

    [Fact]
    [Trait("Plan", "AC-binding-reapply-10")]
    [Trait("Plan", "EDGE-binding-reapply-35")]
    public async Task GetBindings_Shape_NamesOptIn_Scrubbed()
    {
        var (status, body) = await fx.Get("/bindings?mod=Panties&group=Lace&option=High");
        Assert.Equal(200, status);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("designs")[0].GetProperty("name").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("active").GetProperty("name").ValueKind);
        var mod = body.GetProperty("mod");
        Assert.True(mod.GetProperty("governed").GetBoolean());
        Assert.Equal("#FFFFFF", mod.GetProperty("live")[0].GetProperty("diffuse").GetString());
        Assert.True(body.TryGetProperty("storeSha256", out _));

        var (_, named) = await fx.Get("/bindings?names=1");
        var name = named.GetProperty("designs")[0].GetProperty("name").GetString()!;
        Assert.DoesNotContain(Game.CharacterName!, name);

        Assert.Equal(400, (await fx.Get("/bindings?mod=Panties&group=Lace")).Status);
        Assert.Equal(400, (await fx.Get("/bindings?names=yes")).Status);
        Assert.Equal(400, (await fx.Get("/bindings?design=nope")).Status);
        Assert.Equal(404, (await fx.Get("/bindings?mod=Missing")).Status);
        Assert.Equal(404, (await fx.Get($"/bindings?design={Guid.NewGuid()}")).Status);
    }

    [Fact]
    [Trait("Plan", "AC-binding-reapply-9")]
    [Trait("Plan", "EDGE-binding-reapply-44")]
    public async Task Apply_AnswerCarriesSettledAndEchoFields()
    {
        Game.EchoMs = 1500;
        var (status, body) = await fx.Post("/actions/glamourer-apply", ApplyBody());
        Assert.Equal(200, status);
        Assert.True(body.GetProperty("settled").GetBoolean());
        Assert.True(body.GetProperty("finalizedSeen").GetBoolean());
        Assert.True(body.GetProperty("echoWindowOpen").GetBoolean());
        Assert.Equal(1500, body.GetProperty("echoMsLeft").GetInt64());
    }

    [Fact]
    [Trait("Plan", "EDGE-binding-reapply-44")]
    public async Task GetBindings_CarriesEchoFields()
    {
        Game.EchoMs = 0;
        var (_, body) = await fx.Get("/bindings");
        Assert.False(body.GetProperty("echoWindowOpen").GetBoolean());
        Assert.Equal(0, body.GetProperty("echoMsLeft").GetInt64());
    }

    [Fact]
    [Trait("Plan", "AC-binding-reapply-9")]
    public async Task ApplyDesign_ReadsAfterOnlyWhenSettled()
    {
        int probes = 0;
        Game.IdleProbe = () => ++probes > 4;
        var (status, body) = await fx.Post("/actions/glamourer-apply", ApplyBody());
        Assert.Equal(200, status);
        Assert.True(body.GetProperty("settled").GetBoolean());
        var order = Game.Order.Select(o => o.Split('@')[0]).ToList();
        Assert.True(order.Count(o => o == nameof(IHarnessGame.CompositorIdle)) >= 5);
        Assert.True(order.LastIndexOf(nameof(IHarnessGame.CompositorIdle)) < order.LastIndexOf(nameof(IHarnessGame.GlamourerState)));
    }

    [Fact]
    [Trait("Plan", "EDGE-binding-reapply-39")]
    public async Task ApplyDesign_SettleTimeout_AnswersSettledFalse()
    {
        Game.Idle = false;
        var (status, body) = await fx.Post("/actions/glamourer-apply", ApplyBody());
        Assert.Equal(200, status);
        Assert.False(body.GetProperty("settled").GetBoolean());
        Assert.False(fx.Changes.ApplySettling);
    }

    [Theory]
    [Trait("Plan", "AC-binding-reapply-9")]
    [Trait("Plan", "EDGE-binding-reapply-39")]
    [InlineData("refit")]
    [InlineData("shader")]
    [InlineData("variants")]
    [InlineData("hair")]
    public async Task ApplyDesign_SettleWaitsForAllFourAutoWriters(string which)
    {
        // The writer wakes on the apply and never goes quiet: the settle times out on it.
        Game.ApplyEffects[Design] = () =>
        {
            if (which == "refit") Game.View = Game.View with { Busy = true };
            if (which == "shader") Game.ShaderUpgradeView = Game.ShaderUpgradeView with { Busy = true };
            if (which == "variants") Game.VariantRepointView = Game.VariantRepointView with { Busy = true };
            if (which == "hair") Game.HairPortView = Game.HairPortView with { CheckOwed = true };
        };
        var (_, body) = await fx.Post("/actions/glamourer-apply", ApplyBody());
        Assert.False(body.GetProperty("settled").GetBoolean());
    }

    [Fact]
    [Trait("Plan", "EDGE-binding-reapply-46")]
    public async Task Settling_FlagBlocksChangeRoutesAndPutBack()
    {
        Game.Idle = false;   // the apply waits out its settle (FastTimings.RedrawWait)
        var apply = fx.Post("/actions/glamourer-apply", ApplyBody());
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!fx.Changes.ApplySettling && clock.ElapsedMilliseconds < 2000) await Task.Delay(5);
        Assert.True(fx.Changes.ApplySettling);

        var equip = await fx.Post("/actions/equip", """{"slot":"Body","item":500}""");
        var hold = await fx.Post("/actions/mod-settings", """{"mod":"Panties","enabled":false}""");
        var edit = await fx.Post("/actions/binding-edit", """{"mod":"Panties","group":"Lace","option":"High","clear":true}""");
        var again = await fx.Post("/actions/glamourer-apply", ApplyBody());
        var putBack = await fx.Post("/actions/put-back", "{}");
        foreach (var (status, body) in new[] { equip, hold, edit, again, putBack })
        {
            Assert.Equal(409, status);
            Assert.Equal("busy", Code(body));
        }
        Assert.Equal(200, (await apply).Status);
        Assert.False(fx.Changes.ApplySettling);
    }

    [Fact]
    [Trait("Plan", "AC-binding-reapply-13")]
    public async Task NewRoutes_ValidateBodies()
    {
        async Task Bad(string path, string json) => Assert.Equal(400, (await fx.Post(path, json)).Status);
        await Bad("/actions/glamourer-apply", """{"design":"nope"}""");
        await Bad("/actions/glamourer-apply", """{"design":"dddddddd-0000-0000-0000-000000000001","extra":1}""");
        await Bad("/actions/glamourer-apply", """{"design":"dddddddd-0000-0000-0000-000000000001","allowAutoWriters":"yes"}""");
        await Bad("/actions/glamourer-apply", """{"design":"a","design":"b"}""");
        await Bad("/actions/binding-edit", """{"mod":"Panties","row":0,"diffuse":"red"}""");
        await Bad("/actions/binding-edit", """{"mod":"Panties","row":0,"diffuse":"#FF0000","clear":true}""");
        await Bad("/actions/binding-edit", """{"mod":"Panties"}""");
        await Bad("/actions/binding-edit", """{"mod":"Panties","group":"Lace","clear":true}""");
        await Bad("/actions/binding-edit", """{"mod":"Panties","row":0}""");
        await Bad("/actions/binding-edit", """{"mod":"Panties","clear":false}""");
        await Bad("/actions/mod-settings", """{"mod":"Panties","enabled":false,"permanent":"yes"}""");
    }

    [Fact]
    [Trait("Plan", "AC-binding-reapply-13")]
    public async Task NewRoutes_RefusedWhileBusyOrStopping()
    {
        Game.View = Game.View with { Busy = true };
        Assert.Equal("auto-refit-busy", Code((await fx.Post("/actions/glamourer-apply", ApplyBody())).Body));
        Assert.Equal("auto-refit-busy", Code((await fx.Post("/actions/binding-edit", """{"mod":"Panties","clear":true}""")).Body));
        Game.View = Game.View with { Busy = false };
        fx.Changes.BeginStop();
        var (status, body) = await fx.Post("/actions/glamourer-apply", ApplyBody());
        Assert.Equal((503, "harness-stopping"), (status, Code(body)));
        Assert.Equal("harness-stopping", Code((await fx.Post("/actions/mod-settings", """{"mod":"Panties","enabled":false,"permanent":true}""")).Body));
    }

    [Fact]
    [Trait("Plan", "AC-binding-reapply-9")]
    [Trait("Plan", "AC-binding-reapply-11")]
    [Trait("Plan", "AC-binding-reapply-12")]
    public async Task PutBack_AnswerCarriesBindingBlock()
    {
        var (s1, perm) = await fx.Post("/actions/mod-settings", """{"mod":"Panties","enabled":false,"permanent":true}""");
        Assert.Equal(200, s1);
        Assert.True(perm.GetProperty("permanent").GetBoolean());
        Assert.True(perm.GetProperty("before").GetProperty("enabled").GetBoolean());
        Assert.Equal(200, (await fx.Post("/actions/binding-edit", """{"mod":"Panties","group":"Lace","option":"High","row":0,"diffuse":"#ff00ff"}""")).Status);
        Assert.Equal(200, (await fx.Post("/actions/glamourer-apply", ApplyBody())).Status);

        var (_, state) = await fx.Get("/state");
        Assert.Equal(JsonValueKind.Object, state.GetProperty("harnessChanges").GetProperty("binding").ValueKind);

        var (status, body) = await fx.Post("/actions/put-back", "{}");
        Assert.Equal(200, status);
        var b = body.GetProperty("binding");
        Assert.Equal("restored", b.GetProperty("glamourer").GetString());
        Assert.Equal("restored", b.GetProperty("live").GetString());
        Assert.Equal("restored", b.GetProperty("permanent")[0].GetProperty("result").GetString());
        Assert.EndsWith("binding.json", b.GetProperty("record").GetString());
        Assert.Equal(new PermanentSetting(true, 2), Game.Permanent["Panties"]);

        var (_, after) = await fx.Get("/state");
        Assert.Equal(JsonValueKind.Null, after.GetProperty("harnessChanges").GetProperty("binding").ValueKind);
    }

    [Fact]
    [Trait("Plan", "EDGE-binding-reapply-33")]
    public async Task Permanent_NonProteusMod_RefusedOverHttp()
    {
        fx.AddMod("Other");
        var (status, body) = await fx.Post("/actions/mod-settings", """{"mod":"Other","enabled":false,"permanent":true}""");
        Assert.Equal((409, "not-a-proteus-mod"), (status, Code(body)));
    }
}
