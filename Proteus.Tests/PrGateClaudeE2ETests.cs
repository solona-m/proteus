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
                               IReadOnlyList<string> Tools);

    private Call Claude(Sandbox box, string tool, string command)
    {
        string debug = Path.Combine(box.Root.Path, $"debug-{Guid.NewGuid():N}.txt");
        string prompt = $"Use the {tool} tool to run exactly the command below, once, character for character, with no " +
                        "changes and no other tool calls. Then stop and reply only with 'done'.\n\n" + command;
        var (_, stdout, stderr) = Run(ClaudePath()!, ["-p", prompt, "--output-format", "stream-json", "--verbose",
                                                      "--max-budget-usd", "0.5", "--setting-sources", "project",
                                                      "--debug-file", debug,
                                                      "--allowedTools", "Bash(gh:*)", "Bash(cd:*)", "Bash(git commit:*)", "PowerShell(gh:*)"],
                                      box.Clone, box.Env);
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
        return new Call(usedTool, usedCommand, keys, results.ToString(), debugText, tools);
    }

    private static void SameCommand(Call call, string expected)
        => Assert.True(call.Command?.Replace("\r\n", "\n") == expected,
                       $"model altered the command, re-run (asked {expected.Split('\n')[0]}, got {call.Command?.Split('\n')[0]})");

    private const string Create = "gh pr create --base test --title x --body y";

    [ClaudeE2EFact]
    public void ClaudeRunner_BlocksAndAllows()
    {
        // [AC-contributor-gate-11] [AC-contributor-gate-5] [AC-contributor-gate-10] [EDGE-contributor-gate-1]
        // [EDGE-contributor-gate-4] [EDGE-contributor-gate-11] [EDGE-contributor-gate-13]
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

        // (a) plain: the hook's own message, not a permission denial; the stub never ran.
        var a = Claude(box, "Bash", Create);
        SameCommand(a, Create);
        Assert.Contains("no .pr-ready record", a.Result);
        Assert.Empty(box.Calls());

        // (b) compound: any subcommand matches the `if` filter.
        string compound = "cd . && " + Create;
        var b = Claude(box, "Bash", compound);
        SameCommand(b, compound);
        Assert.Contains("no .pr-ready record", b.Result);
        Assert.Empty(box.Calls());

        // (c) an unrelated gh command: the `if` filter skips the hook.
        var c = Claude(box, "Bash", "gh pr view 1");
        SameCommand(c, "gh pr view 1");
        Assert.Contains("pr view 1", box.Calls());
        Assert.DoesNotContain("pr-gate:", c.Result);
        Assert.Contains("Skipping hook due to if condition", c.Debug);
        box.ClearLog();

        // (d) PowerShell, when this session offers the tool.
        if (a.Tools.Contains("PowerShell"))
        {
            var d = Claude(box, "PowerShell", Create);
            Assert.Equal("PowerShell", d.Tool);
            Assert.Contains("command", d.InputKeys);
            Assert.Contains("no .pr-ready record", d.Result);
            Assert.Empty(box.Calls());
        }
        else output.WriteLine("(d) skipped: the PowerShell tool is not offered in this session");

        // (e) the owner's heredoc commits Claude Code can't parse: the hook runs and lets them through.
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
            // 2.1.316 logs `Hooks: PreToolUse:Bash [<command>] finished with status 0 (…ms)` for a hook that ran, and
            // `Skipping hook due to if condition …` for one its `if` skipped.
            Assert.False(e.Debug.Contains("Skipping hook due to if condition", StringComparison.Ordinal),
                         "hook not exercised; re-plan (the if filter skipped it for a too-complex command)");
            Assert.True(e.Debug.Split('\n').Any(l => l.Contains("PreToolUse:Bash", StringComparison.Ordinal)
                                                     && l.Contains("pr-gate.sh", StringComparison.Ordinal)
                                                     && l.Contains("finished with status 0", StringComparison.Ordinal)),
                        "hook not exercised; re-plan (no 'finished with status 0' line for pr-gate.sh)");
            Assert.DoesNotContain("pr-gate:", e.Result);
        }

        // (f) once the record names the clone's current HEAD, gh pr create goes through to the stub.
        var ready = PosixShell.Run([Path.Combine(box.Clone, ".claude", "hooks", "pr-ready.sh"), "skipped: e2e"], null, box.Clone);
        Assert.Equal(0, ready.ExitCode);
        box.ClearLog();
        var f = Claude(box, "Bash", Create);
        SameCommand(f, Create);
        Assert.Contains("pr create --base test --title x --body y", box.Calls());
    }

    [ClaudeE2EFact]
    public void HookCost_IsMeasured()
    {
        // [AC-contributor-gate-10] The settings command's cost per run, through the same shell as Claude Code's hooks.
        var shell = PosixShell.Find()!;
        using var nonGit = TempDir.New();
        var env = new Dictionary<string, string?> { ["CLAUDE_PROJECT_DIR"] = ContributorGateConfigTests.RepoRoot };
        foreach (var (name, command) in new[]
                 {
                     ("too-complex heredoc commit", "git commit -F - <<EOF\nmsg\ngh pr create later\nEOF"),
                     ("gh pr view", "gh pr view 12"),
                     ("blocked gh pr create", "gh pr create"),
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
