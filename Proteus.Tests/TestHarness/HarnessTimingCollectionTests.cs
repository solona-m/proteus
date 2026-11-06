using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Xunit;

namespace Proteus.Tests.TestHarness;

/// <summary>
/// Who runs alone in <see cref="HarnessTimingCollection"/>: every class that starts a live harness or a fake framework
/// dispatcher, and the thread-hop tests. One left out races its short timeouts against the parallel suite again (#49);
/// anything else added puts more of the suite in single file for nothing.
/// </summary>
public class HarnessTimingCollectionTests
{
    private static readonly Type[] Expected =
    [
        typeof(DispatchTicketTests), typeof(HarnessBindingsHttpTests), typeof(HarnessBindingsTests), typeof(HarnessChangesHttpTests), typeof(HarnessChangesTests),
        typeof(HarnessHairPortTests), typeof(HarnessHostTests), typeof(HarnessJobsTests), typeof(HarnessRouterTests),
        typeof(HarnessServerTests), typeof(HarnessStudioRouteTests), typeof(OffThreadTests),
    ];

    [Fact]
    [Trait("Plan", "AC-issue-sweep-a-4")]
    [Trait("Plan", "EDGE-issue-sweep-a-13")]
    public void Every_class_with_live_harness_timing_runs_alone()
    {
        // By the attribute's constructor argument: CollectionAttribute keeps no Name property in xunit v2.
        var members = typeof(HarnessTimingCollection).Assembly.GetTypes()
            .Where(t => t.CustomAttributes.Any(a => a.AttributeType == typeof(CollectionAttribute)
                                                    && a.ConstructorArguments is [{ Value: HarnessTimingCollection.Name }]))
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(Expected.OrderBy(t => t.Name, StringComparer.Ordinal), members);

        var definition = typeof(HarnessTimingCollection).GetCustomAttribute<CollectionDefinitionAttribute>();
        Assert.NotNull(definition);
        Assert.True(definition.DisableParallelization);

        // Every test file that starts a harness or a fake dispatcher declares a member.
        var users = Directory.EnumerateFiles(TestsRoot(), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && Path.GetFileName(f) is not ("HarnessTestKit.cs" or "HarnessTimingCollection.cs"
                                                        or "HarnessTimingCollectionTests.cs"))
            .Where(f => File.ReadAllText(f) is var text
                        && (text.Contains("HarnessFixture") || text.Contains("FakeFrameworkDispatcher")))
            .Select(Path.GetFileNameWithoutExtension)
            .Order(StringComparer.Ordinal)
            .ToList();
        Assert.NotEmpty(users);
        Assert.All(users, u => Assert.Contains(Expected, t => t.Name == u));
    }

    /// <summary>The test project's folder, as the compiler saw this file.</summary>
    private static string TestsRoot([CallerFilePath] string self = "") => Path.GetDirectoryName(Path.GetDirectoryName(self)!)!;
}
