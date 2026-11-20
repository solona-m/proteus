using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Proteus.TestHarness;

namespace Proteus.Tests.TestHarness;

/// <summary>The fake game's side of the worn-refit trigger (<see cref="AutoRefitNowJob"/>).</summary>
internal sealed partial class FakeGame
{
    internal const string RefitTopPath = "chara/equipment/e0628/model/c0201e0628_top.mdl";

    /// <summary>Auto refit on with a preferred body, nothing working.</summary>
    public bool AutoRefitCanRun { get; set; } = true;

    /// <summary>Auto refit starts working right after the request's check, so the job's own re-check finds it busy.</summary>
    public bool BusyAfterRequest { get; set; }

    /// <summary>The mod folders a drawn piece resolves into.</summary>
    public HashSet<string> WornFrom { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What <see cref="RunAutoRefitNowFor"/> answers, given the mod root; by default one enqueued top, which also
    /// "writes" its refit into the mod (file and record), switches the mod's setting and changes the preference, as a real
    /// forced refit does.</summary>
    public Func<string, IReadOnlyList<(string Slot, string GamePath, string Outcome)>?> RunNowBody { get; set; }

    /// <summary>The mod roots <see cref="RunAutoRefitNowFor"/> was asked for.</summary>
    public List<string> RunNowAsked { get; } = [];

    /// <summary>The player collection's auto refit preference as the configuration would write it.</summary>
    public string? PreferenceJson { get; set; } = "{\"BodyDir\":\"Rue\"}";

    /// <summary>Times the preference was put back (and the configuration saved).</summary>
    public int PreferenceRestores { get; private set; }

    private IReadOnlyList<(string, string, string)>? DefaultRunNow(string modRoot)
    {
        File.WriteAllText(Path.Combine(modRoot, "chara", "model", "top.mdl"), "refitted");
        File.WriteAllText(Path.Combine(modRoot, "group_001_top size.json"),
                          "{\"Name\":\"Top Size\",\"Type\":\"Single\",\"Options\":[{\"Name\":\"Tre S\",\"Files\":{}}]}");
        Directory.CreateDirectory(Path.Combine(modRoot, "Proteus"));
        File.WriteAllText(Path.Combine(modRoot, "Proteus", "bodyretarget.json"),
                          "{\"Group\":\"\",\"Options\":[{\"Group\":\"Top Size\",\"InAuthorGroup\":true,\"Name\":\"Tre S\","
                          + $"\"GamePath\":\"{RefitTopPath}\",\"File\":\"Body Retarget/Tre S/top.mdl\",\"BodyMod\":\"Tre\","
                          + "\"From\":\"Omit · Medium\",\"To\":\"Omit · Small\",\"Auto\":true,"
                          + "\"Targets\":{\"_top\":\"files/common/3/c0201e0000_top.mdl\"},\"Seam\":\"Tre|Tre|top size/tre s.mdl\"}]}");
        Permanent[Path.GetFileName(modRoot)] = new PermanentSetting(true, 7);
        PreferenceJson = "{\"BodyDir\":\"Rue\",\"Before\":1}";
        return [("_top", RefitTopPath, "enqueued")];
    }

    public bool CanRunAutoRefitNow()
    {
        if (AutoRefitCanRun && BusyAfterRequest) Activity = new Proteus.Services.AutoRefitWatcher.ActivitySnapshot(true, 1, false, false);
        return AutoRefitCanRun;
    }

    public bool IsWornFrom(string modRoot) => WornFrom.Contains(Path.GetFileName(modRoot));

    public IReadOnlyList<(string Slot, string GamePath, string Outcome)>? RunAutoRefitNowFor(string modRoot)
    {
        lock (RunNowAsked) RunNowAsked.Add(Path.GetFileName(modRoot));
        return (RunNowBody ?? DefaultRunNow)(modRoot);
    }

    public string? AutoRefitPreferenceJson(Guid collection) => PreferenceJson;

    public bool RestoreAutoRefitPreference(Guid collection, string json)
    {
        PreferenceJson = json;
        PreferenceRestores++;
        return true;
    }
}
