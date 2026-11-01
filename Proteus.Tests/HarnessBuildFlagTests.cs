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
}
