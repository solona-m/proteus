using System;
using System.Linq;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// The QA test harness is in a local build and out of every CI build. Always compiled, so it runs in CI too, and it
/// is the documented check command (AGENTS.md) that a local build — the one copied to the game — has the harness:
/// <c>dotnet test Proteus.Tests/Proteus.Tests.csproj -c Release --filter "FullyQualifiedName~HarnessBuildFlagTests"</c>.
/// </summary>
public class HarnessBuildFlagTests
{
    private static bool UnderCi
        => string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void HarnessTypes_MatchRuntimeCiEnvironment()
    {
        // [AC-harness-equip-10] [EDGE-harness-equip-37] the equip/hold types are harness types too: compiled out on CI.
        // [AC-qa-harness-1] [EDGE-qa-harness-1] Judged by the environment the tests run in, not by the define: a
        // global -p:ProteusTestHarness=true in CI would turn both on together, and this would still fail.
        var harness = typeof(Plugin).Assembly.GetTypes()
                                    .Where(t => t.Namespace?.StartsWith("Proteus.TestHarness", StringComparison.Ordinal) == true)
                                    .ToList();
        if (UnderCi)
            Assert.True(harness.Count == 0, $"a CI build must compile the QA test harness out, but Proteus.dll has {harness.Count} of its types");
        else
            Assert.True(harness.Count > 0, "a local build must compile the QA test harness in (is CI=true set in this shell?)");
    }

    [Fact]
    [Trait("Plan", "AC-binding-reapply-13")]
    [Trait("Plan", "EDGE-binding-reapply-36")]
    public void DesignBindingHarnessMembers_MatchRuntimeCiEnvironment()
    {
        // The harness-only DesignBindingService partial (namespace Proteus.Services, so the type check above can't see it).
        const System.Reflection.BindingFlags all = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static
                                                 | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
        var members = typeof(Services.DesignBindingService).GetMembers(all)
                                                           .Where(m => m.Name.StartsWith("Harness", StringComparison.Ordinal))
                                                           .Select(m => m.Name).ToList();
        var types = typeof(Plugin).Assembly.GetTypes().Where(t => t.Name.StartsWith("HarnessBinding", StringComparison.Ordinal)
                                                               || t.Name == "HarnessLiveSnapshot").ToList();
        if (UnderCi)
        {
            Assert.True(members.Count == 0, $"a CI build must compile the harness partial out, but DesignBindingService has {string.Join(", ", members)}");
            Assert.True(types.Count == 0, $"a CI build must compile the harness records out, but has {string.Join(", ", types.Select(t => t.Name))}");
        }
        else
        {
            Assert.Contains("HarnessSuppressApplySignals", members);
            Assert.NotEmpty(types);
        }
    }

    /// <summary>A type's members named Harness… in any case (a lower-case backing field included).</summary>
    private static System.Collections.Generic.List<string> HarnessMembers(Type type)
    {
        const System.Reflection.BindingFlags all = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static
                                                 | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                                                 | System.Reflection.BindingFlags.DeclaredOnly;
        // No exemptions: a bodiless partial declaration (HarnessEnabled, HarnessNoteRefit) is erased from metadata, so under
        // CI either name appears only if a body leaked out of its #if.
        return type.GetMembers(all).Where(m => m.Name.StartsWith("Harness", StringComparison.OrdinalIgnoreCase))
                   .Select(m => m.Name).ToList();
    }

    private static void AssertPartial(Type type, string expected)
    {
        var members = HarnessMembers(type);
        if (UnderCi)
            Assert.True(members.Count == 0, $"a CI build must compile the harness partial out, but {type.Name} has {string.Join(", ", members)}");
        else
            Assert.Contains(expected, members);
    }

    [Fact]
    [Trait("Plan", "EDGE-idle-disk-io-43")]
    [Trait("Plan", "EDGE-idle-disk-io-58")]
    public void CompositorHarnessMembers_MatchRuntimeCiEnvironment()
        // [EDGE-idle-disk-io-43] [EDGE-idle-disk-io-58]
        => AssertPartial(typeof(Services.CompositorService), "HarnessRearmBoot");

    [Fact]
    [Trait("Plan", "EDGE-idle-disk-io-43")]
    [Trait("Plan", "EDGE-idle-disk-io-58")]
    public void SidecarDiscoveryHarnessMembers_MatchRuntimeCiEnvironment()
        // [EDGE-idle-disk-io-43] [EDGE-idle-disk-io-58]
        => AssertPartial(typeof(Services.SidecarDiscoveryService), "HarnessHasStaged");

    [Fact]
    [Trait("Plan", "EDGE-idle-disk-io-43")]
    [Trait("Plan", "EDGE-idle-disk-io-58")]
    public void AutoRefitHarnessMembers_MatchRuntimeCiEnvironment()
    {
        // [EDGE-idle-disk-io-43] [EDGE-idle-disk-io-58] The lower-case backing fields count too.
        AssertPartial(typeof(Services.AutoRefitWatcher), "HarnessOverride");
        if (!UnderCi) Assert.Contains("harnessOverride", HarnessMembers(typeof(Services.AutoRefitWatcher)));
    }
}
