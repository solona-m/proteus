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
}
