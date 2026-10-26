using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// Guards the repo's supply-chain config, which fails SILENTLY: a floating NuGet version, an action
/// referenced by a movable tag, a Dependabot entry that stopped covering a project, or a workflow that
/// starts publishing on a push to <c>test</c> all build and run perfectly well. Nothing notices until
/// the day the code behind a tag changes.
/// <para/>
/// FORM only, never values: no SHA, tag or package version is written down here, so Dependabot's own
/// bumps keep these green. That a pinned SHA is the commit its tag points at was checked when the pin
/// was made — major tags move with every upstream release, so it cannot be a test.
/// <para/>
/// There is no YAML library in the tests and none is worth adding for files this repo writes itself, so
/// YAML is read line by line against the shapes our own files use. PyYAML and GitHub's parser are the
/// syntax checks.
/// </summary>
public sealed class SupplyChainTests
{
    /// <summary>Same walk-up as ReadmeTranslationTests: proteus.slnx exists only at the root.</summary>
    private static readonly Lazy<string?> Root = new(() =>
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "proteus.slnx")))
            dir = dir.Parent;
        return dir?.FullName;
    });

    private static string RepoRoot
    {
        get
        {
            Assert.True(Root.Value != null,
                $"Could not find proteus.slnx above {AppContext.BaseDirectory}. These tests read the " +
                "project, workflow and Dependabot files from the checkout; they cannot run from a " +
                "detached copy of the binaries.");
            return Root.Value!;
        }
    }

    private static string WorkflowsDir => Path.Combine(RepoRoot, ".github", "workflows");

    private static string[] ReadLines(string path) =>
        File.ReadAllText(path).Replace("\r\n", "\n").Split('\n');

    // ---------------------------------------------------------------------------------------------
    // Pure helpers
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// A plain version: 2-4 numeric parts and an optional prerelease label. NuGet reads it as the
    /// lowest applicable version, <c>[x, )</c>. Floats (<c>2.*</c>) and every range are rejected — and so
    /// is the bracketed exact form <c>[x]</c>, on purpose: it blocks a transitive lift and Dependabot
    /// rewrites it badly.
    /// </summary>
    internal static bool IsPlainNuGetVersion(string? version) =>
        version != null && Regex.IsMatch(version, @"^\d+(\.\d+){1,3}(-[0-9A-Za-z][0-9A-Za-z.-]*)?$");

    /// <summary>
    /// The form NuGet writes into lock and assets files: three numeric parts at least, a fourth only
    /// when it is not 0, leading zeros gone, prerelease label compared case-insensitively.
    /// </summary>
    internal static string NormaliseNuGetVersion(string version)
    {
        var dash = version.IndexOf('-');
        var numeric = dash < 0 ? version : version[..dash];
        var label = dash < 0 ? "" : version[dash..].ToLowerInvariant();

        var parts = numeric.Split('.').Select(p => long.TryParse(p, out var n) ? n.ToString() : p).ToList();
        while (parts.Count < 3) parts.Add("0");
        if (parts.Count == 4 && parts[3] == "0") parts.RemoveAt(3);
        return string.Join(".", parts) + label;
    }

    private static bool SameVersion(string a, string b) =>
        NormaliseNuGetVersion(a) == NormaliseNuGetVersion(b);

    private static readonly Regex LowerBoundRange = new(@"^\[\s*([^,\]\s]+)\s*,\s*\)$");

    /// <summary>
    /// Every problem with a packages.lock.json against the versions a csproj declares. NuGet records a
    /// direct dependency, with its normalised requested range, in each target WITHOUT a runtime
    /// identifier; the <c>&lt;tfm&gt;/&lt;rid&gt;</c> targets hold only runtime-specific assets and are
    /// often empty (Proteus's win-x64 one is <c>{}</c>), so there a package may be absent but never at
    /// a different version.
    /// </summary>
    internal static IReadOnlyList<string> CheckLockFile(string lockJson, IReadOnlyDictionary<string, string> declared)
    {
        var problems = new List<string>();
        using var doc = JsonDocument.Parse(lockJson);
        if (!doc.RootElement.TryGetProperty("dependencies", out var targets))
            return ["lock file has no \"dependencies\" section"];

        var all = targets.EnumerateObject().ToList();
        if (!all.Any(t => !t.Name.Contains('/')))
            problems.Add("lock file has no target without a runtime identifier, so no Direct entries");

        foreach (var target in all)
        {
            var ridLess = !target.Name.Contains('/');
            foreach (var (name, version) in declared)
            {
                var entry = target.Value.EnumerateObject()
                    .FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
                var found = entry.Value.ValueKind == JsonValueKind.Object;
                var where = $"{name} in target {target.Name}";

                if (!found)
                {
                    if (ridLess) problems.Add($"{where}: missing");
                    continue;
                }

                var resolved = entry.Value.TryGetProperty("resolved", out var r) ? r.GetString() ?? "" : "";
                if (!SameVersion(resolved, version))
                    problems.Add($"{where}: resolved {resolved}, declared {version}");

                if (!ridLess) continue;

                var type = entry.Value.TryGetProperty("type", out var t) ? t.GetString() : null;
                if (type != "Direct")
                    problems.Add($"{where}: type {type ?? "(none)"}, expected Direct");

                var requested = entry.Value.TryGetProperty("requested", out var q) ? q.GetString() ?? "" : "";
                var range = LowerBoundRange.Match(requested);
                if (!range.Success)
                    problems.Add($"{where}: requested \"{requested}\" is not a lower-bound range [V, )");
                else if (!SameVersion(range.Groups[1].Value, version))
                    problems.Add($"{where}: requested {requested}, declared {version}");
            }
        }
        return problems;
    }

    /// <summary>
    /// YAML content with comments removed: a line whose first non-blank character is <c>#</c> is dropped,
    /// and a trailing comment (a <c>#</c> at the start or after whitespace, outside quotes) is cut. The
    /// workflows carry long "why" comments, and those must be free to name what the file never does.
    /// A <c>#</c> inside quotes is kept, so a doubtful line is still searched.
    /// </summary>
    internal static IEnumerable<string> NonCommentLines(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            if (line.TrimStart().StartsWith('#')) continue;

            char quote = '\0';
            var cut = line.Length;
            for (var i = 0; i < line.Length; i++)
            {
                var c = line[i];
                if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
                if (c is '"' or '\'') { quote = c; continue; }
                if (c == '#' && (i == 0 || char.IsWhiteSpace(line[i - 1]))) { cut = i; break; }
            }
            yield return line[..cut];
        }
    }

    internal static bool ContainsOutsideComments(IEnumerable<string> lines, string needle) =>
        NonCommentLines(lines).Any(l => l.Contains(needle, StringComparison.OrdinalIgnoreCase));

    public enum UsesKind { NotUses, Local, Docker, PinnedWithComment, PinnedNoComment, Unpinned }

    private static readonly Regex UsesLine = new(@"^\s*(-\s*)?uses:\s*(\S+)(.*)$");
    private static readonly Regex PinnedRef = new(@"^[\w.-]+/[\w.-]+(/[\w./-]+)?@[0-9a-f]{40}$");
    private static readonly Regex VersionComment = new(@"^\s+#\s*v\d+(\.\d+)*");

    /// <summary>
    /// What a workflow line references. Works on the RAW line, because the <c># vX.Y.Z</c> comment is
    /// part of what it checks: a bare SHA tells a reader nothing about which release it is.
    /// </summary>
    internal static UsesKind ClassifyUsesLine(string line)
    {
        if (line.TrimStart().StartsWith('#')) return UsesKind.NotUses;
        var m = UsesLine.Match(line);
        if (!m.Success) return UsesKind.NotUses;

        var target = m.Groups[2].Value.Trim('"', '\'');
        if (target.StartsWith("./")) return UsesKind.Local;
        if (target.StartsWith("docker://")) return UsesKind.Docker;
        if (!PinnedRef.IsMatch(target)) return UsesKind.Unpinned;
        return VersionComment.IsMatch(m.Groups[3].Value) ? UsesKind.PinnedWithComment : UsesKind.PinnedNoComment;
    }

    /// <summary>
    /// GitHub's branch filter patterns, the subset this repo could plausibly use: <c>*</c> is anything
    /// but <c>/</c>, <c>**</c> is anything, <c>?</c> is one character. Any other syntax (<c>!</c>,
    /// <c>[...]</c>, <c>+</c>) counts as a match — the guard then reports too much, never too little.
    /// </summary>
    internal static bool GlobMatches(string pattern, string branch)
    {
        if (pattern.IndexOfAny(['!', '[', ']', '+']) >= 0) return true;

        var rx = new StringBuilder("^");
        for (var i = 0; i < pattern.Length; i++)
        {
            if (pattern[i] == '*' && i + 1 < pattern.Length && pattern[i + 1] == '*') { rx.Append(".*"); i++; }
            else if (pattern[i] == '*') rx.Append("[^/]*");
            else if (pattern[i] == '?') rx.Append('.');
            else rx.Append(Regex.Escape(pattern[i].ToString()));
        }
        return Regex.IsMatch(branch, rx.Append('$').ToString());
    }

    private static readonly string[] BranchEvents = ["push", "pull_request", "pull_request_target"];

    /// <summary>
    /// Events that never follow a push: run by hand or on a timer. Every other event that is not a
    /// branch event (workflow_run, check_suite, create, release, …) counts as running on any branch —
    /// workflow_run in particular fires straight after CodeQL on every push to test.
    /// </summary>
    private static readonly string[] NeverOnPush = ["workflow_dispatch", "schedule"];

    private static int Indent(string line) => line.Length - line.TrimStart().Length;

    private static string Unquote(string s) => s.Trim().Trim('"', '\'');

    /// <summary>Items of a flow list (<c>[a, b]</c>) or a scalar.</summary>
    private static List<string> FlowItems(string value)
    {
        var v = value.Trim();
        if (v.StartsWith('[') && v.EndsWith(']')) v = v[1..^1];
        return v.Split(',').Select(Unquote).Where(s => s.Length > 0).ToList();
    }

    /// <summary>
    /// Whether a workflow runs for a push or pull request on <paramref name="branch"/>. It does unless
    /// every branch event is filtered away from it: a <c>branches</c> list with nothing matching, a
    /// <c>branches-ignore</c> that covers it, or only tag filters (a tag push is not a branch push).
    /// Shorthand <c>on: push</c> / <c>on: [push, …]</c> / <c>- push</c> and an unfiltered event all
    /// match; <c>paths</c> filters do not exclude, since any push could touch those paths. Any event
    /// outside <see cref="BranchEvents"/> and <see cref="NeverOnPush"/> matches too.
    /// </summary>
    internal static bool TriggersOnBranch(IReadOnlyList<string> workflowLines, string branch)
    {
        var lines = NonCommentLines(workflowLines).Select(l => l.TrimEnd()).ToList();
        var on = lines.FindIndex(l => Regex.IsMatch(l, @"^[""']?on[""']?:"));
        if (on < 0) return false;

        var inline = lines[on][(lines[on].IndexOf(':') + 1)..].Trim();
        if (inline.Length > 0)
        {
            if (inline.StartsWith('{')) return true;  // flow mapping: not modelled, so assume the worst
            return FlowItems(inline).Any(e => !NeverOnPush.Contains(e));
        }

        // The block under on: — every following line indented deeper than column 0, plus a YAML
        // indentless sequence (`- push` at column 0), which is still the value of on:.
        var block = new List<string>();
        for (var i = on + 1; i < lines.Count; i++)
        {
            if (lines[i].Trim().Length == 0) continue;
            if (Indent(lines[i]) == 0 && !lines[i].StartsWith('-')) break;
            block.Add(lines[i]);
        }
        if (block.Count == 0) return false;
        var eventIndent = Indent(block[0]);

        for (var i = 0; i < block.Count; i++)
        {
            if (Indent(block[i]) != eventIndent) continue;
            var item = block[i].Trim();
            if (item.StartsWith('-'))  // block sequence of event names: each one unfiltered
            {
                if (!NeverOnPush.Contains(Unquote(item[1..]))) return true;
                continue;
            }
            var m = KeyLine.Match(item);
            if (!m.Success) return true;  // an event line this reader cannot parse: assume the worst
            if (NeverOnPush.Contains(m.Groups[1].Value)) continue;
            if (!BranchEvents.Contains(m.Groups[1].Value)) return true;
            if (m.Groups[2].Value.Trim().Length > 0) return true;  // inline mapping: not modelled

            var body = new List<string>();
            for (var j = i + 1; j < block.Count && Indent(block[j]) > eventIndent; j++) body.Add(block[j]);
            if (EventMatches(body, branch)) return true;
        }
        return false;
    }

    /// <summary>A mapping key, bare or quoted, and its inline value.</summary>
    private static readonly Regex KeyLine = new(@"^[""']?([\w-]+)[""']?:\s*(.*)$");

    private static bool EventMatches(List<string> body, string branch)
    {
        var filters = new Dictionary<string, List<string>>();
        if (body.Count > 0)
        {
            var keyIndent = Indent(body[0]);
            for (var i = 0; i < body.Count; i++)
            {
                if (Indent(body[i]) != keyIndent || body[i].TrimStart().StartsWith('-')) continue;
                var m = KeyLine.Match(body[i].Trim());
                if (!m.Success) continue;

                // Items sit deeper than the key, or at its own indent (an indentless sequence).
                var items = m.Groups[2].Value.Trim().Length > 0 ? FlowItems(m.Groups[2].Value) : [];
                for (var j = i + 1; j < body.Count; j++)
                {
                    var item = body[j].Trim();
                    var deeper = Indent(body[j]) > keyIndent;
                    if (!deeper && !(Indent(body[j]) == keyIndent && item.StartsWith('-'))) break;
                    if (item.StartsWith('-')) items.Add(Unquote(item[1..]));
                }
                filters[m.Groups[1].Value] = items;
            }
        }

        if (filters.TryGetValue("branches", out var branches)) return branches.Any(p => GlobMatches(p, branch));
        if (filters.TryGetValue("branches-ignore", out var ignored)) return !ignored.Any(p => GlobMatches(p, branch));
        if (filters.ContainsKey("tags") || filters.ContainsKey("tags-ignore")) return false;
        return true;
    }


    // ---------------------------------------------------------------------------------------------
    // Repo scanning
    // ---------------------------------------------------------------------------------------------

    /// <summary>Submodules: someone else's projects, which neither Dependabot nor these rules own.</summary>
    private static readonly string[] Submodules = ["Glamourer", "Penumbra.Api"];

    /// <summary>
    /// Repo-owned projects: the .csproj files exactly one directory below the root. Submodules and every
    /// dot-directory are skipped — .claude holds whole nested worktrees in the primary checkout.
    /// </summary>
    private static List<string> RepoOwnedProjects() =>
        Directory.GetDirectories(RepoRoot)
            .Where(d => !Path.GetFileName(d).StartsWith('.') && !Submodules.Contains(Path.GetFileName(d)))
            .SelectMany(d => Directory.GetFiles(d, "*.csproj", SearchOption.TopDirectoryOnly))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

    private static IEnumerable<string> VersionFiles()
    {
        foreach (var p in RepoOwnedProjects()) yield return p;
        foreach (var name in new[] { "Directory.Build.props", "Directory.Packages.props" })
        {
            var p = Path.Combine(RepoRoot, name);
            if (File.Exists(p)) yield return p;
        }
    }

    /// <summary>PackageReference (and central PackageVersion) items, Version as attribute or child.</summary>
    private static List<(string Name, string? Version)> PackageReferences(string file) =>
        XDocument.Load(file).Descendants()
            .Where(e => e.Name.LocalName is "PackageReference" or "PackageVersion")
            .Select(e => ((string?)e.Attribute("Include") ?? (string?)e.Attribute("Update") ?? "(unnamed)",
                (string?)e.Attribute("Version") ?? e.Elements().FirstOrDefault(c => c.Name.LocalName == "Version")?.Value))
            .ToList();

    private static string Rel(string path) => Path.GetRelativePath(RepoRoot, path).Replace('\\', '/');

    private static string[] WorkflowFiles() =>
        Directory.GetFiles(WorkflowsDir, "*.yml").Concat(Directory.GetFiles(WorkflowsDir, "*.yaml"))
            .OrderBy(p => p, StringComparer.Ordinal).ToArray();

    // ---------------------------------------------------------------------------------------------
    // dependabot.yml
    // ---------------------------------------------------------------------------------------------

    private static string DependabotPath => Path.Combine(RepoRoot, ".github", "dependabot.yml");

    /// <summary>The <c>updates:</c> entries, each as its comment-free lines, keyed by ecosystem.</summary>
    private static Dictionary<string, List<string>> DependabotEntries()
    {
        Assert.True(File.Exists(DependabotPath), ".github/dependabot.yml is missing.");
        var entries = new Dictionary<string, List<string>>();
        List<string>? current = null;
        foreach (var line in NonCommentLines(ReadLines(DependabotPath)))
        {
            var m = Regex.Match(line, @"^\s*-\s*package-ecosystem:\s*(\S+)");
            if (m.Success)
            {
                var ecosystem = Unquote(m.Groups[1].Value);
                Assert.False(entries.ContainsKey(ecosystem), $"dependabot.yml lists {ecosystem} twice.");
                entries[ecosystem] = current = [];
            }
            else current?.Add(line);
        }
        return entries;
    }

    private static string? Value(List<string> entry, string key)
    {
        var m = entry.Select(l => Regex.Match(l, $@"^\s*{Regex.Escape(key)}:\s*(\S.*)$")).FirstOrDefault(x => x.Success);
        return m == null ? null : Unquote(m.Groups[1].Value);
    }

    /// <summary>The block-list items under <paramref name="key"/>.</summary>
    private static List<string> ListUnder(List<string> entry, string key)
    {
        var start = entry.FindIndex(l => Regex.IsMatch(l, $@"^\s*{Regex.Escape(key)}:\s*$"));
        if (start < 0) return [];
        var keyIndent = Indent(entry[start]);
        var items = new List<string>();
        for (var i = start + 1; i < entry.Count; i++)
        {
            if (entry[i].Trim().Length == 0) continue;
            if (Indent(entry[i]) <= keyIndent && !entry[i].TrimStart().StartsWith('-')) break;
            var t = entry[i].Trim();
            if (!t.StartsWith('-')) break;
            items.Add(Unquote(t[1..]));
        }
        return items;
    }

    // [AC-security-scans-1] [EDGE-security-scans-7]
    [Fact]
    public void Dependabot_CoversEveryEcosystem_TargetingTest()
    {
        var entries = DependabotEntries();
        Assert.Equal(new[] { "github-actions", "gitsubmodule", "npm", "nuget" },
            entries.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());

        foreach (var (ecosystem, entry) in entries)
        {
            Assert.True(Value(entry, "target-branch") == "test",
                $"dependabot.yml's {ecosystem} entry must set target-branch: test. Without it Dependabot " +
                "opens PRs against the default branch, main — the release branch — and bypasses test.");
            Assert.True(Value(entry, "interval") == "weekly",
                $"dependabot.yml's {ecosystem} entry must run weekly.");
        }
    }

    // [AC-security-scans-1] [EDGE-security-scans-1]
    [Fact]
    public void Dependabot_NugetDirectoriesCoverEveryRepoProject()
    {
        var listed = ListUnder(DependabotEntries()["nuget"], "directories").OrderBy(d => d, StringComparer.Ordinal);
        var expected = RepoOwnedProjects().Select(p => "/" + Rel(Path.GetDirectoryName(p)!)).OrderBy(d => d, StringComparer.Ordinal);
        Assert.True(listed.SequenceEqual(expected),
            "dependabot.yml's nuget directories must be exactly the repo-owned project folders.\n" +
            $"  listed:   {string.Join(", ", listed)}\n  projects: {string.Join(", ", expected)}\n" +
            "A project missing from the list never gets dependency updates.");
    }

    // [AC-security-scans-1]
    [Fact]
    public void Dependabot_IgnoresDalamudSdkPackages()
    {
        var ignored = DependabotEntries()["nuget"]
            .Select(l => Regex.Match(l, @"dependency-name:\s*(\S+)")).Where(m => m.Success)
            .Select(m => Unquote(m.Groups[1].Value)).ToHashSet();

        foreach (var sdk in new[] { "Dalamud.NET.Sdk", "DalamudPackager", "DotNet.ReproducibleBuilds" })
            Assert.True(ignored.Contains(sdk),
                $"dependabot.yml's nuget entry must ignore {sdk}. The Dalamud SDK is tied to " +
                "DalamudApiLevel and bumped by hand with the game; the other two come with it.");
    }

    // [AC-security-scans-1] [EDGE-security-scans-9]
    [Fact]
    public void Dependabot_NpmUsesWorkerAndIncreaseIfNecessary()
    {
        var npm = DependabotEntries()["npm"];
        Assert.Equal("/worker", Value(npm, "directory"));
        Assert.True(Value(npm, "versioning-strategy") == "increase-if-necessary",
            "worker/ has no lockfile, so the default strategy opens PRs that only raise a ^ floor and " +
            "change nothing at deploy time. Keep versioning-strategy: increase-if-necessary.");
    }

    // [AC-security-scans-1]
    [Fact]
    public void Dependabot_SubmodulesLimitedToOneOpenPr() =>
        Assert.True(Value(DependabotEntries()["gitsubmodule"], "open-pull-requests-limit") == "1",
            "Glamourer tracks its testing branch and each IPC bump needs checking in game: keep the " +
            "gitsubmodule entry at open-pull-requests-limit: 1.");


    // ---------------------------------------------------------------------------------------------
    // CodeQL
    // ---------------------------------------------------------------------------------------------

    private static string[] CodeQlLines()
    {
        var path = Path.Combine(WorkflowsDir, "codeql.yml");
        Assert.True(File.Exists(path), ".github/workflows/codeql.yml is missing.");
        return ReadLines(path);
    }

    // [AC-security-scans-2]
    [Fact]
    public void CodeQl_AnalysesAllFourLanguages()
    {
        var lines = NonCommentLines(CodeQlLines()).ToList();
        var pairs = new List<string>();
        string? language = null;
        foreach (var l in lines)
        {
            var lang = Regex.Match(l, @"^\s*-\s*language:\s*(\S+)");
            if (lang.Success) { language = Unquote(lang.Groups[1].Value); continue; }
            var mode = Regex.Match(l, @"^\s*build-mode:\s*(\S+)");
            if (mode.Success && language != null && !mode.Groups[1].Value.Contains("${{"))
            {
                pairs.Add($"{language}/{Unquote(mode.Groups[1].Value)}");
                language = null;
            }
        }

        Assert.Equal(new[] { "actions/none", "c-cpp/manual", "csharp/none", "javascript-typescript/none" },
            pairs.OrderBy(p => p, StringComparer.Ordinal).ToArray());
        Assert.Contains(lines, l => l.Contains("github/codeql-action/init@"));
        Assert.Contains(lines, l => l.Contains("github/codeql-action/analyze@"));
    }

    // [AC-security-scans-2]
    [Fact]
    public void CodeQl_TriggersOnMainAndTestAndSchedule_NeverTags()
    {
        var lines = CodeQlLines();
        Assert.True(TriggersOnBranch(lines, "test"), "codeql.yml must run on pushes and PRs to test.");
        Assert.True(TriggersOnBranch(lines, "main"), "codeql.yml must run on pushes and PRs to main.");
        Assert.True(ContainsOutsideComments(lines, "schedule:") && ContainsOutsideComments(lines, "cron:"),
            "codeql.yml must keep its weekly schedule, so new queries reach old code.");
        Assert.False(ContainsOutsideComments(lines, "tags"),
            "codeql.yml must not trigger on tags: a release tag is the release workflows' business.");
    }

    // [AC-security-scans-2] [EDGE-security-scans-18]
    [Fact]
    public void CodeQl_PermissionsAreReadOnlyExceptSecurityEvents()
    {
        var writes = NonCommentLines(CodeQlLines())
            .Where(l => Regex.IsMatch(l, @":\s*write\b") || l.Contains("write-all"))
            .Select(l => l.Trim())
            .ToArray();
        Assert.True(writes.SequenceEqual(new[] { "security-events: write" }),
            "codeql.yml may write security events and nothing else; it runs on every push to test.\n  " +
            string.Join("\n  ", writes));

        // Explicitly read-only at the top, not just silent: the repo's default token permission is a
        // setting that can change without any diff here.
        var lines = NonCommentLines(CodeQlLines()).Select(l => l.TrimEnd()).ToList();
        var top = lines.FindIndex(l => l == "permissions:");
        Assert.True(top >= 0 && top + 1 < lines.Count && lines[top + 1].Trim() == "contents: read",
            "codeql.yml must declare a top-level permissions: block with contents: read.");
    }

    private static readonly string[] Publishing = ["upload-artifact", "action-gh-release", "gh release", "git push", "wrangler"];

    // [AC-security-scans-2] [EDGE-security-scans-10] [EDGE-security-scans-13] [EDGE-security-scans-18]
    [Fact]
    public void CodeQl_CppBuildIsBuildBat_PublishesNothing()
    {
        var raw = CodeQlLines();
        var lines = NonCommentLines(raw).ToList();

        var build = lines.FindIndex(l => l.Contains(@"Proteus\native\src\build.bat"));
        Assert.True(build >= 0, @"codeql.yml must build the native shim through Proteus\native\src\build.bat.");

        // build.bat copies its DLL over the tracked Proteus/native/proteus_bcn.dll in the runner's
        // workspace, so nothing after it may pick the workspace up: analyze, and only analyze.
        var after = lines.Skip(build + 1).Where(l => Regex.IsMatch(l, @"^\s*-\s*(name|uses|run|id|if|shell):")).ToList();
        Assert.True(after.Count == 1,
            "analyze must be the only step after the native build, which overwrites the tracked " +
            "proteus_bcn.dll in the workspace. Steps after it:\n  " + string.Join("\n  ", after));
        Assert.Contains(lines.Skip(build + 1), l => l.Contains("github/codeql-action/analyze@"));

        Assert.False(ContainsOutsideComments(raw, "/arch"),
            "codeql.yml passes an /arch flag. The shim is baseline x64 only; build.bat says why.");
        foreach (var p in Publishing)
            Assert.False(ContainsOutsideComments(raw, p), $"codeql.yml contains \"{p}\"; it must publish nothing.");

        Assert.Contains(lines, l => Regex.IsMatch(l, @"^\s*fail-fast:\s*false\s*$"));
    }

    // ---------------------------------------------------------------------------------------------
    // NuGet
    // ---------------------------------------------------------------------------------------------

    // [EDGE-security-scans-1]
    [Fact]
    public void RepoOwnedProjects_AreTheExpectedSet() =>
        Assert.Equal(new[] { "Proteus.Refit/Proteus.Refit.csproj", "Proteus.Tests/Proteus.Tests.csproj", "Proteus/Proteus.csproj", "XivLiveMesh/XivLiveMesh.csproj" },
            RepoOwnedProjects().Select(Rel).OrderBy(p => p, StringComparer.Ordinal).ToArray());

    // [AC-security-scans-3] [EDGE-security-scans-12]
    [Fact]
    public void PackageReferences_ArePlainVersions()
    {
        var bad = VersionFiles()
            .SelectMany(f => PackageReferences(f).Where(r => !IsPlainNuGetVersion(r.Version))
                .Select(r => $"{Rel(f)}: {r.Name} = {r.Version ?? "(no version)"}"))
            .ToArray();
        Assert.True(bad.Length == 0,
            "These package references float or use a range. Declare the plain version the lock file " +
            "resolves, so every bump is a reviewable one-line diff:\n  " + string.Join("\n  ", bad));
    }

    // [AC-security-scans-3] [EDGE-security-scans-6] [EDGE-security-scans-12] [EDGE-security-scans-14]
    [Fact]
    public void PackageReferences_DeclaredVersionIsResolvedVersion()
    {
        var problems = new List<string>();
        foreach (var project in RepoOwnedProjects())
        {
            var declared = PackageReferences(project)
                .Where(r => r.Version != null)
                .ToDictionary(r => r.Name, r => r.Version!, StringComparer.OrdinalIgnoreCase);
            if (declared.Count == 0) continue;

            var dir = Path.GetDirectoryName(project)!;
            var lockFile = Path.Combine(dir, "packages.lock.json");
            if (File.Exists(lockFile))
            {
                problems.AddRange(CheckLockFile(File.ReadAllText(lockFile), declared).Select(p => $"{Rel(lockFile)}: {p}"));
                continue;
            }

            var assets = Path.Combine(dir, "obj", "project.assets.json");
            Assert.True(File.Exists(assets),
                $"{Rel(assets)} not found for {Rel(project)}; run `dotnet build`/restore first.");
            using var doc = JsonDocument.Parse(File.ReadAllText(assets));
            var libraries = doc.RootElement.GetProperty("libraries").EnumerateObject()
                .Select(p => p.Name.Split('/')).Where(p => p.Length == 2).ToList();
            foreach (var (name, version) in declared)
                if (!libraries.Any(l => string.Equals(l[0], name, StringComparison.OrdinalIgnoreCase) && SameVersion(l[1], version)))
                    problems.Add($"{Rel(project)}: {name} {version} is not what restore resolved");
        }
        Assert.True(problems.Count == 0,
            "A declared package version is not the one that restores. Run a Release build so the lock " +
            "file is regenerated, and check it changed only the requested range:\n  " + string.Join("\n  ", problems));
    }

    // ---------------------------------------------------------------------------------------------
    // Workflows
    // ---------------------------------------------------------------------------------------------

    // [AC-security-scans-4] [EDGE-security-scans-12]
    [Fact]
    public void Workflows_EveryActionPinnedToCommitShaWithVersionComment()
    {
        var bad = new List<string>();
        var remote = 0;
        foreach (var file in WorkflowFiles())
        {
            var lines = ReadLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var kind = ClassifyUsesLine(lines[i]);
                if (kind is UsesKind.NotUses or UsesKind.Local or UsesKind.Docker) continue;
                remote++;
                if (kind != UsesKind.PinnedWithComment)
                    bad.Add($"{Rel(file)}:{i + 1}: {kind}: {lines[i].Trim()}");
            }
        }
        Assert.True(remote > 0, "No uses: lines found under .github/workflows; the scan is looking in the wrong place.");
        Assert.True(bad.Count == 0,
            "Every action must be pinned to a full commit SHA with a # vX.Y.Z comment. A tag can be moved " +
            "to different code at any time, and a release workflow would run it:\n  " + string.Join("\n  ", bad));
    }

    // [AC-security-scans-6] [EDGE-security-scans-11] [EDGE-security-scans-18]
    [Fact]
    public void Workflows_PushToTestTriggersOnlyReadOnlyWorkflows()
    {
        var onTest = WorkflowFiles().Where(f => TriggersOnBranch(ReadLines(f), "test")).ToArray();
        Assert.True(onTest.Select(Path.GetFileName).SequenceEqual(new[] { "codeql.yml" }),
            "Pushing test is the end of every feature, so only the read-only CodeQL workflow may run on it. " +
            "Workflows that do: " + string.Join(", ", onTest.Select(Path.GetFileName)));

        // secrets.: anything holding a token can publish by hand (curl, gh api) without a release step.
        foreach (var file in onTest)
            foreach (var p in Publishing.Append("contents: write").Append("secrets."))
                Assert.False(ContainsOutsideComments(ReadLines(file), p),
                    $"{Rel(file)} runs on a push to test and contains \"{p}\".");
    }

    // [EDGE-security-scans-14]
    [Fact]
    public void RepoRoot_IsFound()
    {
        Assert.True(File.Exists(Path.Combine(RepoRoot, "proteus.slnx")));
        Assert.True(Directory.Exists(WorkflowsDir));
    }

    // ---------------------------------------------------------------------------------------------
    // Helper theories
    // ---------------------------------------------------------------------------------------------

    // [EDGE-security-scans-2] [EDGE-security-scans-3]
    [Theory]
    [InlineData("*", false)]
    [InlineData("2.*", false)]
    [InlineData("2.3.*", false)]
    [InlineData("1.0.0-*", false)]
    [InlineData("[1.0,2.0)", false)]
    [InlineData("(,2.0]", false)]
    [InlineData("[1.0]", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("2.30.16", true)]
    [InlineData("2.4.660", true)]
    [InlineData("1.2.3.4", true)]
    [InlineData("1.0.0-beta.1", true)]
    public void IsPlainNuGetVersion_Classifies(string? version, bool plain) =>
        Assert.Equal(plain, IsPlainNuGetVersion(version));

    // [EDGE-security-scans-3]
    [Theory]
    [InlineData("1.0", "1.0.0", true)]
    [InlineData("1.2.3.0", "1.2.3", true)]
    [InlineData("1.0.0-Beta", "1.0.0-beta", true)]
    [InlineData("1.0.1", "1.0.0", false)]
    [InlineData("1.2.3.4", "1.2.3", false)]
    public void NormaliseNuGetVersion_Compares(string a, string b, bool same) =>
        Assert.Equal(same, SameVersion(a, b));

    private static string LockEntry(string name, string type, string requested, string resolved) =>
        $"\"{name}\": {{ \"type\": \"{type}\", \"requested\": \"{requested}\", \"resolved\": \"{resolved}\", \"contentHash\": \"x\" }}";

    private static string LockJson(string? ridLess, string? rid)
    {
        var targets = new List<string>();
        if (ridLess != null) targets.Add($"\"net10.0-windows7.0\": {{ {ridLess} }}");
        if (rid != null) targets.Add($"\"net10.0-windows7.0/win-x64\": {{ {rid} }}");
        return $"{{ \"version\": 1, \"dependencies\": {{ {string.Join(", ", targets)} }} }}";
    }

    public static TheoryData<string, string, string, int> LockFileCases()
    {
        var ok = LockEntry("X", "Direct", "[1.2.3, )", "1.2.3");
        return new TheoryData<string, string, string, int>
        {
            { "a: RID-less Direct + empty RID target", LockJson(ok, ""), "1.2.3", 0 },
            { "b: RID target at the same version", LockJson(ok, LockEntry("X", "Direct", "[1.2.3, )", "1.2.3")), "1.2.3", 0 },
            { "c: RID target at a different version", LockJson(ok, LockEntry("X", "Direct", "[1.2.3, )", "1.2.4")), "1.2.3", 1 },
            { "d: missing from the RID-less target", LockJson(LockEntry("Y", "Direct", "[1.0.0, )", "1.0.0"), ""), "1.2.3", 1 },
            { "e: floating requested", LockJson(LockEntry("X", "Direct", "[1.*, )", "1.2.3"), ""), "1.2.3", 1 },
            { "f: resolved differs", LockJson(LockEntry("X", "Direct", "[1.2.3, )", "1.2.4"), ""), "1.2.3", 1 },
            { "g: transitive, not Direct", LockJson(LockEntry("X", "Transitive", "[1.2.3, )", "1.2.3"), ""), "1.2.3", 1 },
            { "h: only a RID target", LockJson(null, ok), "1.2.3", 1 },
            { "i: normalised forms", LockJson(LockEntry("X", "Direct", "[1.0.0, )", "1.0.0"), ""), "1.0", 0 },
            { "j: name differs only in case", LockJson(LockEntry("x", "Direct", "[1.2.3, )", "1.2.3"), ""), "1.2.3", 0 },
            { "k1: requested bracketed exact", LockJson(LockEntry("X", "Direct", "[1.2.3]", "1.2.3"), ""), "1.2.3", 1 },
            { "k2: requested bare version", LockJson(LockEntry("X", "Direct", "1.2.3", "1.2.3"), ""), "1.2.3", 1 },
            { "l: requested lower bound differs", LockJson(LockEntry("X", "Direct", "[1.2.2, )", "1.2.3"), ""), "1.2.3", 1 },
        };
    }

    // [EDGE-security-scans-16] [EDGE-security-scans-6]
    [Theory]
    [MemberData(nameof(LockFileCases))]
    public void CheckLockFile_Classifies(string name, string lockJson, string declared, int problems)
    {
        var found = CheckLockFile(lockJson, new Dictionary<string, string> { ["X"] = declared });
        Assert.True(found.Count == problems, $"{name}: expected {problems} problem(s), got:\n  " + string.Join("\n  ", found));
    }

    // [EDGE-security-scans-18]
    [Theory]
    [InlineData("# never add /arch:AVX2 here", "/arch", false)]
    [InlineData("    # gh release is not used", "gh release", false)]
    [InlineData("      run: echo hi  # wrangler deploy is never run", "wrangler", false)]
    [InlineData("      run: gh release create x", "gh release", true)]
    [InlineData("        wrangler deploy", "wrangler", true)]
    [InlineData("      run: echo \"a # gh release\"", "gh release", true)]
    [InlineData("      run: cl /arch:AVX2 x.c", "/arch", true)]
    [InlineData("      contents: write  # needed", ": write", true)]
    public void NonCommentLines_IgnoresCommentsButCatchesSteps(string line, string needle, bool found) =>
        Assert.Equal(found, ContainsOutsideComments([line], needle));

    // [EDGE-security-scans-4]
    [Theory]
    [InlineData("      - uses: github/codeql-action/init@9b3254085494b55a0f7c4c2b3d2d52cc712ce370 # v4.39.1", UsesKind.PinnedWithComment)]
    [InlineData("        uses: actions/setup-dotnet@a98b56852c35b8e3190ac28c8c2271da59106c68 # v6.0.0", UsesKind.PinnedWithComment)]
    [InlineData("      - uses: actions/checkout@v7", UsesKind.Unpinned)]
    [InlineData("      - uses: actions/checkout@main", UsesKind.Unpinned)]
    [InlineData("      - uses: actions/checkout@3d3c42e # v7.0.1", UsesKind.Unpinned)]
    [InlineData("      - uses: actions/checkout@3D3C42E5AAC5BA805825DA76410C181273BA90B1 # v7.0.1", UsesKind.Unpinned)]
    [InlineData("      - uses: actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1", UsesKind.PinnedNoComment)]
    [InlineData("      - uses: ./.github/actions/x", UsesKind.Local)]
    [InlineData("      - uses: docker://alpine:3", UsesKind.Docker)]
    [InlineData("      # - uses: actions/checkout@v7", UsesKind.NotUses)]
    [InlineData("        run: echo \"uses: x@v1\"", UsesKind.NotUses)]
    public void ClassifyUsesLine_Classifies(string line, UsesKind kind) =>
        Assert.Equal(kind, ClassifyUsesLine(line));

    // [AC-security-scans-6] [EDGE-security-scans-11]
    [Theory]
    [InlineData("*", "test", true)]
    [InlineData("*", "feature/x", false)]
    [InlineData("**", "feature/x", true)]
    [InlineData("test*", "test", true)]
    [InlineData("t*", "test", true)]
    [InlineData("te?t", "test", true)]
    [InlineData("te?t", "tet", false)]
    [InlineData("feature/*", "test", false)]
    [InlineData("releases/**", "test", false)]
    [InlineData("main", "test", false)]
    [InlineData("test", "test", true)]
    [InlineData("!main", "test", true)]
    [InlineData("[tm]est", "test", true)]
    public void GlobMatches_Classifies(string pattern, string branch, bool matches) =>
        Assert.Equal(matches, GlobMatches(pattern, branch));

    // [AC-security-scans-6] [EDGE-security-scans-11]
    [Theory]
    [InlineData("on: push", true)]
    [InlineData("on: [push, pull_request]", true)]
    [InlineData("on:\n  push:\n\njobs:", true)]
    [InlineData("on:\n  push:\n    branches: [ main ]", false)]
    [InlineData("on:\n  push:\n    branches: [main, test]", true)]
    [InlineData("on:\n  push:\n    branches:\n      - test", true)]
    [InlineData("on:\n  push:\n    branches: ['*']", true)]
    [InlineData("on:\n  push:\n    branches: ['**']", true)]
    [InlineData("on:\n  push:\n    branches: ['test*']", true)]
    [InlineData("on:\n  push:\n    branches: ['t*']", true)]
    [InlineData("on:\n  push:\n    branches: ['feature/*']", false)]
    [InlineData("on:\n  push:\n    branches-ignore: [main]", true)]
    [InlineData("on:\n  push:\n    branches-ignore: [test]", false)]
    [InlineData("on:\n  push:\n    branches-ignore: ['t*']", false)]
    [InlineData("on:\n  push:\n    tags:\n      - 'v*'", false)]
    [InlineData("on:\n  pull_request:\n    branches: [main]", false)]
    [InlineData("on:\n  pull_request_target:\n\njobs:", true)]
    [InlineData("on:\n  workflow_dispatch:\n    inputs:\n      tag:\n        required: true", false)]
    [InlineData("on:\n  schedule:\n    - cron: '0 0 * * 1'", false)]
    [InlineData("on:\n  push:\n    paths:\n      - 'worker/**'", true)]
    [InlineData("on:\n  push:\n    branches: [ main ]  # test later", false)]
    [InlineData("on:\n  - push", true)]
    [InlineData("on:\n- push\njobs:", true)]
    [InlineData("on:\n  push:\n    branches:\n    - test", true)]
    [InlineData("on:\n  push:\n    branches:\n    - main\n    paths:\n    - 'x/**'", false)]
    [InlineData("on:\n  'push':", true)]
    [InlineData("on:\n  \"push\":\n    branches: [main]", false)]
    [InlineData("on:\n  - workflow_dispatch", false)]
    [InlineData("on: workflow_run", true)]
    [InlineData("on: [workflow_dispatch, schedule]", false)]
    [InlineData("on:\n  workflow_run:\n    workflows: [CodeQL]\n    types: [completed]", true)]
    [InlineData("on:\n  release:\n    types: [published]", true)]
    [InlineData("on:\n  workflow_dispatch:\n  push:\n    tags: ['v*']", false)]
    public void TriggersOnBranch_Classifies(string workflow, bool matches) =>
        Assert.Equal(matches, TriggersOnBranch(workflow.Split('\n'), "test"));
}
