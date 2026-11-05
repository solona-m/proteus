using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// The adversarial-QA guide covers what QA needs to drive the in-game harness safely, AGENTS.md points to it, and the
/// ADR exists. Always compiled; when the harness is compiled in, the guide's routes are checked against the router's.
/// </summary>
public class HarnessDocsTests
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

    [Fact]
    public void AdversarialQaDoc_CoversRequiredTopics()
    {
        // [AC-qa-harness-10] [EDGE-qa-harness-3]
        string doc = File.ReadAllText(Path.Combine(RepoRoot, "docs", "agent-rules", "adversarial-qa.md"));
        foreach (var required in new[]
                 {
                     "test-harness.json", "127.0.0.1", "Authorization: Bearer", "/proteus harness on",
                     "/health", "/state", "/log", "/screenshot", "/actions/refit", "/actions/restore", "/jobs/",
                     "\"job\"", "\"backup\"", "oldest", "olderSessionBackups", "skippedNewer", "backup-incomplete",
                     "changedSinceJob", "restore-failed", "pre-restore snapshot", "test-harness/backups",
                     "delete them by hand", "autoRefit.busy", "phase", "idle", "scratchpad", "Blocker",
                     "compiled OUT", "CI=true", "HarnessBuildFlagTests", "must restore every mod you refitted",
                     "Never edit a mod's files yourself", "Screenshots show the game HUD",
                     // [AC-hair-port-qa-10] [EDGE-hair-port-qa-23]
                     "/hair-port", "/actions/hair-port", "hairPort", "hair-port-busy", "hair-not-in-mod", "hair-ported",
                     "check.owed", "check.running", "\"auto\"", "off in the player's collection", "UNVERIFIED",
                     "never blocks a restore",
                 })
            Assert.True(doc.Contains(required, StringComparison.Ordinal), $"adversarial-qa.md must mention '{required}'");
        Assert.Contains("Never put names", doc);

        string agents = File.ReadAllText(Path.Combine(RepoRoot, "AGENTS.md"));
        Assert.Contains("docs/agent-rules/adversarial-qa.md", agents);
        Assert.Contains("FullyQualifiedName~HarnessBuildFlagTests", agents);
        Assert.Contains("Proteus/TestHarness/", agents);
        Assert.True(File.Exists(Path.Combine(RepoRoot, "docs", "adr", "0002-qa-harness-compile-out-and-threat-model.md")));

#if PROTEUS_TEST_HARNESS
        // Every route the router serves has its row in the guide's table: "`GET /log?…`", "`POST /actions/refit`".
        foreach (var (method, path) in Proteus.TestHarness.HarnessRouter.Routes)
            Assert.True(doc.Contains($"`{method} {path}", StringComparison.Ordinal), $"adversarial-qa.md does not document {method} {path}");
#endif
    }
}
