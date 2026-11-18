using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Proteus.TestHarness;

namespace Proteus.Tests.TestHarness;

/// <summary>The fake game's side of the worn-refit trigger (<see cref="AutoRefitNowJob"/>).</summary>
internal sealed partial class FakeGame
{
    /// <summary>Auto refit on with a preferred body, nothing working.</summary>
    public bool AutoRefitCanRun { get; set; } = true;

    /// <summary>The mod folders a drawn piece resolves into.</summary>
    public HashSet<string> WornFrom { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What <see cref="RunAutoRefitNowFor"/> answers, given the mod root; by default one enqueued top, which also
    /// "writes" into the mod and changes the preference, as a real forced refit does.</summary>
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
        PreferenceJson = "{\"BodyDir\":\"Rue\",\"Before\":1}";
        return [("_top", "chara/equipment/e0628/model/c0201e0628_top.mdl", "enqueued")];
    }

    public bool CanRunAutoRefitNow() => AutoRefitCanRun;

    public bool IsWornFrom(string modRoot) => WornFrom.Contains(Path.GetFileName(modRoot));

    public IReadOnlyList<(string Slot, string GamePath, string Outcome)>? RunAutoRefitNowFor(string modRoot)
    {
        RunNowAsked.Add(Path.GetFileName(modRoot));
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
