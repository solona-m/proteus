using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Proteus.Services;
using Proteus.TestHarness;
using Xunit;

namespace Proteus.Tests.TestHarness;

/// <summary>The binding half of the harness's session changes, headless against <see cref="FakeGame"/>.</summary>
[Collection(HarnessTimingCollection.Name)]
public class HarnessBindingsTests : IDisposable
{
    private readonly HarnessFixture fx = new();
    private FakeGame Game => fx.Game;
    private HarnessChanges C => fx.Changes;
    private HarnessBindings B => fx.Changes.Bindings;
    private Guid Coll => Game.Collection!.Value;

    private static readonly Guid Design = Guid.Parse("dddddddd-0000-0000-0000-000000000001");
    private static readonly Guid Other  = Guid.Parse("dddddddd-0000-0000-0000-000000000002");

    private string RecordPath => Path.Combine(fx.Temp.Path, "config", "test-harness", "backups", "session-a", "binding.json");

    public HarnessBindingsTests()
    {
        Game.DesignNames[Design] = "Bound Look";
        Game.DesignNames[Other] = "Other Look";
        Game.Proteus.Add("Panties");
        Game.Permanent["Panties"] = new PermanentSetting(true, 2);
        Game.Live[("Panties", "Lace", "High")] = [Row(0, "#FFFFFF"), Row(1, "#000000")];
        // Applying the design swaps the legs and, as a restore does, writes the bound mod's permanent setting.
        Game.ApplyEffects[Design] = () =>
        {
            Game.GlamourerNow = FakeGame.GlamState(("Body", 100), ("Legs", 555));
            Game.Permanent["Panties"] = new PermanentSetting(false, 7);
        };
    }

    public void Dispose() => fx.Dispose();

    private static ColorTableRowPreset Row(int row, string diffuse)
        => new() { Row = row, SubRowA = new ColorTableSubRowPreset { Diffuse = diffuse } };

    private (ChangeResult? Error, PendingApply? Pending) Begin(Guid id, bool allow = false)
        => fx.Dispatcher.Run(() => B.BeginApply(id, allow, C.EquipsRecorded));

    private ChangeResult Apply(Guid id, bool allow = false)
    {
        var (error, pending) = Begin(id, allow);
        return error ?? fx.Dispatcher.Run(() => B.FinishApply(pending!, settled: true));
    }

    private PutBackReport PutBack(bool skip = false) => fx.Dispatcher.Run(() => C.PutBack(skip));

    private List<string> Order() => Game.Order.Select(o => o.Split('@')[0]).ToList();

    // ── apply ───────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-binding-reapply-9")]
    public void ApplyDesign_RecordsSession_CallsIpcOnce_ReadsAfterOnlyWhenSettled()
    {
        var r = Apply(Design);
        Assert.Equal(200, r.Status);
        Assert.Equal(1, Game.ApplyCalls);
        var body = (Dictionary<string, object?>)r.Body!;
        Assert.Equal(true, body["finalizedSeen"]);
        Assert.Equal(true, body["settled"]);
        // After is read in the final hop, past the IPC.
        var order = Order();
        Assert.True(order.LastIndexOf(nameof(IHarnessGame.GlamourerState)) > order.IndexOf(nameof(IHarnessGame.ApplyDesign)));
        Assert.True(B.HasSession);
    }

    [Fact]
    [Trait("Plan", "EDGE-binding-reapply-21")]
    [Trait("Plan", "EDGE-binding-reapply-22")]
    [Trait("Plan", "EDGE-binding-reapply-24")]
    public void ApplyDesign_Refusals()
    {
        Assert.Equal("design-not-found", Begin(Guid.NewGuid()).Error!.Code);
        Game.ApplyAnswer = "InvalidKey";
        var refused = Begin(Design).Error!;
        Assert.Equal(("glamourer-refused", 409), (refused.Code, refused.Status));
        Assert.False(B.HasSession);
        Game.ApplyAnswer = null;
        Game.GlamourerUp = false;
        Assert.Equal(("glamourer-unavailable", 503), (Begin(Design).Error!.Code, Begin(Design).Error!.Status));
        Game.GlamourerUp = true;
        Game.LoggedIn = false;
        Assert.Equal("not-logged-in", Begin(Design).Error!.Code);
    }

    [Fact]
    [Trait("Plan", "EDGE-binding-reapply-23")]
    public void ApplyDesign_RefusedWhilePresetsPinned()
    {
        Game.Pinned = true;
        Assert.Equal("presets-pinned", Begin(Design).Error!.Code);
        Assert.Equal(0, Game.ApplyCalls);
    }

    [Fact]
    [Trait("Plan", "EDGE-binding-reapply-29")]
    public void ApplyDesign_RefusedAfterEquips_PutBackOrderGearFirst()
    {
        Game.Gear["Body"] = new Worn(111, 0, 0);
        Assert.Equal(200, fx.Dispatcher.Run(() => C.Equip("Body", 500, null)).Status);
        Assert.Equal("put-back-first", Begin(Design).Error!.Code);
        PutBack();
        Assert.Equal(200, Apply(Design).Status);
    }

    [Theory]
    [Trait("Plan", "EDGE-binding-reapply-41")]
    [InlineData("shader", "shader-upgrade-busy")]
    [InlineData("variants", "variant-repoint-busy")]
    [InlineData("hair", "hair-port-busy")]
    public void ApplyDesign_RefusedWhileAutoWritersBusy(string which, string code)
    {
        if (which == "shader") Game.ShaderUpgradeView = Game.ShaderUpgradeView with { Busy = true };
        if (which == "variants") Game.VariantRepointView = Game.VariantRepointView with { Busy = true };
        if (which == "hair") Game.HairPortView = Game.HairPortView with { CheckOwed = true };
        Assert.Equal(code, Begin(Design).Error!.Code);
    }

    [Fact]
    [Trait("Plan", "EDGE-binding-reapply-41")]
    public void ApplyDesign_RefusedWhileAutoRefitBusy()
    {
        Game.View = Game.View with { Busy = true };
        var r = fx.Dispatcher.Run(() => C.Gate(false, Game.AutoRefit().Busy));
        Assert.Equal("auto-refit-busy", r!.Code);
    }

    [Theory]
    [Trait("Plan", "EDGE-binding-reapply-47")]
    [InlineData("auto refit")]
    [InlineData("gear shader upgrade")]
    [InlineData("Gear variants")]
    [InlineData("auto hair port")]
    [InlineData("auto hat compat")]
    public void ApplyDesign_AutoWritersOn_RefusedUnlessAllowed(string writer)
    {
        Game.AutoWriters = [writer];
        var refused = Begin(Design).Error!;
        Assert.Equal("auto-writers-on", refused.Code);
        Assert.Contains(writer, refused.Message);
        Assert.Equal(200, Apply(Design, allow: true).Status);
    }

    private static JObject Customize(params (string Name, int Value, bool Apply)[] fields)
    {
        var c = new JObject();
        foreach (var (n, v, a) in fields) c[n] = new JObject { ["Value"] = v, ["Apply"] = a };
        return new JObject { ["Customize"] = c };
    }

    [Fact]
    [Trait("Plan", "EDGE-binding-reapply-42")]
    public void ModelChange_PureCases()
    {
        var state = Customize(("Race", 1, true), ("Gender", 1, true), ("Clan", 2, true), ("BodyType", 1, true), ("Hairstyle", 5, true));
        Assert.Equal("Race", HarnessBindings.ModelChange(Customize(("Race", 3, true)), state, false));
        Assert.Equal("Gender", HarnessBindings.ModelChange(Customize(("Gender", 0, true)), state, false));
        Assert.Equal("Clan", HarnessBindings.ModelChange(Customize(("Clan", 1, true)), state, false));
        Assert.Equal("BodyType", HarnessBindings.ModelChange(Customize(("BodyType", 3, true)), state, false));
        Assert.Null(HarnessBindings.ModelChange(Customize(("Race", 1, true)), state, false));
        Assert.Null(HarnessBindings.ModelChange(Customize(("Race", 3, false)), state, false));
        Assert.Null(HarnessBindings.ModelChange(new JObject(), state, false));
    }

    [Fact]
    [Trait("Plan", "EDGE-binding-reapply-42")]
    public void ApplyDesign_ModelChange_Refused()
    {
        Game.GlamourerNow["Customize"] = Customize(("Race", 1, true))["Customize"];
        Game.DesignJsons[Design] = Customize(("Race", 4, true));
        Assert.Equal("model-change", Begin(Design).Error!.Code);
        Assert.Equal(0, Game.ApplyCalls);
    }

    [Fact]
    [Trait("Plan", "EDGE-binding-reapply-48")]
    public void ModelChange_HairstyleCountsOnlyWhenAutoHairPortOn()
    {
        var state = Customize(("Hairstyle", 5, true));
        var design = Customize(("Hairstyle", 9, true));
        Assert.Null(HarnessBindings.ModelChange(design, state, hairToo: false));
        Assert.Equal("Hairstyle", HarnessBindings.ModelChange(design, state, hairToo: true));
    }

    [Fact]
    [Trait("Plan", "EDGE-binding-reapply-48")]
    public void ApplyDesign_HairstyleChange_RefusedWhenAutoHairPortOn()
    {
        Game.GlamourerNow["Customize"] = Customize(("Hairstyle", 5, true))["Customize"];
        Game.DesignJsons[Design] = Customize(("Hairstyle", 9, true));
        Game.AutoWriters = ["auto hair port"];
        Assert.Equal("model-change", Begin(Design, allow: true).Error!.Code);
        Game.AutoWriters = [];
        Assert.Equal(200, Apply(Design).Status);
    }

    // ── the on-disk record ──────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-binding-reapply-13")]
    [Trait("Plan", "EDGE-binding-reapply-38")]
    public void Record_WrittenBeforeFirstPersistentChange()
    {
        bool existed = false;
        JObject? seen = null;
        Game.BeforeApply = () => { existed = File.Exists(RecordPath); if (existed) seen = JObject.Parse(File.ReadAllText(RecordPath)); };
        Assert.Equal(200, Apply(Design).Status);
        Assert.True(existed);
        Assert.Equal(101UL, seen!["glamourerBefore"]!["Equipment"]!["Legs"]!["ItemId"]!.ToObject<ulong>());
        Assert.True(seen["permanentBefore"]!["Panties"]!["enabled"]!.ToObject<bool>());
        Assert.Equal(2, seen["permanentBefore"]!["Panties"]!["priority"]!.ToObject<int>());
        Assert.NotNull(seen["live"]);
        var text = File.ReadAllText(RecordPath);
        Assert.DoesNotContain(Game.CharacterName!, text);
        Assert.DoesNotContain("Bound Look", text);
    }

    [Fact]
    [Trait("Plan", "AC-binding-reapply-13")]
    [Trait("Plan", "EDGE-binding-reapply-38")]
    public void Record_WrittenBeforePermanentWrite()
    {
        bool existed;
        Assert.Equal(200, fx.Dispatcher.Run(() => B.WritePermanent(Coll, "Panties", false, null)).Status);
        existed = File.Exists(RecordPath);
        Assert.True(existed);
        var rec = JObject.Parse(File.ReadAllText(RecordPath));
        Assert.Equal(JTokenType.Null, rec["glamourerBefore"]!.Type);
        Assert.True(rec["permanentBefore"]!["Panties"]!["enabled"]!.ToObject<bool>());
    }

    [Fact]
    [Trait("Plan", "AC-binding-reapply-13")]
    [Trait("Plan", "EDGE-binding-reapply-38")]
    public void Record_WriteFails_RefusesAndChangesNothing()
    {
        // A file where the session folder must go: the record cannot be written.
        var sessionDir = Path.GetDirectoryName(RecordPath)!;
        Directory.CreateDirectory(Path.GetDirectoryName(sessionDir)!);
        File.WriteAllText(sessionDir, "in the way");
        var r = Begin(Design).Error!;
        Assert.Equal(("record-failed", 503), (r.Code, r.Status));
        Assert.Equal(0, Game.ApplyCalls);
        Assert.Equal("record-failed", fx.Dispatcher.Run(() => B.WritePermanent(Coll, "Panties", false, null)).Code);
        Assert.Empty(Game.PermanentWrites);
        Assert.False(B.HasSession);
    }

    [Fact]
    [Trait("Plan", "AC-binding-reapply-13")]
    [Trait("Plan", "EDGE-binding-reapply-38")]
    public void Record_WrittenOncePerSession()
    {
        Apply(Design);
        var first = File.GetLastWriteTimeUtc(RecordPath);
        var content = File.ReadAllText(RecordPath);
        Game.ApplyEffects[Design] = () => { };
        System.Threading.Thread.Sleep(20);
        Apply(Design);
        fx.Dispatcher.Run(() => B.WritePermanent(Coll, "Panties", true, null));
        Assert.Equal(content, File.ReadAllText(RecordPath));
        Assert.Equal(first, File.GetLastWriteTimeUtc(RecordPath));
    }

    [Fact]
    [Trait("Plan", "EDGE-binding-reapply-50")]
    public void Record_RewrittenOnceWithGlamourerBeforeOnFirstApply()
    {
        fx.Dispatcher.Run(() => B.WritePermanent(Coll, "Panties", false, null));
        Assert.Equal(JTokenType.Null, JObject.Parse(File.ReadAllText(RecordPath))["glamourerBefore"]!.Type);

        JObject? atApply = null;
        Game.BeforeApply = () => atApply = JObject.Parse(File.ReadAllText(RecordPath));
        Apply(Design);
        Assert.NotNull(atApply);
        Assert.Equal(101UL, atApply!["glamourerBefore"]!["Equipment"]!["Legs"]!["ItemId"]!.ToObject<ulong>());
        // The permanent Before stays the first one (from before the harness's write).
        Assert.True(atApply["permanentBefore"]!["Panties"]!["enabled"]!.ToObject<bool>());
        Assert.False(File.Exists(RecordPath + ".tmp"));

        var content = File.ReadAllText(RecordPath);
        Game.ApplyEffects[Design] = () => { };
        Apply(Design);
        Assert.Equal(content, File.ReadAllText(RecordPath));
    }

    [Fact]
    [Trait("Plan", "EDGE-binding-reapply-50")]
    public void Record_RewriteFails_NoIpc()
    {
        fx.Dispatcher.Run(() => B.WritePermanent(Coll, "Panties", false, null));
        // Block the temp file the rewrite needs.
        Directory.CreateDirectory(RecordPath + ".tmp");
        var r = Begin(Design).Error!;
        Assert.Equal("record-failed", r.Code);
        Assert.Equal(0, Game.ApplyCalls);
    }

    // ── permanent ───────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-binding-reapply-12")]
    [Trait("Plan", "EDGE-binding-reapply-27")]
    public void Permanent_FromApplyAndWrite_RestoredOnlyWhileStillOurs()
    {
        Game.Proteus.Add("Bralettes");
        Game.Permanent["Bralettes"] = new PermanentSetting(false, 0);
        Apply(Design);                                                                // the restore writes Panties
        fx.Dispatcher.Run(() => B.WritePermanent(Coll, "Bralettes", true, 4));        // the harness writes Bralettes
        Game.Permanent["Bralettes"] = new PermanentSetting(true, 9);                  // ... and the user changes it since
        var r = PutBack().Binding!;
        Assert.Equal("restored", r.Glamourer);
        Assert.Equal("restored", r.Permanent.Single(e => e.Name == "Panties").Result);
        Assert.Equal("changed", r.Permanent.Single(e => e.Name == "Bralettes").Result);
        Assert.Equal(new PermanentSetting(true, 2), Game.Permanent["Panties"]);
        Assert.Equal(new PermanentSetting(true, 9), Game.Permanent["Bralettes"]);
    }

    [Fact]
    [Trait("Plan", "AC-binding-reapply-12")]
    [Trait("Plan", "EDGE-binding-reapply-27")]
    public void Permanent_OptionsAndInheritance_FromARestore_PutBack()
    {
        // The restore sets the bound mod's options and gives a mod that only inherited a setting of its own.
        Game.Permanent["Panties"] = new PermanentSetting(true, 2, new Dictionary<string, List<string>> { ["Lace"] = ["Low"] }, true);
        Game.Permanent["Shared"] = PermanentSetting.Inherits;
        Game.ApplyEffects[Design] = () =>
        {
            Game.GlamourerNow = FakeGame.GlamState(("Body", 100), ("Legs", 555));
            Game.Permanent["Panties"] = new PermanentSetting(true, 2, new Dictionary<string, List<string>> { ["Lace"] = ["High"] }, true);
            Game.Permanent["Shared"] = new PermanentSetting(true, 9);
            Game.Permanent["Fresh"] = new PermanentSetting(true, 4);   // never configured before
        };
        Apply(Design);
        var rec = JObject.Parse(File.ReadAllText(RecordPath));
        Assert.Equal("Low", rec["permanentBefore"]!["Panties"]!["options"]!["Lace"]![0]!.ToString());
        Assert.False(rec["permanentBefore"]!["Shared"]!["own"]!.ToObject<bool>());

        var r = PutBack().Binding!;
        Assert.All(r.Permanent, e => Assert.Equal("restored", e.Result));
        Assert.Equal(["Low"], Game.Permanent["Panties"].Options["Lace"]);
        Assert.False(Game.Permanent["Shared"].Own);
        Assert.False(Game.Permanent["Fresh"].Own);
    }

    [Fact]
    [Trait("Plan", "AC-binding-reapply-13")]
    [Trait("Plan", "EDGE-binding-reapply-38")]
    public void Record_LiveBefore_IsTheFirstSnapshot_NotTheHarnessEdit()
    {
        var original = Game.LiveNow;
        fx.Dispatcher.Run(() => B.EditRow("Panties", "Lace", "High", 0, "#FF00FF"));
        Game.LiveNow = original with { BagJson = "{\"edited\":\"#FF00FF\"}" };
        Apply(Design);
        var bag = JObject.Parse(File.ReadAllText(RecordPath))["live"]!["bag"]!;
        Assert.DoesNotContain("FF00FF", bag.ToString());
    }

    [Fact]
    [Trait("Plan", "AC-binding-reapply-13")]
    [Trait("Plan", "EDGE-binding-reapply-38")]
    public void Record_LaterSessionWritesItsOwnFile()
    {
        Apply(Design);
        Game.GlamourerNow = FakeGame.GlamState(("Body", 300));        // the user applied something: abandoned
        Assert.Equal("changed", PutBack().Binding!.Glamourer);
        var first = File.ReadAllText(RecordPath);

        Game.ApplyEffects[Design] = () => Game.GlamourerNow = FakeGame.GlamState(("Body", 400));
        Apply(Design);
        var second = Path.Combine(Path.GetDirectoryName(RecordPath)!, "binding-2.json");
        Assert.True(File.Exists(second));
        Assert.Equal(first, File.ReadAllText(RecordPath));
        Assert.Contains(C.Leftovers().Split(", "), l => l.EndsWith("binding-2.json", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Plan", "EDGE-binding-reapply-29")]
    public void PutBack_GearBeforeGlamourer()
    {
        Apply(Design);
        Game.Gear["Head"] = new Worn(111, 0, 0);
        Assert.Equal(200, fx.Dispatcher.Run(() => C.Equip("Head", 500, null)).Status);   // equips after an apply are allowed
        Game.Order.Clear();
        PutBack();
        var order = Order();
        Assert.True(order.IndexOf(nameof(IHarnessGame.SetGear)) < order.IndexOf(nameof(IHarnessGame.RestoreGlamourerState)));
    }

    [Fact]
    [Trait("Plan", "AC-binding-reapply-12")]
    public void Permanent_PartialWriteOnInheritedMod_KeepsTheOtherFields()
    {
        // Panties inherits: enabled at priority 3 from the parent collection.
        Game.Permanent["Panties"] = new PermanentSetting(true, 3, new Dictionary<string, List<string>>(), Own: false);
        var r = fx.Dispatcher.Run(() => B.WritePermanent(Coll, "Panties", null, 5));
        Assert.Equal(200, r.Status);
        Assert.Equal(new PermanentSetting(true, 5), Game.Permanent["Panties"]);   // still enabled
        var body = (Dictionary<string, object?>)r.Body!;
        Assert.Contains("enabled = True", body["before"]!.ToString());
        Assert.Contains("priority = 3", body["before"]!.ToString());
        Assert.Contains("own = False", body["before"]!.ToString());

        PutBack();
        Assert.False(Game.Permanent["Panties"].Own);   // back to inheriting
    }

    [Fact]
    [Trait("Plan", "EDGE-binding-reapply-22")]
    public void ApplyDesign_RefusedAfterRecord_LeavesNoStaleRecordForTheNextSession()
    {
        Game.ApplyAnswer = "InvalidKey";
        Assert.Equal("glamourer-refused", Begin(Design).Error!.Code);
        Game.ApplyAnswer = null;
        Game.GlamourerNow = FakeGame.GlamState(("Body", 222), ("Legs", 101));   // the look moved on before the real apply
        Apply(Design);
        var rec = JObject.Parse(File.ReadAllText(RecordPath));
        Assert.Equal(222UL, rec["glamourerBefore"]!["Equipment"]!["Body"]!["ItemId"]!.ToObject<ulong>());
    }

    [Fact]
    [Trait("Plan", "AC-binding-reapply-12")]
    public void Permanent_NoOpWrite_WritesNothing_NoRedraw()
    {
        var redraws = Game.RedrawRequests;
        var r = fx.Dispatcher.Run(() => B.WritePermanent(Coll, "Panties", true, 2));   // already enabled at 2
        Assert.Equal(200, r.Status);
        Assert.Equal(false, ((Dictionary<string, object?>)r.Body!)["redrawRequested"]);
        Assert.Empty(Game.PermanentWrites);
        Assert.Equal(redraws, Game.RedrawRequests);
        Assert.False(B.HasSession);
        Assert.False(File.Exists(RecordPath));   // nothing to record either
    }

    [Fact]
    [Trait("Plan", "EDGE-binding-reapply-50")]
    public void Record_RefusedApplyInsideASession_NextApplyRewritesGlamourerBefore()
    {
        fx.Dispatcher.Run(() => B.WritePermanent(Coll, "Panties", false, null));   // session starts with a permanent write
        Game.ApplyAnswer = "InvalidKey";
        Assert.Equal("glamourer-refused", Begin(Design).Error!.Code);
        Game.ApplyAnswer = null;
        Game.GlamourerNow = FakeGame.GlamState(("Body", 333), ("Legs", 101));      // the look moves on
        Apply(Design);
        var rec = JObject.Parse(File.ReadAllText(RecordPath));
        Assert.Equal(333UL, rec["glamourerBefore"]!["Equipment"]!["Body"]!["ItemId"]!.ToObject<ulong>());
    }

    [Fact]
    [Trait("Plan", "AC-binding-reapply-12")]
    public void FinishApply_IgnoresTheManagedOutputMod()
    {
        Game.Permanent[Game.ManagedModDir] = new PermanentSetting(true, 10);
        Game.ApplyEffects[Design] = () =>
        {
            Game.GlamourerNow = FakeGame.GlamState(("Body", 100), ("Legs", 555));
            Game.Permanent[Game.ManagedModDir] = new PermanentSetting(true, 99);    // the compositor's raise
        };
        Apply(Design);
        PutBack();
        Assert.Equal(new PermanentSetting(true, 99), Game.Permanent[Game.ManagedModDir]);
        Assert.DoesNotContain(Game.PermanentWrites, w => w.Mod == Game.ManagedModDir);
    }

    [Fact]
    [Trait("Plan", "EDGE-binding-reapply-27")]
    public void CollectionChangedMidSession_Refused()
    {
        Apply(Design);
        Game.Collection = Guid.NewGuid();
        Assert.Equal("collection-changed", Begin(Design).Error!.Code);
        Assert.Equal("collection-changed", fx.Dispatcher.Run(() => B.WritePermanent(Game.Collection!.Value, "Panties", false, null)).Code);
    }

    [Fact]
    [Trait("Plan", "EDGE-binding-reapply-33")]
    public void Permanent_NonProteusMod_Refused()
    {
        var r = fx.Dispatcher.Run(() => B.WritePermanent(Coll, "SomeoneElses", false, null));
        Assert.Equal(("not-a-proteus-mod", 409), (r.Code, r.Status));
        Assert.Empty(Game.PermanentWrites);
        Assert.False(File.Exists(RecordPath));
    }

    // ── live edits ──────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-binding-reapply-11")]
    public void EditClear_SnapshotLiveOnce_PutBackRestoresLive()
    {
        var before = Game.LiveNow;
        Assert.Equal(200, fx.Dispatcher.Run(() => B.EditRow("Panties", "Lace", "High", 1, "#FF00FF")).Status);
        Assert.Equal("#FF00FF", Game.Live[("Panties", "Lace", "High")].Single(r => r.Row == 1).SubRowA!.Diffuse);
        Game.LiveNow = before with { BagJson = "{\"edited\":true}" };
        Assert.Equal(200, fx.Dispatcher.Run(() => B.ClearOption("Panties", "Lace", "High")).Status);
        Assert.False(Game.Live.ContainsKey(("Panties", "Lace", "High")));

        var r = PutBack().Binding!;
        Assert.Equal("none", r.Glamourer);
        Assert.Equal("restored", r.Live);
        Assert.Equal(before, Assert.Single(Game.LiveRestores));   // the first snapshot, not the edited one
        Assert.False(B.HasSession);
    }

    [Fact]
    [Trait("Plan", "EDGE-binding-reapply-28")]
    public void PutBack_ActiveDesignChangedSince_LiveLeftAlone()
    {
        fx.Dispatcher.Run(() => B.EditRow("Panties", "Lace", "High", 0, "#123456"));
        Game.LiveNow = Game.LiveNow with { Active = Other };
        var r = PutBack().Binding!;
        Assert.Equal("changed", r.Live);
        Assert.Empty(Game.LiveRestores);
        Assert.False(B.HasSession);
    }

    [Fact]
    [Trait("Plan", "EDGE-binding-reapply-30")]
    [Trait("Plan", "EDGE-binding-reapply-31")]
    [Trait("Plan", "EDGE-binding-reapply-32")]
    public void Edit_Refusals()
    {
        ChangeResult Edit(string mod = "Panties", int row = 0, string? group = "Lace", string? option = "High")
            => fx.Dispatcher.Run(() => B.EditRow(mod, group, option, row, "#FF0000"));
        Game.EditStates["Off"] = new BindingEditState(false, true, true, false);
        Game.EditStates["NoActive"] = new BindingEditState(true, false, false, false);
        Game.EditStates["Unbound"] = new BindingEditState(true, true, false, false);
        Game.EditStates["Pinned"] = new BindingEditState(true, true, true, true);
        Assert.Equal("binding-off", Edit("Off").Code);
        Assert.Equal("no-active-binding", Edit("NoActive").Code);
        Assert.Equal("not-bound", Edit("Unbound").Code);
        Assert.Equal("preset-driven", Edit("Pinned").Code);
        Assert.Equal("row-not-found", Edit(row: 9).Code);
        Assert.Equal("no-override-rows", Edit(group: "Lace", option: "Low").Code);
        Assert.Equal("not-bound", fx.Dispatcher.Run(() => B.ClearOption("Unbound", null, null)).Code);
        Assert.False(B.HasSession);
    }

    // ── put back ────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-binding-reapply-9")]
    [Trait("Plan", "EDGE-binding-reapply-25")]
    public void PutBack_RestoresGlamourer_CarriersFromEitherMomentExcluded_SuppressionFirst()
    {
        Game.CarriersValue = new DesignBindingService.Carriers(null, [9295UL], OwnedSlots: ["RFinger"]);
        Game.GlamourerNow = FakeGame.GlamState(("Body", 100), ("Legs", 101), ("RFinger", 9295), ("LFinger", 42));
        Game.ApplyEffects[Design] = () => Game.GlamourerNow = FakeGame.GlamState(("Body", 100), ("Legs", 555), ("RFinger", 9295), ("LFinger", 42));
        Apply(Design);
        Game.CarriersValue = new DesignBindingService.Carriers(null, [9295UL], OwnedSlots: ["LFinger"]);

        var r = PutBack().Binding!;
        Assert.Equal("restored", r.Glamourer);
        var restore = Assert.Single(Game.Restores);
        Assert.Contains("RFinger", restore.Skip);
        Assert.Contains("LFinger", restore.Skip);
        Assert.Equal(101UL, Game.GlamourerNow["Equipment"]!["Legs"]!["ItemId"]!.ToObject<ulong>());
    }

    [Fact]
    [Trait("Plan", "EDGE-binding-reapply-26")]
    public void PutBack_GlamourerChangedSince_PermanentAndLiveSkippedInconsistent()
    {
        Apply(Design);
        Game.GlamourerNow = FakeGame.GlamState(("Body", 300), ("Legs", 555));   // the user applied something since
        var r = PutBack().Binding!;
        Assert.Equal("changed", r.Glamourer);
        Assert.Equal("skipped-inconsistent", Assert.Single(r.Permanent).Result);
        Assert.Equal("skipped-inconsistent", r.Live);
        Assert.Empty(Game.Restores);
        // Abandoned for good (logged; the record keeps the Before values): a later put-back never restores them onto
        // a look the harness did not put back.
        Assert.False(B.HasSession);
        Assert.Null(PutBack().Binding);
        Assert.Null(PutBack().Binding);
        Assert.Empty(Game.PermanentWrites);
        Assert.Empty(Game.LiveRestores);
        Assert.Equal(new PermanentSetting(false, 7), Game.Permanent["Panties"]);
    }

    [Fact]
    [Trait("Plan", "EDGE-binding-reapply-43")]
    public void PutBack_GlamourerPartial_BlocksPermanentAndLive_StaysRecorded()
    {
        Apply(Design);
        Game.AfterRestore = () => Game.GlamourerNow["Materials"] = new JObject { ["x"] = 1 };
        var r = PutBack().Binding!;
        Assert.Equal("partial", r.Glamourer);
        Assert.Equal("skipped-inconsistent", Assert.Single(r.Permanent).Result);
        Assert.Equal("skipped-inconsistent", r.Live);
        Assert.True(B.HasSession);

        // The second put-back retries the restore (not "changed"), still partial: nothing else goes back.
        var second = PutBack().Binding!;
        Assert.Equal("partial", second.Glamourer);
        Assert.Equal(2, Game.Restores.Count);
        Assert.Empty(Game.PermanentWrites);
        Assert.Empty(Game.LiveRestores);

        // Once the look comes back, the rest follows.
        Game.AfterRestore = null;
        Game.GlamourerNow.Remove("Materials");
        var third = PutBack().Binding!;
        Assert.Equal("restored", third.Glamourer);
        Assert.Equal("restored", Assert.Single(third.Permanent).Result);
        Assert.Equal("restored", third.Live);
        Assert.False(B.HasSession);
    }

    [Fact]
    [Trait("Plan", "EDGE-binding-reapply-40")]
    public void PutBack_QuiescesBeforeAnyRestore()
    {
        Apply(Design);
        Game.Order.Clear();
        PutBack();
        var order = Order();
        int quiesce = order.IndexOf(nameof(IHarnessGame.QuiesceBinding));
        Assert.True(quiesce >= 0);
        Assert.True(quiesce < order.IndexOf(nameof(IHarnessGame.RestoreGlamourerState)));
        Assert.True(quiesce < order.IndexOf(nameof(IHarnessGame.WritePermanent)));
        Assert.True(quiesce < order.IndexOf(nameof(IHarnessGame.RestoreLive)));
    }

    [Fact]
    [Trait("Plan", "EDGE-binding-reapply-49")]
    public void PutBack_NoBindingSession_DoesNotQuiesce()
    {
        Game.Gear["Body"] = new Worn(111, 0, 0);
        fx.Dispatcher.Run(() => C.Equip("Body", 500, null));
        var r = PutBack();
        Assert.Null(r.Binding);
        Assert.Equal(0, Game.Quiesces);
    }

    [Fact]
    [Trait("Plan", "EDGE-binding-reapply-34")]
    public void PutBack_FailedStepStaysRecordedAndRetries()
    {
        Apply(Design);
        Game.RestoreAnswer = "InvalidState";
        var r = PutBack().Binding!;
        Assert.Equal("failed", r.Glamourer);
        Assert.Equal("skipped-inconsistent", Assert.Single(r.Permanent).Result);
        Assert.True(B.HasSession);
        Assert.Contains("glamourer state", C.Leftovers());

        Game.RestoreAnswer = null;
        var again = PutBack().Binding!;
        Assert.Equal("restored", again.Glamourer);
        Assert.Equal("restored", Assert.Single(again.Permanent).Result);
        Assert.Equal("restored", again.Live);
        Assert.False(B.HasSession);
        Assert.True(C.Empty);
    }

    [Fact]
    [Trait("Plan", "EDGE-binding-reapply-46")]
    public void Stop_DuringSettle_SkipsHalfRecordedSession_LogsAndNeverThrows()
    {
        Apply(Design);
        var r = PutBack(skip: true).Binding!;
        Assert.Equal("skipped-settling", r.Glamourer);
        Assert.Empty(Game.Restores);
        Assert.Equal(0, Game.Quiesces);
        Assert.True(B.HasSession);
    }

    [Fact]
    [Trait("Plan", "EDGE-binding-reapply-46")]
    public void Settling_GatesEveryChange()
    {
        C.ApplySettling = true;
        Assert.Equal("busy", C.Gate(false, false)!.Code);
        C.ApplySettling = false;
        Assert.Null(C.Gate(false, false));
    }

    [Fact]
    [Trait("Plan", "AC-binding-reapply-9")]
    public void State_ShowsTheBindingSession()
    {
        Apply(Design);
        var view = HarnessJson.Bytes(fx.Dispatcher.Run(C.View));
        using var doc = System.Text.Json.JsonDocument.Parse(view);
        var binding = doc.RootElement.GetProperty("binding");
        Assert.Equal(Design.ToString(), binding.GetProperty("applied")[0].GetString());
        Assert.Equal("Panties", binding.GetProperty("permanent")[0].GetString());
    }
}
