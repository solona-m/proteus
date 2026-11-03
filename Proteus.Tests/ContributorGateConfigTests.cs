using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// The contributor PR gate's committed config: the ai-workflow plugin turned on for contributors, the precise `if`
/// filters that start the hook, the guide, and the files' line endings. No shell needed.
/// </summary>
public class ContributorGateConfigTests
{
    internal static string RepoRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "proteus.slnx"))) dir = dir.Parent;
            Assert.True(dir != null, "run from the checkout: proteus.slnx not found");
            return dir!.FullName;
        }
    }

    internal const string GuardedCommand =
        "[ ! -f \"${CLAUDE_PROJECT_DIR}/.claude/hooks/pr-gate.sh\" ] || sh \"${CLAUDE_PROJECT_DIR}/.claude/hooks/pr-gate.sh\" || true";

    private static JsonElement Settings()
        => JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot, ".claude", "settings.json"))).RootElement.Clone();

    [Fact]
    public void Settings_DeclareMarketplaceAndEnablePlugin()
    {
        // [AC-contributor-gate-1]
        var s = Settings();
        var source = s.GetProperty("extraKnownMarketplaces").GetProperty("ai-workflow").GetProperty("source");
        Assert.Equal(["source", "repo"], source.EnumerateObject().Select(p => p.Name));
        Assert.Equal("github", source.GetProperty("source").GetString());
        Assert.Equal("cunhaax/ai-workflow", source.GetProperty("repo").GetString());
        Assert.True(s.GetProperty("enabledPlugins").GetProperty("ai-workflow@ai-workflow").GetBoolean());
        Assert.Equal(["Bash(git push --no-verify)", "Bash(git push --no-verify:*)",
                      "Bash(SKIP_REVIEW_GATE=1 git push)", "Bash(SKIP_REVIEW_GATE=1 git push:*)"],
                     s.GetProperty("permissions").GetProperty("deny").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public void Settings_RegisterPreciseIfFilters()
    {
        // [AC-contributor-gate-10] [EDGE-contributor-gate-11]
        var hooks = Settings().GetProperty("hooks");
        Assert.Equal(["PreToolUse"], hooks.EnumerateObject().Select(p => p.Name));
        var entries = hooks.GetProperty("PreToolUse").EnumerateArray().ToList();
        Assert.Equal(2, entries.Count);
        foreach (var (entry, tool) in entries.Zip(new[] { "Bash", "PowerShell" }))
        {
            Assert.Equal(tool, entry.GetProperty("matcher").GetString());
            var list = entry.GetProperty("hooks").EnumerateArray().ToList();
            Assert.Equal([$"{tool}(gh pr create *)", $"{tool}(gh pr new *)"], list.Select(h => h.GetProperty("if").GetString()));
            foreach (var h in list)
            {
                // Exactly these keys: an onFailure or shell key could turn a script failure into a block in every session.
                Assert.Equal(["type", "if", "timeout", "command"], h.EnumerateObject().Select(p => p.Name));
                Assert.Equal("command", h.GetProperty("type").GetString());
                Assert.Equal(10, h.GetProperty("timeout").GetInt32());
                Assert.Equal(GuardedCommand, h.GetProperty("command").GetString());
            }
        }
        string text = File.ReadAllText(Path.Combine(RepoRoot, ".claude", "settings.json"));
        Assert.DoesNotContain("*gh*pr*", text);
        Assert.DoesNotContain("core.hooksPath", text);
        Assert.DoesNotContain("githooks", text);
    }

    [Fact]
    public void ContributingGuide_DescribesContributorPath()
    {
        // [AC-contributor-gate-7]
        string guide = File.ReadAllText(Path.Combine(RepoRoot, "docs", "agent-rules", "contributing-with-claude.md"));
        foreach (var s in new[]
                 {
                     "/feature", "code-critic", "scripts/review-ok.sh", "/init-workflow", "githooks",
                     "docs/agent-rules/adversarial-qa.md", "/proteus harness on", @"E:\repos\Proteus", "your own",
                     "sh .claude/hooks/pr-ready.sh", "ran:", "skipped:", "blocked:",
                     "gh pr create --base test", "Rule 4", "only when your user asks",
                     "/plugin marketplace add cunhaax/ai-workflow", "/plugin install ai-workflow@ai-workflow", "npx",
                     "not enforcement", ".claude/settings.json", "Git for Windows",
                     // [AC-pr-gate-advisory-9] A recommendation, never a requirement.
                     "recommended for non-trivial changes", "Small changes and contributors not using Claude Code don't need it",
                     "never blocks", "silences", "the owner",
                 })
            Assert.True(guide.Contains(s, StringComparison.Ordinal), $"contributing-with-claude.md must mention '{s}'");
        foreach (var s in new[] { "blocks it", "What the gate is", "required" })
            Assert.False(guide.Contains(s, StringComparison.OrdinalIgnoreCase), $"contributing-with-claude.md must not say '{s}'");
    }

    [Fact]
    public void AgentsMd_LinksGuideListsCommandAndSensitiveArea()
    {
        // [AC-contributor-gate-7]
        var lines = File.ReadAllLines(Path.Combine(RepoRoot, "AGENTS.md"));
        string all = string.Join("\n", lines);
        Assert.Contains("docs/agent-rules/contributing-with-claude.md", all);
        int commands = Array.IndexOf(lines, "## Commands");
        int architecture = Array.IndexOf(lines, "## Architecture");
        // [AC-pr-gate-advisory-9] Optional, and it silences a recommendation.
        var command = Assert.Single(lines[commands..architecture], l => l.Contains(".claude/hooks/pr-ready.sh", StringComparison.Ordinal));
        Assert.Contains("optional", command);
        Assert.Contains("silences", command);
        Assert.DoesNotContain("required", command);
        Assert.DoesNotContain("before a PR:", command);
        int sensitive = Array.FindIndex(lines, l => l.StartsWith("## Sensitive Areas", StringComparison.Ordinal));
        int next = Array.FindIndex(lines, sensitive + 1, l => l.StartsWith("## ", StringComparison.Ordinal));
        Assert.Contains(lines[sensitive..next], l => l.StartsWith("- ", StringComparison.Ordinal)
            && l.Contains(".claude/settings.json") && l.Contains(".claude/hooks/")
            && l.Contains("every agent session") && l.Contains("checked out")
            && l.Contains("latency") && l.Contains("never blocks") && !l.Contains("required"));
    }

    [Fact]
    public void CodeCriticMd_NamesReminderAdvisory()
    {
        // [AC-pr-gate-advisory-9]
        var lines = File.ReadAllLines(Path.Combine(RepoRoot, "docs", "agent-rules", "code-critic.md"));
        int i = Array.FindIndex(lines, l => l.Contains("PrGateTests", StringComparison.Ordinal));
        Assert.True(i > 0, "code-critic.md must name PrGateTests");
        Assert.Contains("advisory", lines[i - 1] + " " + lines[i]);
    }

    [Fact]
    public void GateScript_NeverExitsNonZero()
    {
        // [EDGE-pr-gate-advisory-3] Exit 2 would block, so the script only ever exits 0 (the settings tail covers crashes).
        var code = SupplyChainTests.NonCommentLines(File.ReadAllLines(Path.Combine(RepoRoot, ".claude", "hooks", "pr-gate.sh"))).ToList();
        Assert.DoesNotContain(code, l => System.Text.RegularExpressions.Regex.IsMatch(l, @"\bexit\b(?!\s+0\b)"));
        Assert.DoesNotContain(code, l => System.Text.RegularExpressions.Regex.IsMatch(l, @"\bset\s+-e"));
    }

    [Fact]
    public void Scripts_AreLfAndRecordIgnored()
    {
        // [AC-contributor-gate-8] [EDGE-contributor-gate-12]
        foreach (var name in new[] { "pr-gate.sh", "pr-ready.sh" })
            Assert.DoesNotContain((byte)'\r', File.ReadAllBytes(Path.Combine(RepoRoot, ".claude", "hooks", name)));
        Assert.Contains(SupplyChainTests.NonCommentLines(File.ReadAllLines(Path.Combine(RepoRoot, ".gitattributes"))),
                        l => l.Trim() == "*.sh text eol=lf");
        Assert.Contains(File.ReadAllLines(Path.Combine(RepoRoot, ".gitignore")), l => l.Trim() == ".pr-ready*");
        foreach (var name in new[] { ".pr-ready", ".pr-ready.tmp.123" })
        {
            var psi = new System.Diagnostics.ProcessStartInfo("git", $"check-ignore -q {name}")
            {
                WorkingDirectory = RepoRoot, UseShellExecute = false,
            };
            using var p = System.Diagnostics.Process.Start(psi)!;
            p.WaitForExit();
            Assert.True(p.ExitCode == 0, $"{name} must be gitignored");
        }
    }
}
