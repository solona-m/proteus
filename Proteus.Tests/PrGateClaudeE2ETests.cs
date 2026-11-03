using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace Proteus.Tests;

/// <summary>
/// Runs only when asked (PROTEUS_CLAUDE_E2E=1) with `claude` on PATH, never under CI: these spend real Claude calls.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class ClaudeE2EFactAttribute : FactAttribute
{
    public ClaudeE2EFactAttribute()
    {
        bool ci = string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase)
               || string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase);
        if (ci) Skip = "never under CI: spends Claude calls";
        else if (Environment.GetEnvironmentVariable("PROTEUS_CLAUDE_E2E") != "1") Skip = "opt-in: set PROTEUS_CLAUDE_E2E=1";
        else if (PrGateClaudeE2ETests.ClaudePath() == null) Skip = "claude is not on PATH";
        else if (PosixShell.Missing() is { } why) Skip = why;
    }
}

/// <summary>
/// The contributor PR gate under Claude Code's real hook runner (the merge gate for the gate itself). Runs in a
/// throwaway clone of HEAD with no remote, a stub `gh` first on PATH that only logs, and every GitHub token unset, so
/// nothing here can reach GitHub.
/// </summary>
[Trait("Speed", "Slow")]
public class PrGateClaudeE2ETests(ITestOutputHelper output)
{
    internal static string? ClaudePath()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            foreach (var name in OperatingSystem.IsWindows() ? new[] { "claude.exe", "claude.cmd" } : new[] { "claude" })
            {
                string p = Path.Combine(dir, name);
                if (File.Exists(p)) return p;
            }
        return null;
    }

    private sealed class Sandbox : IDisposable
    {
        public TempDir Root { get; } = TempDir.New();
        public string Clone => Path.Combine(Root.Path, "clone");
        public string Log => Path.Combine(Root.Path, "gh-calls.log");
        public Dictionary<string, string?> Env { get; } = new();

        public Sandbox()
        {
            PosixShell.Git(Root.Path, "clone", "-q", ContributorGateConfigTests.RepoRoot, Clone);
            PosixShell.Git(Clone, "remote", "remove", "origin");
            PosixShell.Git(Clone, "config", "user.name", "t");
            PosixShell.Git(Clone, "config", "user.email", "t@t");
            string stub = Path.Combine(Root.Path, "stub");
            Directory.CreateDirectory(stub);
            File.WriteAllText(Path.Combine(stub, "gh"), $"#!/bin/sh\nprintf '%s\\n' \"$*\" >> '{Log.Replace('\\', '/')}'\nexit 0\n");
            File.WriteAllText(Path.Combine(stub, "gh.cmd"), $"@echo off\r\necho %*>> \"{Log}\"\r\nexit /b 0\r\n");
            string config = Path.Combine(Root.Path, "gh-config");
            Directory.CreateDirectory(config);
            Env["PATH"] = stub + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
            foreach (var k in new[] { "GH_TOKEN", "GITHUB_TOKEN", "GH_ENTERPRISE_TOKEN", "GH_REPO", "CLAUDE_PROJECT_DIR" }) Env[k] = null;
            Env["GH_CONFIG_DIR"] = config;
        }

        public string[] Calls() => File.Exists(Log) ? File.ReadAllLines(Log).Select(l => l.Trim()).Where(l => l.Length > 0).ToArray() : [];
        public void ClearLog() { if (File.Exists(Log)) File.Delete(Log); }
        public void Dispose() => Root.Dispose();
    }

    private static (int Exit, string Stdout, string Stderr) Run(string file, IEnumerable<string> args, string cwd,
                                                                IReadOnlyDictionary<string, string?> env, int timeoutMs = 300_000)
    {
        var psi = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            UseShellExecute = false, WorkingDirectory = cwd,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        foreach (var (k, v) in env)
            if (v == null) psi.Environment.Remove(k); else psi.Environment[k] = v;
        using var p = Process.Start(psi)!;
        p.StandardInput.Close();
        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } throw new TimeoutException($"{file} timed out"); }
        p.WaitForExit();
        return (p.ExitCode, o.Result, e.Result);
    }

    /// <summary>What one Claude call did: the tool it used, the exact command, what came back, and its debug log.</summary>
    private sealed record Call(string? Tool, string? Command, IReadOnlyList<string> InputKeys, string Result, string Debug,
                               IReadOnlyList<string> Tools, string SessionId, string Transcript);

    /// <summary>The session's transcript, `&lt;config&gt;/projects/*/&lt;session&gt;.jsonl`, searched one level deep only.</summary>
    private static string TranscriptOf(string sessionId)
    {
        if (sessionId.Length == 0) return "";
        string config = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } c
            ? c : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        string projects = Path.Combine(config, "projects");
        if (!Directory.Exists(projects)) return "";
        foreach (var dir in Directory.EnumerateDirectories(projects))
        {
            string file = Path.Combine(dir, sessionId + ".jsonl");
            if (File.Exists(file)) return File.ReadAllText(file);
        }
        return "";
    }

    private Call Claude(Sandbox box, string tool, string command, params string[] extra)
    {
        string debug = Path.Combine(box.Root.Path, $"debug-{Guid.NewGuid():N}.txt");
        string prompt = $"Use the {tool} tool to run exactly the command below, once, character for character, with no " +
                        "changes and no other tool calls. Then stop and reply only with 'done'.\n\n" + command;
        var (_, stdout, stderr) = Run(ClaudePath()!, ["-p", prompt, "--output-format", "stream-json", "--verbose",
                                                      "--max-budget-usd", "0.5", "--setting-sources", "project",
                                                      "--debug-file", debug,
                                                      .. extra,
                                                      "--allowedTools", "Bash(gh:*)", "Bash(cd:*)", "Bash(git commit:*)", "PowerShell(gh:*)"],
                                      box.Clone, box.Env);
        string sessionId = "";
        string? usedTool = null, usedCommand = null;
        var keys = new List<string>();
        var results = new StringBuilder();
        var tools = new List<string>();
        foreach (var line in stdout.Split('\n'))
        {
            if (!line.TrimStart().StartsWith('{')) continue;
            JsonElement e;
            try { e = JsonDocument.Parse(line).RootElement.Clone(); } catch (JsonException) { continue; }
            if (e.ValueKind != JsonValueKind.Object) continue;
            if (e.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String && t.GetString() == "system"
                && e.TryGetProperty("tools", out var ts) && ts.ValueKind == JsonValueKind.Array)
                tools.AddRange(ts.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!));
            if (sessionId.Length == 0 && e.TryGetProperty("session_id", out var sid) && sid.ValueKind == JsonValueKind.String)
                sessionId = sid.GetString()!;
            if (!e.TryGetProperty("message", out var m) || m.ValueKind != JsonValueKind.Object
                || !m.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) continue;
            foreach (var c in content.EnumerateArray())
            {
                if (c.ValueKind != JsonValueKind.Object) continue;
                string kind = c.TryGetProperty("type", out var ct) && ct.ValueKind == JsonValueKind.String ? ct.GetString()! : "";
                if (kind == "tool_use" && usedTool == null)
                {
                    usedTool = c.TryGetProperty("name", out var n) ? n.ToString() : null;
                    if (c.TryGetProperty("input", out var input) && input.ValueKind == JsonValueKind.Object)
                    {
                        keys.AddRange(input.EnumerateObject().Select(p => p.Name));
                        if (input.TryGetProperty("command", out var cmd) && cmd.ValueKind == JsonValueKind.String) usedCommand = cmd.GetString();
                    }
                }
                else if (kind == "tool_result")
                    results.AppendLine(c.TryGetProperty("content", out var rc) ? rc.ToString() : "");
            }
        }
        string debugText = File.Exists(debug) ? File.ReadAllText(debug) : "";
        output.WriteLine($"--- {tool}: {command.Split('\n')[0]}\n tool_use: {usedTool} {usedCommand?.Split('\n')[0]}\n result: {results.ToString().Trim()}\n stderr: {stderr.Trim()}");
        return new Call(usedTool, usedCommand, keys, results.ToString(), debugText, tools, sessionId, TranscriptOf(sessionId));
    }

    private static void SameCommand(Call call, string expected)
        => Assert.True(call.Command?.Replace("\r\n", "\n") == expected,
                       $"model altered the command, re-run (asked {expected.Split('\n')[0]}, got {call.Command?.Split('\n')[0]})");

    private const string Create = "gh pr create --base test --title x --body y";

    /// <summary>
    /// The note reached Claude: the debug log says the hook gave additionalContext (2.1.316 names the hook by its command,
    /// which holds pr-gate.sh), and the transcript holds the hook_additional_context attachment Claude is shown.
    /// </summary>
    private static void GateNoted(Call call, string tool, string reason)
    {
        Assert.True(call.Debug.Split('\n').Any(l => l.Contains("PreToolUse", StringComparison.Ordinal)
                                                   && l.Contains("pr-gate.sh", StringComparison.Ordinal)
                                                   && l.Contains("provided additionalContext", StringComparison.Ordinal)),
                    "re-plan: the runner did not deliver the note (no 'provided additionalContext' line)");
        Assert.True(call.Transcript.Split('\n').Any(l => l.Contains("\"hook_additional_context\"", StringComparison.Ordinal)
                                                        && l.Contains($"PreToolUse:{tool}", StringComparison.Ordinal)
                                                        && l.Contains("pr-gate: recommendation only", StringComparison.Ordinal)
                                                        && l.Contains(reason, StringComparison.Ordinal)),
                    "re-plan: the runner did not deliver the note (no hook_additional_context in the transcript)");
    }

    /// <summary>No note: no additionalContext from our hook, and none in the transcript.</summary>
    private static void GateSilent(Call call)
    {
        Assert.False(call.Debug.Split('\n').Any(l => l.Contains("pr-gate.sh", StringComparison.Ordinal)
                                                    && l.Contains("provided additionalContext", StringComparison.Ordinal)),
                     "the hook gave a note it should not have");
        Assert.DoesNotContain("pr-gate: recommendation only", call.Transcript);
    }

    [ClaudeE2EFact]
    public void ClaudeRunner_RecommendsAndRuns()
    {
        // [AC-pr-gate-advisory-10] [AC-pr-gate-advisory-1] [AC-pr-gate-advisory-2] [AC-pr-gate-advisory-6]
        // [AC-pr-gate-advisory-7] [EDGE-pr-gate-advisory-5] [EDGE-pr-gate-advisory-10] [EDGE-pr-gate-advisory-11]
        // [EDGE-pr-gate-advisory-12] [AC-contributor-gate-11]
        using var box = new Sandbox();

        // Sentinel: both shells resolve the stub, not a real gh.
        var shell = PosixShell.Find()!;
        var bashEnv = new Dictionary<string, string?>(box.Env);
        if (shell.PathPrefix != null) bashEnv["PATH"] = shell.PathPrefix + Path.PathSeparator + bashEnv["PATH"];
        Run(shell.ShPath, ["-c", "gh --version"], box.Clone, bashEnv);
        if (OperatingSystem.IsWindows()) Run("powershell", ["-NoProfile", "-Command", "gh --version"], box.Clone, box.Env);
        Assert.True(box.Calls().Count(c => c == "--version") == (OperatingSystem.IsWindows() ? 2 : 1),
                    $"the stub gh was not the one resolved: {string.Join(" | ", box.Calls())}");
        box.ClearLog();

        // (a) No record: the PR command runs, and the note reaches Claude and the user.
        var a = Claude(box, "Bash", Create);
        SameCommand(a, Create);
        Assert.Contains("pr create --base test --title x --body y", box.Calls());
        GateNoted(a, "Bash", "no .pr-ready record");
        Assert.DoesNotContain("hook blocking error", a.Result);
        Assert.DoesNotContain("pr-gate: blocked", a.Result);
        // [AC-pr-gate-advisory-2] The step-0 dry run showed -p persists the user-side note.
        Assert.True(a.Transcript.Split('\n').Any(l => l.Contains("\"hook_system_message\"", StringComparison.Ordinal)
                                                     && l.Contains("never blocks", StringComparison.Ordinal)),
                    "the user-side note (hook_system_message) is missing from the transcript");
        box.ClearLog();

        // (b) Compound: any subcommand matches the `if` filter.
        string compound = "cd . && " + Create;
        var b = Claude(box, "Bash", compound);
        SameCommand(b, compound);
        Assert.Contains("pr create --base test --title x --body y", box.Calls());
        GateNoted(b, "Bash", "no .pr-ready record");
        box.ClearLog();

        // (c) An unrelated gh command: the `if` filter skips the hook.
        var c = Claude(box, "Bash", "gh pr view 1");
        SameCommand(c, "gh pr view 1");
        Assert.Contains("pr view 1", box.Calls());
        Assert.Contains("Skipping hook due to if condition \"Bash(gh pr create *)\"", c.Debug);
        GateSilent(c);
        box.ClearLog();

        // (d) PowerShell, when this session offers the tool.
        if (a.Tools.Contains("PowerShell"))
        {
            var d = Claude(box, "PowerShell", Create);
            SameCommand(d, Create);
            Assert.Equal("PowerShell", d.Tool);
            Assert.Contains("command", d.InputKeys);
            Assert.Contains("pr create --base test --title x --body y", box.Calls());
            GateNoted(d, "PowerShell", "no .pr-ready record");
        }
        else output.WriteLine("(d) skipped: the PowerShell tool is not offered in this session");
        box.ClearLog();

        // (e) The owner's heredoc commits Claude Code can't parse: the hook runs and stays silent. (Claude Code's own
        // permission check refuses these as too complex under -p, so whether the commit lands is not asserted.)
        string body = "Merge x\n\ngh pr create --base test is gated now\n";
        foreach (var heredoc in new[]
                 {
                     $"git commit --allow-empty -F - <<EOF\n{body}EOF",
                     $"git commit --allow-empty -F - <<-EOF\n\t{body.Replace("\n", "\n\t").TrimEnd('\t')}\tEOF",
                     $"git commit --allow-empty -F - <<'EOF' && git status\n{body}EOF",
                 })
        {
            var e = Claude(box, "Bash", heredoc);
            SameCommand(e, heredoc);
            Assert.False(e.Debug.Contains("Skipping hook due to if condition", StringComparison.Ordinal),
                         "hook not exercised; re-plan (the if filter skipped it for a too-complex command)");
            Assert.True(e.Debug.Split('\n').Any(l => l.Contains("PreToolUse:Bash", StringComparison.Ordinal)
                                                     && l.Contains("pr-gate.sh", StringComparison.Ordinal)
                                                     && l.Contains("finished with status 0", StringComparison.Ordinal)),
                        "hook not exercised; re-plan (no 'finished with status 0' line for pr-gate.sh)");
            GateSilent(e);
        }

        // (f) [AC-pr-gate-advisory-3] With a record for the current HEAD: the PR command runs, silently.
        string readyScript = Path.Combine(box.Clone, ".claude", "hooks", "pr-ready.sh");
        Assert.Equal(0, PosixShell.Run([readyScript, "skipped: e2e"], null, box.Clone).ExitCode);
        box.ClearLog();
        var f = Claude(box, "Bash", Create);
        SameCommand(f, Create);
        Assert.Contains("pr create --base test --title x --body y", box.Calls());
        GateSilent(f);

        // (g) The user's deny rule still wins: the note carries no permission decision.
        File.Delete(Path.Combine(box.Clone, ".pr-ready"));
        box.ClearLog();
        var g = Claude(box, "Bash", Create, "--disallowedTools", "Bash(gh pr create:*)");
        SameCommand(g, Create);
        Assert.DoesNotContain(box.Calls(), call => call.Contains("pr create"));
        Assert.True(g.Result.Contains("denied", StringComparison.OrdinalIgnoreCase)
                    || g.Result.Contains("permission", StringComparison.OrdinalIgnoreCase),
                    $"(g) expected a permission denial, got: {g.Result.Trim()}");
        output.WriteLine("(g) hook ran before the deny: " + g.Debug.Split('\n').Any(l => l.Contains("PreToolUse:Bash", StringComparison.Ordinal)
                                                                                  && l.Contains("pr-gate.sh", StringComparison.Ordinal)));
    }

    [ClaudeE2EFact]
    public void HookCost_IsMeasured()
    {
        // [AC-contributor-gate-10] [EDGE-pr-gate-advisory-7] The settings command's cost per run, through the same shell as Claude Code's hooks.
        var shell = PosixShell.Find()!;
        using var nonGit = TempDir.New();
        var env = new Dictionary<string, string?> { ["CLAUDE_PROJECT_DIR"] = ContributorGateConfigTests.RepoRoot };
        foreach (var (name, command) in new[]
                 {
                     ("too-complex heredoc commit", "git commit -F - <<EOF\nmsg\ngh pr create later\nEOF"),
                     ("gh pr view", "gh pr view 12"),
                     ("noted gh pr create", "gh pr create"),
                 })
        {
            string payload = PrGateTests.Payload(command, nonGit.Path);
            var times = Enumerable.Range(0, 20)
                                  .Select(_ => PosixShell.Run(["-c", ContributorGateConfigTests.GuardedCommand], payload, nonGit.Path, env).Elapsed)
                                  .OrderBy(t => t).ToList();
            var median = times[times.Count / 2];
            output.WriteLine($"{name}: median {median.TotalMilliseconds:F0} ms (via {shell.ShPath})");
            Assert.True(median < TimeSpan.FromSeconds(2), $"{name}: median {median}");
        }
    }
}
