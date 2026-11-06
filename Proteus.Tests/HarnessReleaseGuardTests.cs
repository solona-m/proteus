using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// The QA test harness stays out of every published build: the build switch is forced off under CI, and both release
/// workflows check the DLL — loose and inside latest.zip — before the release step. Text checks over the repo's own
/// files, comments ignored (the "why" comments name what the files never do). Always compiled.
/// </summary>
public class HarnessReleaseGuardTests
{
    private static string RepoRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "proteus.slnx"))) dir = dir.Parent;
            Assert.True(dir != null, "run from the checkout: proteus.slnx not found");
            return dir!.FullName;
        }
    }

    private static string[] Lines(params string[] path)
        => File.ReadAllText(Path.Combine([RepoRoot, .. path])).Replace("\r\n", "\n").Split('\n');

    private static int IndexOf(string[] code, string needle)
        => Array.FindIndex(code, l => l.Contains(needle, StringComparison.Ordinal));

    [Theory]
    [InlineData("release.yml")]
    [InlineData("testing-release.yml")]
    public void BothReleaseWorkflows_GuardLooseAndZippedDll(string workflow)
    {
        // [AC-qa-harness-1] [EDGE-qa-harness-2] The guard reads the loose DLL and the zipped one, fails on the
        // harness's namespace or a zip without the DLL, and sits after Package and before the release is made.
        var lines = SupplyChainTests.NonCommentLines(Lines(".github", "workflows", workflow)).ToArray();
        int build = IndexOf(lines, "dotnet build Proteus/Proteus.csproj -c Release");
        int package = IndexOf(lines, "- name: Package");
        int guard = IndexOf(lines, "- name: Guard against the test harness");
        int release = IndexOf(lines, "softprops/action-gh-release@");
        Assert.True(build >= 0 && package > build, $"{workflow}: Build then Package");
        Assert.True(guard > package, $"{workflow}: the harness guard must come after Package");
        Assert.True(release > guard, $"{workflow}: the harness guard must come before the release step");

        var step = lines.Skip(guard).TakeWhile((l, i) => i == 0 || !l.TrimStart().StartsWith("- name:", StringComparison.Ordinal)).ToList();
        Assert.Contains(step, l => l.Contains("'Proteus.TestHarness'", StringComparison.Ordinal));
        // [AC-binding-reapply-13] [EDGE-binding-reapply-36] A second marker: a harness-only member outside the namespace.
        Assert.Contains(step, l => l.Contains("'HarnessSuppressApplySignals'", StringComparison.Ordinal));
        Assert.Contains(step, l => l.Contains(@"Proteus\bin\Release\win-x64\Proteus.dll", StringComparison.Ordinal));
        Assert.Contains(step, l => l.Contains("latest.zip", StringComparison.Ordinal) && l.Contains("OpenRead", StringComparison.Ordinal));
        Assert.Contains(step, l => l.Contains("has no Proteus.dll", StringComparison.Ordinal));
        Assert.Equal(3, step.Count(l => l.Contains("exit 1", StringComparison.Ordinal)));
        Assert.DoesNotContain(step, l => l.TrimStart().StartsWith("uses:", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Plan", "AC-binding-reapply-13")]
    [Trait("Plan", "EDGE-binding-reapply-51")]
    public void SecondMarker_OnlyInsideHarnessGuards()
    {
        // The guard's second marker may only ever match a build that compiled the harness in: outside
        // Proteus/TestHarness/ (removed from CI builds) it appears only between #if PROTEUS_TEST_HARNESS and #endif.
        const string marker = "HarnessSuppressApplySignals";
        var proteus = Path.Combine(RepoRoot, "Proteus");
        var harness = Path.Combine(proteus, "TestHarness") + Path.DirectorySeparatorChar;
        int seen = 0;
        foreach (var file in Directory.EnumerateFiles(proteus, "*.cs", SearchOption.AllDirectories))
        {
            if (file.StartsWith(harness, StringComparison.OrdinalIgnoreCase)) continue;
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) continue;
            // One entry per open #if: whether its current branch is compiled only with the harness.
            var stack = new System.Collections.Generic.Stack<bool>();
            foreach (var line in File.ReadAllLines(file))
            {
                var t = line.Trim();
                if (t.StartsWith("#if", StringComparison.Ordinal))
                    stack.Push(System.Text.RegularExpressions.Regex.IsMatch(t, @"^#if\s+PROTEUS_TEST_HARNESS\s*(//.*)?$"));
                else if (t.StartsWith("#elif", StringComparison.Ordinal) || t.StartsWith("#else", StringComparison.Ordinal))
                { if (stack.Count > 0) { stack.Pop(); stack.Push(false); } }
                else if (t.StartsWith("#endif", StringComparison.Ordinal) && stack.Count > 0) stack.Pop();
                if (!line.Contains(marker, StringComparison.Ordinal)) continue;
                seen++;
                Assert.True(stack.Contains(true), $"{Path.GetRelativePath(RepoRoot, file)} names {marker} outside #if PROTEUS_TEST_HARNESS");
            }
        }
        Assert.True(seen > 0, $"{marker} is defined nowhere: the release guard's second marker would never match");
    }

    [Fact]
    public void BuildSwitch_ForcedOffUnderCi_AndReported()
    {
        // [AC-qa-harness-1] [EDGE-qa-harness-1] [EDGE-qa-harness-3] On by default, then off under CI or GITHUB_ACTIONS
        // (the later assignment wins); both projects gate the define and the folder on it; the build says which.
        var props = string.Join("\n", Lines("Directory.Build.props"));
        int on = props.IndexOf("<ProteusTestHarness Condition=\"'$(ProteusTestHarness)' == ''\">true</ProteusTestHarness>", StringComparison.Ordinal);
        int off = props.IndexOf("<ProteusTestHarness Condition=\"'$(CI)' == 'true' OR '$(GITHUB_ACTIONS)' == 'true'\">false</ProteusTestHarness>", StringComparison.Ordinal);
        Assert.True(on >= 0 && off > on, "Directory.Build.props: default on, then forced off under CI");

        foreach (var project in new[] { Path.Combine("Proteus", "Proteus.csproj"), Path.Combine("Proteus.Tests", "Proteus.Tests.csproj") })
        {
            var text = string.Join("\n", Lines(project));
            Assert.Contains("<PropertyGroup Condition=\"'$(ProteusTestHarness)' == 'true'\">", text);
            Assert.Contains("$(DefineConstants);PROTEUS_TEST_HARNESS", text);
            Assert.Contains("<ItemGroup Condition=\"'$(ProteusTestHarness)' != 'true'\">", text);
            Assert.Contains("<Compile Remove=\"TestHarness\\**\" />", text);
        }
        var plugin = string.Join("\n", Lines("Proteus", "Proteus.csproj"));
        Assert.Contains("Proteus test harness: compiled IN", plugin);
        Assert.Contains("Proteus test harness: compiled OUT", plugin);
    }
}
