using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Proteus.Services;
using Proteus.TestHarness;

namespace Proteus.Tests.TestHarness;

/// <summary>The design-binding side of <see cref="FakeGame"/>: scriptable designs, Glamourer state, live rows and
/// permanent settings, with every call logged in <c>Order</c>.</summary>
internal sealed partial class FakeGame
{
    public Dictionary<Guid, string> DesignNames { get; } = new();
    public Dictionary<Guid, JObject> DesignJsons { get; } = new();
    public JObject GlamourerNow { get; set; } = GlamState(("Body", 100), ("Legs", 101));

    /// <summary>What applying a design does to the Glamourer state and the permanent settings (a restore's writes).</summary>
    public Dictionary<Guid, Action> ApplyEffects { get; } = new();
    public string? ApplyAnswer { get; set; }
    public Action? BeforeApply { get; set; }
    public int ApplyCalls;
    private long designApplied;
    public long DesignAppliedCount => designApplied;

    public string? RestoreAnswer { get; set; }
    public Action? AfterRestore { get; set; }
    public List<(JObject State, List<string> Skip, bool SkipGlasses)> Restores { get; } = [];

    public DesignBindingService.Carriers CarriersValue { get; set; } = new(null, null);
    public long EchoMs { get; set; }
    public int Quiesces;
    public HarnessBindingView? BindingViewValue { get; set; }
    public bool Pinned { get; set; }
    public Dictionary<string, BindingEditState> EditStates { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<(string, string?, string?), List<ColorTableRowPreset>> Live { get; } = new();
    public HarnessLiveSnapshot LiveNow { get; set; } = new(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"), null, null, "{}");
    public List<HarnessLiveSnapshot> LiveRestores { get; } = [];
    public HashSet<string> Proteus { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, PermanentSetting> Permanent { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool PermanentReadable { get; set; } = true;
    public string? PermanentWriteAnswer { get; set; }
    public List<(string Mod, PermanentSetting Setting)> PermanentWrites { get; } = [];
    public bool Idle { get; set; } = true;
    public Func<bool>? IdleProbe { get; set; }
    public List<string> AutoWriters { get; set; } = [];

    /// <summary>A Glamourer-shaped state with the given gear.</summary>
    public static JObject GlamState(params (string Slot, ulong Item)[] slots)
    {
        var eq = new JObject();
        foreach (var (slot, item) in slots) eq[slot] = new JObject { ["ItemId"] = item, ["Apply"] = true };
        return new JObject { ["Equipment"] = eq };
    }

    public IReadOnlyDictionary<Guid, string>? Designs() { Fw(nameof(Designs)); return GlamourerUp ? new Dictionary<Guid, string>(DesignNames) : null; }

    public JObject? DesignJson(Guid id) { Fw(nameof(DesignJson)); return DesignJsons.TryGetValue(id, out var d) ? d : GlamourerUp ? new JObject() : null; }

    public JObject? GlamourerState() { Fw(nameof(GlamourerState)); return GlamourerUp && LoggedIn ? (JObject)GlamourerNow.DeepClone() : null; }

    public string? ApplyDesign(Guid id)
    {
        Fw(nameof(ApplyDesign));
        ApplyCalls++;
        BeforeApply?.Invoke();
        if (ApplyAnswer != null) return ApplyAnswer;
        if (ApplyEffects.TryGetValue(id, out var effect)) effect();
        designApplied++;
        return null;
    }

    public string? RestoreGlamourerState(JObject state, IReadOnlyCollection<string> skipSlots, bool skipGlasses)
    {
        Fw(nameof(RestoreGlamourerState));
        Restores.Add(((JObject)state.DeepClone(), skipSlots.ToList(), skipGlasses));
        if (RestoreAnswer != null) return RestoreAnswer;
        var next = (JObject)state.DeepClone();
        foreach (var slot in skipSlots)
            if (GlamourerNow["Equipment"]?[slot] is { } keep) next["Equipment"]![slot] = keep.DeepClone();
        GlamourerNow = next;
        AfterRestore?.Invoke();
        return null;
    }

    public DesignBindingService.Carriers CarriersNow() { Fw(nameof(CarriersNow)); return CarriersValue; }
    public long EchoMsLeft() { Fw(nameof(EchoMsLeft)); return EchoMs; }
    public void QuiesceBinding() { Fw(nameof(QuiesceBinding)); Quiesces++; }

    public HarnessBindingView BindingView(string? mod, string? group, string? option)
    {
        Fw(nameof(BindingView));
        return BindingViewValue ?? new HarnessBindingView(true, false, false, false, EchoMs, LiveNow.Active, "Secret Design",
                                                          ["Equipment/Body/Item"], "ABC", [], null);
    }

    public IReadOnlyList<string>? DesignFields(Guid id) { Fw(nameof(DesignFields)); return ["Equipment/Body/Item"]; }
    public bool IsBound(Guid id) { Fw(nameof(IsBound)); return id == LiveNow.Active; }
    public bool PresetsPinned() { Fw(nameof(PresetsPinned)); return Pinned; }

    public BindingEditState EditState(string modDir)
    {
        Fw(nameof(EditState));
        return EditStates.TryGetValue(modDir, out var s) ? s : new BindingEditState(true, true, true, false);
    }

    public List<ColorTableRowPreset>? LiveRows(string modDir, string? group, string? option)
    {
        Fw(nameof(LiveRows));
        return Live.TryGetValue((modDir, group, option), out var r) ? r.Select(x => x.Clone()).ToList() : null;
    }

    public bool SetLiveRows(string modDir, string? group, string? option, List<ColorTableRowPreset> rows)
    {
        Fw(nameof(SetLiveRows));
        Live[(modDir, group, option)] = rows;
        return true;
    }

    public bool ClearLiveOption(string modDir, string? group, string? option)
    {
        Fw(nameof(ClearLiveOption));
        return Live.Remove((modDir, group, option));
    }

    public HarnessLiveSnapshot LiveSnapshot() { Fw(nameof(LiveSnapshot)); return LiveNow; }

    public void RestoreLive(HarnessLiveSnapshot snapshot)
    {
        Fw(nameof(RestoreLive));
        LiveRestores.Add(snapshot);
        LiveNow = snapshot;
    }

    public IReadOnlySet<string> ProteusDirs() { Fw(nameof(ProteusDirs)); return Proteus; }

    public IReadOnlyDictionary<string, PermanentSetting>? PermanentSettings(Guid collection)
    {
        Fw(nameof(PermanentSettings));
        return PermanentReadable ? new Dictionary<string, PermanentSetting>(Permanent, StringComparer.OrdinalIgnoreCase) : null;
    }

    public string? WritePermanent(Guid collection, string modDir, PermanentSetting setting)
    {
        Fw(nameof(WritePermanent));
        if (PermanentWriteAnswer != null) return PermanentWriteAnswer;
        PermanentWrites.Add((modDir, setting));
        Permanent[modDir] = setting;
        return null;
    }

    public bool CompositorIdle() { Fw(nameof(CompositorIdle)); return IdleProbe?.Invoke() ?? Idle; }

    public IReadOnlyList<string> AutoWritersOn() { Fw(nameof(AutoWritersOn)); return AutoWriters; }
}
