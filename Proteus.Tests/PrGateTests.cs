using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using Xunit;

namespace Proteus.Tests;

/// <summary>Fixture folders for the gate's tests, made once: no git repo, a ready repo, and somewhere else.</summary>
public sealed class PrGateFixture : IDisposable
{
    internal TempDir NonGit { get; } = TempDir.New();
    internal TempDir Ready { get; } = TempDir.New(withSpace: true);
    internal TempDir EmptyProject { get; } = TempDir.New();
    internal TempDir Elsewhere { get; } = TempDir.New();
    internal string ReadyHead { get; } = "";

    public PrGateFixture()
    {
        if (PosixShell.Find() == null) return;
        PosixShell.Git(Ready.Path, "init", "-q");
        PosixShell.Git(Ready.Path, "commit", "-q", "--allow-empty", "-m", "first");
        ReadyHead = PosixShell.Git(Ready.Path, "rev-parse", "HEAD");
        File.WriteAllText(Path.Combine(Ready.Path, ".pr-ready"), $"commit={ReadyHead}\nqa=skipped: fixture\n");
    }

    public void Dispose()
    {
        NonGit.Dispose();
        Ready.Dispose();
        EmptyProject.Dispose();
        Elsewhere.Dispose();
    }
}

/// <summary>
/// The contributor PR gate's scripts, run with a real POSIX sh. Every case runs the script from a process working
/// directory other than the payload's cwd, so a result can only come from the payload. Detection cases use a folder
/// that is not a repo: a detected PR command there blocks with "cannot read HEAD".
/// </summary>
public class PrGateTests(PrGateFixture fx) : IClassFixture<PrGateFixture>
{
    private static readonly string HooksDir = Path.Combine(ContributorGateConfigTests.RepoRoot, ".claude", "hooks");
    private static readonly string GateScript = Path.Combine(HooksDir, "pr-gate.sh");
    private static readonly string ReadyScript = Path.Combine(HooksDir, "pr-ready.sh");

    private static readonly JsonSerializerOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly JsonSerializerOptions Indented = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, WriteIndented = true };

    /// <summary>A PreToolUse payload as Claude Code sends it.</summary>
    internal static string Payload(string command, string cwd, string tool = "Bash", string description = "Run it",
                                   bool indented = false)
    {
        var payload = new Dictionary<string, object>
        {
            ["session_id"] = "0b7a2c1e-0000-4000-8000-000000000001",
            ["transcript_path"] = "/home/u/.claude/projects/gh-pr-Proteus/x.jsonl",
            ["cwd"] = cwd,
            ["permission_mode"] = "default",
            ["hook_event_name"] = "PreToolUse",
            ["tool_name"] = tool,
            ["tool_input"] = new Dictionary<string, string> { ["command"] = command, ["description"] = description },
        };
        string json = JsonSerializer.Serialize(payload, indented ? Indented : Compact);
        // Indented output has no space before ':'; put one on both sides so the optional-whitespace rule is exercised.
        return indented ? json.Replace("\": ", "\" : ") : json;
    }

    private PosixShell.Result Gate(string stdin, string? processCwd = null, string? projectDir = null)
        => PosixShell.Run([GateScript], stdin, processCwd ?? fx.Elsewhere.Path,
                          new Dictionary<string, string?> { ["CLAUDE_PROJECT_DIR"] = projectDir ?? fx.EmptyProject.Path });

    private static void Allowed(PosixShell.Result r)
    {
        Assert.True(r.ExitCode == 0, $"expected exit 0, got {r.ExitCode}: {r.Stderr}");
        Assert.Equal("", r.Stdout);
        Assert.Equal("", r.Stderr);
    }

    internal const string FirstLine = "pr-gate: recommendation only; this gh pr create was not blocked ({0}).";

    private static readonly string[] NoPermissionKeys =
        ["permissionDecision", "permissionDecisionReason", "decision", "continue", "reason", "updatedInput"];

    /// <summary>
    /// A note, never a block: exit 0, empty stderr, and stdout exactly one JSON object with these keys in order and no
    /// permission decision anywhere. Returns (additionalContext, systemMessage).
    /// </summary>
    private static (string Context, string System) Noted(PosixShell.Result r, string reason)
    {
        Assert.True(r.ExitCode == 0, $"expected exit 0, got {r.ExitCode}: {r.Stderr}");
        Assert.Equal("", r.Stderr);
        Assert.True(r.Stdout.Length < 2000, $"note is {r.Stdout.Length} characters");
        Assert.True(r.Stdout.All(c => c < 128), "note must be ASCII");
        using var doc = JsonDocument.Parse(r.Stdout);
        var root = doc.RootElement;
        Assert.Equal(["systemMessage", "hookSpecificOutput"], root.EnumerateObject().Select(p => p.Name));
        var specific = root.GetProperty("hookSpecificOutput");
        Assert.Equal(["hookEventName", "additionalContext"], specific.EnumerateObject().Select(p => p.Name));
        Assert.Equal("PreToolUse", specific.GetProperty("hookEventName").GetString());
        foreach (var key in NoPermissionKeys) Assert.DoesNotContain($"\"{key}\"", r.Stdout);
        string context = specific.GetProperty("additionalContext").GetString()!;
        Assert.Equal(string.Format(FirstLine, reason), context.Split('\n')[0]);
        return (context, root.GetProperty("systemMessage").GetString()!);
    }

    private static TempDir Repo(out string head)
    {
        var dir = TempDir.New();
        PosixShell.Git(dir.Path, "init", "-q");
        PosixShell.Git(dir.Path, "commit", "-q", "--allow-empty", "-m", "first");
        head = PosixShell.Git(dir.Path, "rev-parse", "HEAD");
        return dir;
    }

    private static void Record(string dir, string text) => File.WriteAllText(Path.Combine(dir, ".pr-ready"), text);

    [ShellFact]
    public void SettingsCommand_GuardsMissingScriptAndNeverBlocks()
    {
        // [AC-contributor-gate-10] [EDGE-contributor-gate-14] [EDGE-pr-gate-advisory-2] [EDGE-pr-gate-advisory-3] The
        // settings command, run as Claude Code's hook shell does: silent without the script, the note passes through,
        // and no script failure can block.
        string payload = Payload("gh pr create", fx.NonGit.Path);
        var missing = PosixShell.Run(["-c", ContributorGateConfigTests.GuardedCommand], payload, fx.Elsewhere.Path,
                                     new Dictionary<string, string?> { ["CLAUDE_PROJECT_DIR"] = fx.EmptyProject.Path });
        Allowed(missing);

        using var project = TempDir.New();
        string hooks = Path.Combine(project.Path, ".claude", "hooks");
        Directory.CreateDirectory(hooks);
        File.Copy(GateScript, Path.Combine(hooks, "pr-gate.sh"));
        var present = PosixShell.Run(["-c", ContributorGateConfigTests.GuardedCommand], payload, fx.Elsewhere.Path,
                                     new Dictionary<string, string?> { ["CLAUDE_PROJECT_DIR"] = project.Path });
        Noted(present, "cannot read HEAD");

        foreach (var (body, stdout) in new[] { ("printf x; exit 2\n", "x"), ("if then fi (\n", "") })
        {
            File.WriteAllText(Path.Combine(hooks, "pr-gate.sh"), body);
            var broken = PosixShell.Run(["-c", ContributorGateConfigTests.GuardedCommand], payload, fx.Elsewhere.Path,
                                        new Dictionary<string, string?> { ["CLAUDE_PROJECT_DIR"] = project.Path });
            Assert.Equal(0, broken.ExitCode);
            Assert.Equal(stdout, broken.Stdout);
        }
    }

    [ShellFact]
    public void Gate_NotesPrCreateWithoutRecord()
    {
        // [AC-pr-gate-advisory-1] [AC-pr-gate-advisory-2] [EDGE-pr-gate-advisory-10] Never blocked: advice for the PR
        // just opened, and a one-line note for the user.
        using var repo = Repo(out _);
        var (context, system) = Noted(Gate(Payload("gh pr create --fill", repo.Path)), "no .pr-ready record");
        foreach (var s in new[]
                 {
                     "recommended for non-trivial changes", "Small changes and contributors not using Claude Code don't need it",
                     "push fixes to this branch to update the PR", "code-critic", "docs/agent-rules/adversarial-qa.md",
                     "docs/agent-rules/contributing-with-claude.md", "tell your user", "take no other action unless they ask",
                 })
            Assert.True(context.Contains(s, StringComparison.Ordinal), $"the note must say '{s}'");
        Assert.DoesNotContain("required", context, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pr-gate: blocked", context);
        Assert.DoesNotContain('\n', system);
        foreach (var s in new[] { "recommended for non-trivial changes", "small changes don't need it", "never blocks",
                                  "docs/agent-rules/contributing-with-claude.md" })
            Assert.True(system.Contains(s, StringComparison.Ordinal), $"the user's note must say '{s}'");
        Assert.DoesNotContain("required", system, StringComparison.OrdinalIgnoreCase);
    }

    public static TheoryData<string, string> EveryReason() => new()
    {
        { "none", "no .pr-ready record" },
        { "qa=ran: x\n", "record has no commit" },
        { "commit={0}\n", "qa outcome missing or malformed" },
        { "commit=0000000000000000000000000000000000000000\nqa=ran: x\n", "record is for 0000000 but HEAD is {1}" },
        { "unreadable", "cannot read HEAD" },
    };

    [ShellTheory]
    [MemberData(nameof(EveryReason))]
    public void Gate_NoteCarriesNoPermissionDecision(string record, string reason)
    {
        // [AC-pr-gate-advisory-7] [EDGE-pr-gate-advisory-14] For every reason: no permission decision anywhere, so the
        // user's own rules (deny, ask) decide as usual. Noted() checks the exact keys and their absence.
        using var repo = Repo(out string head);
        string cwd = record == "unreadable" ? fx.NonGit.Path : repo.Path;
        if (record != "none" && record != "unreadable") Record(repo.Path, string.Format(record, head));
        Noted(Gate(Payload("gh pr create", cwd)), string.Format(reason, head, head[..7]));
    }

    [ShellTheory]
    [InlineData("gh pr create --base test --fill", "ran: 1 finding fixed")]
    [InlineData("gh pr create --base test --fill", "skipped: no UI or API surface")]
    [InlineData("gh pr create --base test --fill", "blocked: harness not listening; user chose to proceed")]
    [InlineData("cd . && gh pr create --base test", "ran: 1 finding fixed")]
    [InlineData("cd . && gh pr create --base test", "skipped: no UI or API surface")]
    [InlineData("cd . && gh pr create --base test", "blocked: harness not listening; user chose to proceed")]
    public void Gate_AllowsWithValidRecord(string command, string qa)
    {
        // [AC-contributor-gate-3]
        using var repo = Repo(out string head);
        Record(repo.Path, $"commit={head}\nqa={qa}\n");
        Allowed(Gate(Payload(command, repo.Path)));
    }

    [ShellFact]
    public void Gate_NotesStaleRecord()
    {
        // [AC-contributor-gate-4] [AC-pr-gate-advisory-4] A commit after the record makes it stale; the note names both.
        using var repo = Repo(out string old);
        Assert.Equal(0, PosixShell.Run([ReadyScript, "skipped: x"], null, repo.Path).ExitCode);
        PosixShell.Git(repo.Path, "commit", "-q", "--allow-empty", "-m", "second");
        string now = PosixShell.Git(repo.Path, "rev-parse", "HEAD");
        Noted(Gate(Payload("gh pr create", repo.Path)), $"record is for {old[..7]} but HEAD is {now[..7]}");
    }

    [ShellTheory]
    [InlineData("git push origin test")]
    [InlineData("git push -u origin x")]
    [InlineData("git merge --no-ff x")]
    [InlineData("git commit -m \"open with gh pr create later\"")]
    [InlineData("gh pr view 12")]
    [InlineData("gh pr list")]
    [InlineData("gh pr checkout 3")]
    [InlineData("gh pr status")]
    [InlineData("gh issue create")]
    [InlineData("echo \"gh pr create\"")]
    [InlineData("dotnet test")]
    public void Gate_IgnoresOtherCommands(string command)
    {
        // [AC-contributor-gate-5]
        Allowed(Gate(Payload(command, fx.NonGit.Path)));
    }

    private const string Body = "Merge x: the gate\n\ngh pr create --base test is gated now (\"quoted\", 'single')\nmore)";

    public static TheoryData<string, string> NeverNotes() => new()
    {
        { "Bash", $"git commit -m \"{Body.Replace("\"", "\\\"")}\"" },
        { "Bash", $"git commit -F - <<'EOF'\n{Body}\nEOF" },
        { "Bash", $"git commit -F - <<EOF\n{Body}\nEOF" },
        { "Bash", $"git commit -F - <<-EOF\n\t{Body.Replace("\n", "\n\t")}\n\tEOF" },
        { "Bash", $"git commit --allow-empty -F - <<'EOF' && git status\n{Body}\nEOF" },
        { "Bash", $"git merge --no-ff x -F - <<'EOF'\n{Body}\nEOF" },
        { "Bash", $"git commit -m \"$(cat <<'EOF'\n{Body}\nEOF\n)\"" },
        { "Bash", "git commit -m \"x\r\ngh pr create\"" },
        { "PowerShell", $"@'\n{Body}\n'@ | git commit -F -" },
        { "PowerShell", $"@\"\n{Body}\n\"@ | git merge --no-ff x -F -" },
        { "PowerShell", "git commit -m \"a`\"; gh pr create\"" },
        { "Bash", "echo \"gh pr create\"" },
        { "Bash", "echo 'a; gh pr create'" },
        { "Bash", "# gh pr create" },
        { "Bash", "git push # then gh pr create" },
        { "Bash", "git push\ngh pr create" },
        // One row per cut, each of which would block if its cut were removed.
        { "Bash", "git push # a; gh pr create --x" },
        { "Bash", "echo `date; gh pr create --x`" },
        { "Bash", "echo $(date; gh pr create --x)" },
        { "Bash", "cat > f <<EOF; gh pr create --x\nbody\nEOF" },
    };

    [ShellTheory]
    [MemberData(nameof(NeverNotes))]
    public void Gate_NeverNotesQuotedHeredocOrLaterLineText(string tool, string command)
    {
        // [AC-contributor-gate-5] [EDGE-contributor-gate-2] [EDGE-contributor-gate-4] The owner's own multi-line
        // commits and merges, which Claude Code may run the hook for anyway: never blocked.
        Allowed(Gate(Payload(command, fx.NonGit.Path, tool)));
    }

    [ShellTheory]
    [InlineData("Bash", "gh pr create")]
    [InlineData("Bash", "gh pr create --fill")]
    [InlineData("Bash", "gh pr new")]
    [InlineData("Bash", "   gh pr create")]
    [InlineData("Bash", "cd x && gh pr create")]
    [InlineData("Bash", "false || gh pr create")]
    [InlineData("Bash", "a; gh pr create")]
    [InlineData("Bash", "x | gh pr create")]
    [InlineData("Bash", "sleep 1 & gh pr create")]
    [InlineData("Bash", "git push -u origin x && gh pr create --title t")]
    [InlineData("Bash", "gh pr create --title \"x\" --body \"$(cat <<'EOF'\nbody\nEOF\n)\"")]
    [InlineData("PowerShell", "& gh pr create")]
    [InlineData("PowerShell", "git push; gh pr create --base test")]
    public void Gate_DetectsPrefixSegments(string tool, string command)
    {
        // [EDGE-contributor-gate-1] [EDGE-contributor-gate-11]
        Noted(Gate(Payload(command, fx.NonGit.Path, tool)), "cannot read HEAD");
    }

    [ShellTheory]
    [InlineData("gh prs create")]
    [InlineData("gh pr created")]
    [InlineData("xgh pr create")]
    [InlineData("gh  pr create")]
    [InlineData("gh -R o/r pr create")]
    [InlineData("gh --repo=o/r pr create")]
    [InlineData("/usr/bin/gh pr create")]
    [InlineData("gh.exe pr create")]
    [InlineData("env gh pr create")]
    [InlineData("GH_TOKEN=x gh pr create")]
    [InlineData("command gh pr create")]
    [InlineData("\"gh\" pr create")]
    [InlineData("cd 'x y' && gh pr create")]
    [InlineData(@"cd C:\x && gh pr create")]
    [InlineData("(gh pr create)")]
    [InlineData("{ gh pr create; }")]
    [InlineData("if true; then gh pr create; fi")]
    [InlineData("Gh pr create")]
    [InlineData("GH PR CREATE")]
    public void Gate_AcceptedMissesExitZero(string command)
    {
        // [EDGE-contributor-gate-3] Near misses and the detector's accepted misses: pinned, so any change shows.
        Allowed(Gate(Payload(command, fx.NonGit.Path)));
    }

    [ShellTheory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"tool_name\":\"Bash\",\"tool_input\":{\"description\":\"x\"},\"cwd\":\"/\"}")]
    [InlineData("{\"tool_name\":\"Bash\",\"tool_input\":{\"description\":\"command\"},\"cwd\":\"/\"}")]
    public void Gate_FailsOpenOnBadInput(string stdin)
    {
        // [EDGE-contributor-gate-5]
        Allowed(Gate(stdin));
    }

    [ShellTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Gate_RealisticPayload(bool indented)
    {
        // [EDGE-contributor-gate-5] The description and transcript path mention gh/pr; only the command counts.
        Allowed(Gate(Payload("gh pr view 12", fx.NonGit.Path, description: "Then run gh pr create", indented: indented)));
        // The end-of-string cut: without it the prefix would run on into the description.
        Allowed(Gate(Payload("gh pr view 12", fx.NonGit.Path, description: "x; gh pr create --fill", indented: indented)));
        Noted(Gate(Payload("gh pr create", fx.NonGit.Path, description: "Then run gh pr create", indented: indented)),
                "cannot read HEAD");
    }

    [ShellFact]
    public void Gate_NotesWhenHeadUnreadable()
    {
        // [EDGE-contributor-gate-6] [AC-pr-gate-advisory-5] [EDGE-pr-gate-advisory-13] Not a repo, and a repo with no
        // commit: still runs, with a note.
        Noted(Gate(Payload("gh pr create", fx.NonGit.Path)), "cannot read HEAD");
        using var unborn = TempDir.New();
        PosixShell.Git(unborn.Path, "init", "-q");
        Noted(Gate(Payload("gh pr create", unborn.Path)), "cannot read HEAD");
    }

    [ShellTheory]
    [InlineData("commit={0}\r\nqa=ran: fine\r\n")]
    [InlineData("commit={0}\nqa=ran: fine")]
    [InlineData("commit={0}\nreview=pass\nqa=ran: fine\n")]
    public void Gate_AcceptsCrlfAndExtraLines(string record)
    {
        // [EDGE-contributor-gate-7]
        using var repo = Repo(out string head);
        Record(repo.Path, string.Format(record, head));
        Allowed(Gate(Payload("gh pr create", repo.Path)));
    }

    [ShellTheory]
    [InlineData("qa=ran: fine\n", "record has no commit")]
    [InlineData("commit={0}\n", "qa outcome missing or malformed")]
    [InlineData("commit={0}\nqa=skipped:\n", "qa outcome missing or malformed")]
    [InlineData("commit={0}\nqa=skipped:   \n", "qa outcome missing or malformed")]
    [InlineData("commit={0}\nqa=done: x\n", "qa outcome missing or malformed")]
    [InlineData("commit={0}\nqa=ran:x\n", "qa outcome missing or malformed")]
    public void Gate_NotesMalformedRecord(string record, string reason)
    {
        // [EDGE-contributor-gate-8] [AC-pr-gate-advisory-5]
        using var repo = Repo(out string head);
        Record(repo.Path, string.Format(record, head));
        Noted(Gate(Payload("gh pr create", repo.Path)), reason);
    }

    [ShellFact]
    public void Gate_ReadsRecordFromCwdWorktree()
    {
        // [AC-contributor-gate-9] [EDGE-contributor-gate-9] The cwd's worktree decides, never CLAUDE_PROJECT_DIR.
        using var withRecord = Repo(out string head);
        using var without = Repo(out _);
        Record(withRecord.Path, $"commit={head}\nqa=ran: fine\n");
        Allowed(Gate(Payload("gh pr create", withRecord.Path), projectDir: without.Path));
        Noted(Gate(Payload("gh pr create", without.Path), projectDir: withRecord.Path), "no .pr-ready record");

        using var parent = TempDir.New();
        string tree = Path.Combine(parent.Path, "wt");
        PosixShell.Git(without.Path, "worktree", "add", "-q", "-b", "side", tree);
        Assert.Equal(0, PosixShell.Run([ReadyScript, "ran: in the worktree"], null, tree).ExitCode);
        Allowed(Gate(Payload("gh pr create", tree), projectDir: without.Path));
        Assert.False(File.Exists(Path.Combine(without.Path, ".pr-ready")));
    }

    [ShellFact]
    public void Gate_HandlesEscapedWindowsCwd()
    {
        // [EDGE-contributor-gate-10] A path with a space and é, backslash-escaped in the JSON on Windows.
        string payload = Payload("gh pr create", fx.Ready.Path);
        if (OperatingSystem.IsWindows()) Assert.Contains("\\\\", payload);
        Allowed(Gate(payload));
        using var noRecord = TempDir.New(withSpace: true);
        PosixShell.Git(noRecord.Path, "init", "-q");
        PosixShell.Git(noRecord.Path, "commit", "-q", "--allow-empty", "-m", "first");
        Noted(Gate(Payload("gh pr create", noRecord.Path)), "no .pr-ready record");
    }

    [ShellFact]
    public void Gate_ReadsPrettyPrintedCwd()
    {
        // [AC-contributor-gate-9] [EDGE-contributor-gate-10] The payload's cwd, read from indented JSON, decides — not
        // the process's working directory.
        Allowed(Gate(Payload("gh pr create --base test", fx.Ready.Path, indented: true), processCwd: fx.NonGit.Path));
        Noted(Gate(Payload("gh pr create --base test", fx.NonGit.Path, indented: true), processCwd: fx.Ready.Path),
                "cannot read HEAD");
    }

    [ShellFact]
    public void Gate_HandlesHugeCommand()
    {
        // [EDGE-contributor-gate-15] A 200 KB unquoted heredoc (too complex, so the hook runs): allowed, quickly.
        var body = string.Join("\n", Enumerable.Range(0, 4000).Select(i => $"line {i} gh pr create --base test filler text"));
        var r = Gate(Payload($"cat > f <<EOF\n{body}\nEOF", fx.NonGit.Path));
        Allowed(r);
        Assert.True(r.Elapsed < TimeSpan.FromSeconds(5), $"took {r.Elapsed}");
    }

    [ShellFact]
    public void Gate_NoCwdInPayload_UsesProcessCwd()
    {
        // [AC-contributor-gate-9] [EDGE-contributor-gate-10] With no cwd in the payload (or a null one), the process's
        // working directory decides.
        string noCwd = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["tool_name"] = "Bash", ["tool_input"] = new Dictionary<string, string> { ["command"] = "gh pr create" },
        }, Compact);
        string nullCwd = "{\"tool_name\":\"Bash\",\"tool_input\":{\"command\":\"gh pr create\"},\"cwd\":null}";
        foreach (var stdin in new[] { noCwd, nullCwd })
        {
            Allowed(Gate(stdin, processCwd: fx.Ready.Path));
            Noted(Gate(stdin, processCwd: fx.NonGit.Path), "cannot read HEAD");
        }
    }

    [ShellTheory]
    [InlineData("abc", "abc")]
    [InlineData("ab\"c\\d", "ab?c?d")]
    [InlineData("1%d2%s", "1?d2??")]
    [InlineData("\ta", "?a")]
    public void Gate_RecordCommitIsCleaned(string commit, string shown)
    {
        // [AC-pr-gate-advisory-4] [EDGE-pr-gate-advisory-1] A hand-edited commit goes into JSON: anything outside hex
        // shows as `?`, and `%` is never a printf format.
        using var repo = Repo(out string head);
        Record(repo.Path, $"commit={commit}\nqa=ran: fine\n");
        Noted(Gate(Payload("gh pr create", repo.Path)), $"record is for {shown} but HEAD is {head[..7]}");
    }

    [ShellFact]
    public void Gate_RecordCommitNonAsciiIsCleaned()
    {
        // [EDGE-pr-gate-advisory-1] One `?` per byte or per character, whichever the locale gives; the note stays ASCII.
        using var repo = Repo(out string head);
        File.WriteAllText(Path.Combine(repo.Path, ".pr-ready"), "commit=ab\u00e91\nqa=ran: fine\n", new System.Text.UTF8Encoding(false));
        var r = Gate(Payload("gh pr create", repo.Path));
        Assert.Equal(0, r.ExitCode);
        Assert.True(r.Stdout.All(c => c < 128), "note must be ASCII");
        string context = JsonDocument.Parse(r.Stdout).RootElement.GetProperty("hookSpecificOutput").GetProperty("additionalContext").GetString()!;
        Assert.Matches(@"^pr-gate: recommendation only; this gh pr create was not blocked \(record is for ab\?+1 but HEAD is [0-9a-f]{7}\)\.", context);
    }

    [ShellFact]
    public void Scripts_ParseUnderSh()
    {
        // [AC-contributor-gate-8] A syntax error would exit 2, a block, on every hook run: both scripts must parse.
        foreach (var script in new[] { GateScript, ReadyScript })
        {
            var r = PosixShell.Run(["-n", script], null, fx.Elsewhere.Path);
            Assert.True(r.ExitCode == 0, $"{Path.GetFileName(script)}: {r.Stderr}");
        }
    }

    [ShellFact]
    public void PrReady_WritesRecordForHead()
    {
        // [AC-contributor-gate-6]
        using var repo = Repo(out string head);
        var r = PosixShell.Run([ReadyScript, "skipped: no UI or API surface"], null, repo.Path);
        Assert.Equal(0, r.ExitCode);
        // [AC-pr-gate-advisory-8]
        Assert.Contains($"Recorded PR readiness for {head[..7]}; the PR recommendation is silenced for this commit.", r.Stdout);
        Assert.Contains("gh pr create --base test", r.Stdout);
        Assert.Equal($"commit={head}\nqa=skipped: no UI or API surface\n", File.ReadAllText(Path.Combine(repo.Path, ".pr-ready")));
        Allowed(Gate(Payload("gh pr create", repo.Path)));
    }

    public static TheoryData<string[]> BadOutcomes() => new()
    {
        Array.Empty<string>(), new[] { "" }, new[] { "skipped:" }, new[] { "skipped:   " }, new[] { "done: x" },
        new[] { "ran:x" }, new[] { "ran: a", "extra" },
    };

    [ShellTheory]
    [MemberData(nameof(BadOutcomes))]
    public void PrReady_RefusesBadOutcome(string[] args)
    {
        // [AC-contributor-gate-6] [EDGE-contributor-gate-8] Refused, and nothing written or changed.
        using var repo = Repo(out string head);
        string existing = $"commit={head}\nqa=ran: earlier\n";
        Record(repo.Path, existing);
        var r = PosixShell.Run([ReadyScript, .. args], null, repo.Path);
        Assert.Equal(1, r.ExitCode);
        Assert.NotEqual("", r.Stderr);
        Assert.Equal(existing, File.ReadAllText(Path.Combine(repo.Path, ".pr-ready")));
        Assert.Empty(Directory.EnumerateFiles(repo.Path, ".pr-ready.tmp.*"));
    }

    [ShellFact]
    public void PrReady_RefusesOutsideARepoOrUnborn()
    {
        // [AC-contributor-gate-6] [EDGE-contributor-gate-6] No repo, or no commit: refused, nothing written.
        using var notRepo = TempDir.New();
        using var unborn = TempDir.New();
        PosixShell.Git(unborn.Path, "init", "-q");
        foreach (var dir in new[] { notRepo.Path, unborn.Path })
        {
            var r = PosixShell.Run([ReadyScript, "ran: x"], null, dir);
            Assert.Equal(1, r.ExitCode);
            Assert.NotEqual("", r.Stderr);
            Assert.False(File.Exists(Path.Combine(dir, ".pr-ready")));
            Assert.Empty(Directory.EnumerateFiles(dir, ".pr-ready.tmp.*"));
        }
    }
}
