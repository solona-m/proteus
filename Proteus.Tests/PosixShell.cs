using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// A POSIX <c>sh</c> to run the repo's Claude Code hook scripts with: Git for Windows' on Windows, <c>sh</c> on PATH
/// elsewhere. Never WSL's <c>bash.exe</c> under System32, which would run them in another filesystem.
/// </summary>
internal static class PosixShell
{
    internal sealed record Shell(string ShPath, string? PathPrefix);

    internal sealed record Result(int ExitCode, string Stdout, string Stderr, TimeSpan Elapsed);

    private static readonly Lazy<Shell?> Found = new(Locate);

    public static Shell? Find() => Found.Value;

    private static Shell? Locate()
    {
        if (!OperatingSystem.IsWindows()) return OnPath("sh") is { } sh ? new Shell(sh, null) : null;
        // git --exec-path is <git root>/mingw64/libexec/git-core.
        string? exec = RunGit("--exec-path");
        if (exec == null) return null;
        var root = new DirectoryInfo(exec.Trim().Replace('/', Path.DirectorySeparatorChar)).Parent?.Parent?.Parent;
        if (root == null) return null;
        // bin/sh.exe sets up its own PATH (cat, git), as Claude Code's hook shell does, so it runs with the PATH as
        // given; usr/bin/sh.exe needs usr/bin put first.
        string bin = Path.Combine(root.FullName, "bin", "sh.exe");
        if (File.Exists(bin)) return new Shell(bin, null);
        string usr = Path.Combine(root.FullName, "usr", "bin", "sh.exe");
        return File.Exists(usr) ? new Shell(usr, Path.Combine(root.FullName, "usr", "bin")) : null;
    }

    private static string? OnPath(string name)
        => (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Select(d => Path.Combine(d, name)).FirstOrDefault(File.Exists);

    private static string? RunGit(string args)
    {
        try
        {
            var p = Process.Start(new ProcessStartInfo("git", args) { RedirectStandardOutput = true, UseShellExecute = false });
            if (p == null) return null;
            string o = p.StandardOutput.ReadToEnd();
            p.WaitForExit(10_000);
            return p.ExitCode == 0 ? o : null;
        }
        catch { return null; }
    }

    /// <summary>Why the shell tests can't run here, or null when they can (or must: under CI they never skip).</summary>
    public static string? Missing()
    {
        if (Find() != null) return null;
        bool ci = string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase)
               || string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase);
        return ci ? null : "no POSIX sh found (Git for Windows on Windows)";
    }

    /// <summary>Run sh with arguments, stdin and a working directory; git's own environment never leaks in.</summary>
    public static Result Run(IEnumerable<string> args, string? stdin, string workingDirectory,
                             IReadOnlyDictionary<string, string?>? env = null)
    {
        var shell = Find() ?? throw new InvalidOperationException("no POSIX sh found");
        var psi = new ProcessStartInfo(shell.ShPath)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, WorkingDirectory = workingDirectory,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        foreach (var k in new[] { "GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE", "CLAUDE_PROJECT_DIR" }) psi.Environment.Remove(k);
        psi.Environment["GIT_CEILING_DIRECTORIES"] = TempRoot;
        if (shell.PathPrefix != null)
            psi.Environment["PATH"] = shell.PathPrefix + Path.PathSeparator + psi.Environment["PATH"];
        if (env != null)
            foreach (var (k, v) in env)
                if (v == null) psi.Environment.Remove(k); else psi.Environment[k] = v;

        var clock = Stopwatch.StartNew();
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (stdin != null)
        {
            var bytes = new UTF8Encoding(false).GetBytes(stdin);
            p.StandardInput.BaseStream.Write(bytes, 0, bytes.Length);
        }
        p.StandardInput.Close();
        if (!p.WaitForExit(30_000))
        {
            try { p.Kill(true); } catch { }
            throw new TimeoutException("sh did not finish in 30 s");
        }
        p.WaitForExit();
        return new Result(p.ExitCode, stdout.Result, stderr.Result, clock.Elapsed);
    }

    /// <summary>Run git in a directory with a fixed identity and no signing or hooks; throws on failure.</summary>
    public static string Git(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = dir,
        };
        foreach (var a in new[] { "-c", "user.name=t", "-c", "user.email=t@t", "-c", "commit.gpgsign=false",
                                  "-c", "core.hooksPath=" + NoHooks.Value })
            psi.ArgumentList.Add(a);
        foreach (var a in args) psi.ArgumentList.Add(a);
        foreach (var k in new[] { "GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE" }) psi.Environment.Remove(k);
        psi.Environment["GIT_CEILING_DIRECTORIES"] = TempRoot;
        using var p = Process.Start(psi)!;
        string o = p.StandardOutput.ReadToEnd();
        string e = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {e}");
        return o.Trim();
    }

    private static readonly Lazy<string> NoHooks = new(() => TempDir.New().Path);

    /// <summary>The temp folder, as git's ceiling: a test folder never resolves a repo above it.</summary>
    private static string TempRoot => Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}

/// <summary>A GUID-named temp folder, deleted on Dispose.</summary>
internal sealed class TempDir : IDisposable
{
    public string Path { get; }

    private TempDir(string path) => Path = path;

    public static TempDir New(bool withSpace = false)
    {
        string name = (withSpace ? "pr gate é " : "prgate-") + Guid.NewGuid().ToString("N");
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), name);
        Directory.CreateDirectory(path);
        return new TempDir(path);
    }

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(Path, true);
        }
        catch { /* best effort */ }
    }
}

/// <summary>A fact that needs a POSIX sh: skipped, saying why, without one (never under CI).</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class ShellFactAttribute : FactAttribute
{
    public ShellFactAttribute()
    {
        if (PosixShell.Missing() is { } why) Skip = why;
    }
}

/// <summary>A theory that needs a POSIX sh: skipped, saying why, without one (never under CI).</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class ShellTheoryAttribute : TheoryAttribute
{
    public ShellTheoryAttribute()
    {
        if (PosixShell.Missing() is { } why) Skip = why;
    }
}
