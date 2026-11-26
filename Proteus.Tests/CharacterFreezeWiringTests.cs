using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Dalamud.Game.ClientState.Conditions;
using Proteus.Interop;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// The character freeze's game side, pinned by its statics and by reading the source: <c>CharacterFreeze</c> needs the
/// game to run. Textual and deliberately so — change these with the code. Methods are found by their declarations.
/// </summary>
public class CharacterFreezeWiringTests
{
    private static string ProteusDir([CallerFilePath] string here = "")
        => Path.Combine(Path.GetDirectoryName(here)!, "..", "Proteus");

    private static string Source(string relative) => File.ReadAllText(Path.Combine(ProteusDir(), relative)).Replace("\r\n", "\n");

    /// <summary>The body of the first block after <paramref name="signature"/>, braces matched.</summary>
    private static string Body(string source, string signature, int from = 0)
    {
        int at = source.IndexOf(signature, from, StringComparison.Ordinal);
        Assert.True(at >= 0, $"no {signature}");
        int open = source.IndexOf('{', at + signature.Length), depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source[open..(i + 1)];
        }
        Assert.Fail($"unbalanced {signature}");
        return "";
    }

    private static int At(string s, string what)
    {
        int i = s.IndexOf(what, StringComparison.Ordinal);
        Assert.True(i >= 0, $"no {what}");
        return i;
    }

    private static string Freeze => Source("Interop/CharacterFreeze.cs");
    private static string OnUpdate => Body(Freeze, "private void OnUpdate(");

    // ── statics ──────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-studio-freeze-5")]
    [Trait("Plan", "EDGE-studio-freeze-32")]
    [Trait("Plan", "EDGE-studio-freeze-33")]
    [Trait("Plan", "EDGE-studio-freeze-48")]
    public void BusyConditions_is_exactly_the_planned_set()
    {
        string[] planned =
        [
            "Occupied", "Occupied30", "OccupiedInEvent", "OccupiedInQuestEvent", "Occupied33", "OccupiedInCutSceneEvent",
            "Occupied38", "Occupied39", "OccupiedSummoningBell", "TradeOpen", "Crafting", "PreparingToCraft",
            "ExecutingCraftingAction", "Gathering", "ExecutingGatheringAction", "Fishing", "InCombat", "Casting", "Casting87",
            "Mounted", "Mounting", "Mounting71", "MountOrOrnamentTransition", "RidingPillion", "Emoting", "InThatPosition",
            "Jumping", "Jumping61", "BeingMoved", "UsingChocoboTaxi", "InFlight", "CarryingObject", "CarryingItem",
            "Performing", "PlayingMiniGame", "ChocoboRacing", "PlayingLordOfVerminion", "Swimming", "Diving", "Unconscious",
            "WatchingCutscene", "WatchingCutscene78", "BetweenAreas", "BetweenAreas51", "LoggingOut", "EditingPortrait",
            "MeldingMateria", "OperatingSiegeMachine",
        ];
        // Compared as values: some flags have more than one name (Mounted prints as Mounted2).
        var flags = CharacterFreeze.BusyConditions;
        Assert.Equal(planned.Select(Enum.Parse<ConditionFlag>).Order(), flags.Order());
        Assert.Equal(flags.Length, flags.Distinct().Count());
        foreach (var occupied in Enum.GetNames<ConditionFlag>().Where(n => n.StartsWith("Occupied", StringComparison.Ordinal)))
            Assert.Contains(Enum.Parse<ConditionFlag>(occupied), flags);
        Assert.DoesNotContain(ConditionFlag.UsingFashionAccessory, flags);
    }

    [Fact]
    [Trait("Plan", "AC-studio-freeze-9")]
    [Trait("Plan", "EDGE-studio-freeze-40")]
    public void BusyReasonOf_reports_the_first_reason_in_order()
    {
        Assert.Equal("mode:EmoteLoop", CharacterFreeze.BusyReasonOf(3, true, false, false, _ => true));
        Assert.Equal("InCombat", CharacterFreeze.BusyReasonOf(1, true, true, false, _ => false));
        Assert.Equal("OccupiedSummoningBell",
                     CharacterFreeze.BusyReasonOf(1, false, false, false, f => f == ConditionFlag.OccupiedSummoningBell));
        Assert.Null(CharacterFreeze.BusyReasonOf(1, false, false, false, f => f == ConditionFlag.UsingFashionAccessory));
        Assert.Null(CharacterFreeze.BusyReasonOf(1, false, false, false, _ => false));
    }

    // ── wiring ───────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-studio-freeze-1")]
    [Trait("Plan", "AC-studio-freeze-2")]
    [Trait("Plan", "EDGE-studio-freeze-9")]
    public void Want_is_the_second_statement_of_the_braced_arming_block()
    {
        var panel = Source("Gui/PartsPanel.cs");
        var m = Regex.Match(panel, @"ArmedModelFile\([^\n]*is \{ \} armed\)\s*\{");
        Assert.True(m.Success, "the arming if has no braces");
        var block = Body(panel, "is { } armed)", m.Index);
        var code = Regex.Replace(block.Trim('{', '}'), @"//[^\n]*", "");   // comments may hold a ';'
        var statements = code.Split(';').Select(x => x.Trim()).Where(x => x.Length > 0).ToArray();
        Assert.StartsWith("liveBrush.ArmBrush(armed,", statements[0]);
        Assert.Equal("if (FreezesCharacter(tool) && liveBrush.Problem == null) freeze.Want()", statements[1]);
        Assert.Equal(2, statements.Length);
        Assert.Single(Regex.Matches(panel, @"freeze\.Want\(\)"));
    }

    [Fact]
    [Trait("Plan", "AC-studio-freeze-3")]
    [Trait("Plan", "EDGE-studio-freeze-19")]
    [Trait("Plan", "EDGE-studio-freeze-20")]
    public void Plugin_disposes_freeze_after_harness_and_has_no_hook_service()
    {
        var plugin = Source("Plugin.cs");
        var dispose = Body(plugin, "private void DisposeServices(");
        int harness = At(dispose, "harness.Dispose()"), freeze = At(dispose, "characterFreeze.Dispose()");
        Assert.True(harness < freeze, "the freeze is disposed after the harness");
        Assert.True(freeze < At(dispose, "statusWindow.TickImport"), "the freeze is disposed before everything else");
        Assert.DoesNotContain("IGameInteropProvider", plugin);
    }

    [Fact]
    [Trait("Plan", "AC-studio-freeze-10")]
    [Trait("Plan", "EDGE-studio-freeze-18")]
    public void Freeze_uses_the_ClientStructs_slot_functions_and_no_hook_or_byte_pattern()
    {
        var src = Freeze;
        var ctor = Body(src, "public CharacterFreeze(");
        foreach (var fn in new[] { "GetSlotSpeed", "SetSlotSpeed", "GetSlotTimeline" })
            Assert.Contains($"ActionTimelineSequencer.Addresses.{fn}.Value != 0", ctor);
        Assert.Contains("public bool Available => resolved && !faulted;", src);
        foreach (var banned in new[] { "Hook<", "HookFromAddress", "IGameInteropProvider" })
            Assert.DoesNotContain(banned, src);
        // Clean room: no byte pattern of anyone's in any string literal.
        foreach (Match lit in Regex.Matches(src, "\"[^\"\\n]*\""))
            Assert.DoesNotMatch(@"([0-9A-F?]{2} ){4,}", lit.Value);
    }

    [Fact]
    [Trait("Plan", "AC-studio-freeze-10")]
    [Trait("Plan", "EDGE-studio-freeze-17")]
    [Trait("Plan", "EDGE-studio-freeze-69")]
    public void Update_fault_releases_once_then_latches_unavailable()
    {
        // The toggle says "unavailable" from then on: State maps the latch, whatever the policy last answered.
        Assert.Contains("public FreezeState State => faulted ? FreezeState.Unavailable : policy.State;", Freeze);
        var update = OnUpdate;
        // Latched: nothing more runs (and logs) once faulted.
        Assert.StartsWith("if (faulted) return;", Regex.Replace(update.TrimStart('{'), @"//[^\n]*", "").TrimStart());
        var c = update[update.LastIndexOf("catch (Exception", StringComparison.Ordinal)..];
        Assert.Contains("try { ReleaseNow(); }", c);
        // A release that throws is never retried through the game: the ledger is dropped.
        Assert.True(At(c, "try { ReleaseNow(); }") < At(c, "slots.Drop();"));
        Assert.Contains("faulted = true;", c);
        Assert.Contains("BusyReason = null;", c);
        Assert.Contains("Available, unknown, slot0Learned", update);   // Ready is Available, which includes !faulted
    }

    [Fact]
    [Trait("Plan", "AC-studio-freeze-3")]
    [Trait("Plan", "EDGE-studio-freeze-10")]
    [Trait("Plan", "EDGE-studio-freeze-11")]
    [Trait("Plan", "EDGE-studio-freeze-12")]
    public void ReleaseNow_writes_back_only_for_the_same_character_outside_zone_transitions()
    {
        var release = Body(Freeze, "private void ReleaseNow(");
        Assert.Contains("if (chara != null && (nint)chara == ledgerOwner && !BetweenAreas)", release);
        Assert.True(At(release, "slots.Release(") < At(release, "else slots.Drop();"));
        var update = OnUpdate;
        Assert.True(At(update, "if (ledgerOwner != 0 && ledgerOwner != (nint)chara) slots.Drop();")
                  < At(update, "slots.Hold("));
    }

    [Fact]
    [Trait("Plan", "AC-studio-freeze-3")]
    [Trait("Plan", "EDGE-studio-freeze-20")]
    [Trait("Plan", "EDGE-studio-freeze-34")]
    public void Dispose_releases_on_thread_or_through_a_bounded_hop()
    {
        var dispose = Body(Freeze, "public void Dispose(");
        Assert.True(At(dispose, "framework.Update -= OnUpdate;") < At(dispose, "try"));
        // No look at the ledger itself off the framework thread; only the volatile holding flag.
        Assert.DoesNotContain("Held", dispose);
        Assert.Contains("if (!holding) return;", dispose);
        Assert.Contains("if (slots.Held.Count == 0)", Body(Freeze, "private void ReleaseNow("));
        Assert.Contains("if (framework.IsInFrameworkUpdateThread) ReleaseNow();", dispose);
        Assert.Contains("framework.RunOnFrameworkThread(() => ReleaseNow()).Wait(TimeSpan.FromMilliseconds(500))", dispose);
        Assert.Contains("catch (Exception", dispose);
    }

    [Fact]
    [Trait("Plan", "EDGE-studio-freeze-51")]
    [Trait("Plan", "EDGE-studio-freeze-52")]
    [Trait("Plan", "EDGE-studio-freeze-61")]
    [Trait("Plan", "EDGE-studio-freeze-62")]
    [Trait("Plan", "EDGE-studio-freeze-63")]
    public void Observe_gets_the_last_freezable_and_learning_resets_on_owner_change()
    {
        var update = OnUpdate;
        Assert.Contains("slots.Observe(access, lastFreezable, now)", update);
        Assert.Contains("lastFreezable = policy.Freezable;", update);
        Assert.Contains("FreezeOwner.Next(owner, (nint)chara, RaceSexOf(chara), (nint)chara->GameObject.DrawObject)", update);
        Assert.Contains("if (change == OwnerChange.Reset) slots.ForgetLearned();", update);
        Assert.Contains("else if (change == OwnerChange.Reseed) slots.Reseed();", update);
        Assert.Single(Regex.Matches(Freeze, @"slots\.ForgetLearned\(\)"));
        // The race/sex is read only while a Human is drawn; anything else is unknown (null), never 0.
        var raceSex = Body(Freeze, "private static ushort? RaceSexOf(");
        Assert.Contains("GetModelType() == CharacterBase.ModelType.Human ? ((Human*)cb)->RaceSexId : null", raceSex);
        int step = At(update, "policy.Step(");
        Assert.True(At(update, "unknown = slots.Observe(") < step);
        Assert.True(At(update, "slots.UnknownRestart(access)") < step);
        Assert.True(At(update, "slot0Learned = slots.IsLearned(0,") < step);
        Assert.Contains("bool entry = before != FreezeState.Frozen;", update);
        Assert.Contains("slots.Hold(new SequencerSlots(&chara->Timeline.TimelineSequencer), entry, nowMs: now)", update);
        // Only a plain let-go opens the induced-swap window after a write-back; an action's release never does.
        Assert.Contains("plainLetGo ? Environment.TickCount64 : long.MinValue / 2", Body(Freeze, "private void ReleaseNow("));
        Assert.Contains("ReleaseNow(plainLetGo: state is FreezeState.Idle or FreezeState.Off);", update);
        Assert.Single(Regex.Matches(Freeze, @"plainLetGo: "));
    }

    [Fact]
    [Trait("Plan", "EDGE-studio-freeze-47")]
    public void Want_resets_and_update_counts_the_lapse()
    {
        Assert.Contains("updatesSinceWant = 0;", Body(Freeze, "public void Want("));
        var update = OnUpdate;
        Assert.Contains("if (updatesSinceWant < int.MaxValue) updatesSinceWant++;", update);
        Assert.Contains("new FreezeInputs(Enabled, lastWantMs, updatesSinceWant,", update);
    }

    [Fact]
    [Trait("Plan", "EDGE-studio-freeze-46")]
    public void Freeze_counters_reset_on_freeze_entry()
    {
        Assert.Contains("if (entry) writes = restores = restoreSkipped = 0;", OnUpdate);
        Assert.Contains("lastRelease = r;", Body(Freeze, "private void ReleaseNow("));
    }

    [Fact]
    [Trait("Plan", "AC-studio-freeze-6")]
    [Trait("Plan", "EDGE-studio-freeze-28")]
    [Trait("Plan", "EDGE-studio-freeze-38")]
    public void Snowflake_toggle_draws_ButtonText_under_its_own_id_and_saves()
    {
        var panel = Source("Gui/PartsPanel.cs");
        var toggle = Body(panel, "private void DrawFreezeToggle(");
        Assert.True(At(toggle, "ImRaii.PushId(\"studioFreeze\")")
                  < At(toggle, "ButtonText(Strings.Parts.FreezeCharacter)"));
        Assert.DoesNotMatch(@"IconButtonWithText\([^)]*,\s*Strings\.Parts\.FreezeCharacter\s*,", toggle);
        Assert.Contains("matcapConfig.StudioFreezeCharacter = !matcapConfig.StudioFreezeCharacter;", toggle);
        Assert.Contains("matcapConfig.Save();", toggle);
        Assert.Contains("if (!showModelView) DrawFreezeToggle();", panel);
        Assert.Contains("ImGui.CalcTextSize(ButtonText(Strings.Parts.FreezeCharacter))", Body(panel, "private static float ToolColumnWidth("));
    }

    [Fact]
    [Trait("Plan", "AC-studio-freeze-9")]
    [Trait("Plan", "EDGE-studio-freeze-25")]
    public void EndSession_clears_the_freeze_override_first()
    {
        var harness = Source("Gui/PartsPanel.Harness.cs");
        var end = Body(harness, "StudioPutBack IStudioHarness.EndSession(");
        var first = end.TrimStart('{').TrimStart().Split('\n')[0];
        Assert.StartsWith("freeze.SessionOverride = null;", first);
    }

    [Fact]
    [Trait("Plan", "EDGE-studio-freeze-36")]
    public void StudioDrag_kind_map_is_unchanged()
    {
        var harness = Source("Gui/PartsPanel.Harness.cs");
        var drag = Body(harness, "StudioDragOutcome IStudioHarness.Drag(");
        Assert.Contains("request.Kind switch { \"rotate\" => Tool.Rotate, \"scale\" => Tool.Scale, _ => Tool.Move }", drag);
        Assert.DoesNotContain("HarnessTool", drag);
    }
}
