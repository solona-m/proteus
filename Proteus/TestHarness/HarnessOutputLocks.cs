using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Dalamud.Plugin.Services;
using Proteus.Services;

namespace Proteus.TestHarness;

/// <summary>
/// <c>POST /actions/output-lock</c>'s state (ADR 0002, 2026-11-08 amendment): either read-only handles held on Proteus's
/// own output files (no write or delete sharing, so a replace fails as when the game holds them — #91's real lock), or
/// <see cref="HarnessMoveFault"/> armed for the next N moves. One set at a time; released at expiry, on release, at
/// put-back, on <c>off</c> and on unload. Writes nothing; the handles die with the process. Any thread (locked).
/// </summary>
internal sealed class HarnessOutputLocks(IPluginLog? log) : IDisposable
{
    internal const int MaxMs = 120_000;
    internal const int MaxFiles = 32;
    internal const int MaxFailMoves = 20;
    internal static readonly string[] Folders = ["textures", "materials", "models"];

    private static readonly Regex LeafRe = new(@"^[A-Za-z0-9_.*\-]+$", RegexOptions.CultureInvariant);

    private readonly object gate = new();
    private readonly List<(string File, FileStream Handle)> held = [];
    private bool moveFaultArmed;
    private Timer? expiry;
    private DateTime? untilUtc;

    /// <summary>Whether a lock or an armed move fault stands.</summary>
    public bool Active
    {
        get
        {
            lock (gate) return held.Count > 0 || (moveFaultArmed && HarnessMoveFault.State != null);
        }
    }

    /// <summary>
    /// Null when <paramref name="pattern"/> is a path into Proteus's own output: one of <see cref="Folders"/>, a slash, and
    /// a leaf of plain characters that may hold <c>*</c>. Else why not.
    /// </summary>
    internal static string? Validate(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return "a file pattern is empty";
        if (Path.IsPathRooted(pattern) || pattern.Contains("..", StringComparison.Ordinal) || pattern.Contains('\\'))
            return $"{pattern} must be relative, with forward slashes and no '..'";
        var parts = pattern.Split('/');
        if (parts.Length != 2) return $"{pattern} must be folder/leaf";
        if (!Folders.Contains(parts[0], StringComparer.Ordinal)) return $"{pattern}: the folder must be textures, materials or models";
        if (!LeafRe.IsMatch(parts[1])) return $"{pattern}: the leaf may hold only letters, digits, _ . - and *";
        return null;
    }

    /// <summary>The existing files under <paramref name="root"/> the patterns match, relative ("textures/ss_1_norm.tex").</summary>
    internal static List<string> Match(string root, IEnumerable<string> patterns)
    {
        var hits = new List<string>();
        foreach (var p in patterns)
        {
            var parts = p.Split('/');
            var dir = Path.Combine(root, parts[0]);
            if (!Directory.Exists(dir)) continue;
            foreach (var f in Directory.EnumerateFiles(dir, parts[1]))
            {
                var name = Path.GetFileName(f);
                if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;   // a write in progress
                var rel = parts[0] + "/" + name;
                if (!hits.Contains(rel, StringComparer.OrdinalIgnoreCase)) hits.Add(rel);
            }
        }
        return hits;
    }

    /// <summary>
    /// Holds read-only handles on the matched files for <paramref name="ms"/>. Null (nothing held) when nothing matched; a
    /// file that cannot be opened is skipped. Throws <see cref="InvalidOperationException"/> when a set stands already.
    /// </summary>
    public (List<(string File, long Bytes)> Held, DateTime UntilUtc)? Lock(string root, IReadOnlyList<string> patterns, int ms)
    {
        lock (gate)
        {
            if (held.Count > 0 || (moveFaultArmed && HarnessMoveFault.State != null)) throw new InvalidOperationException("locks-held");
            var files = Match(root, patterns).Take(MaxFiles).ToList();
            var opened = new List<(string, long)>();
            foreach (var rel in files)
            {
                try
                {
                    var fs = new FileStream(Path.Combine(root, rel), FileMode.Open, FileAccess.Read, FileShare.Read);
                    held.Add((rel, fs));
                    opened.Add((rel, fs.Length));
                }
                catch (Exception ex) { log?.Debug("[Proteus] test harness: could not hold {0} ({1})", rel, ex.Message); }
            }
            if (opened.Count == 0) return null;
            Arm(ms);
            log?.Information("[Proteus] test harness: holding {0} output file(s) for {1}ms", opened.Count, ms);
            return (opened, untilUtc!.Value);
        }
    }

    /// <summary>Fails the next <paramref name="moves"/> moves of matching output files (expiring after
    /// <see cref="MaxMs"/>). Throws <see cref="InvalidOperationException"/> when a set stands already.</summary>
    public DateTime ArmMoves(IReadOnlyList<string> patterns, int moves)
    {
        lock (gate)
        {
            if (held.Count > 0 || (moveFaultArmed && HarnessMoveFault.State != null)) throw new InvalidOperationException("locks-held");
            HarnessMoveFault.Arm(patterns, moves);
            moveFaultArmed = true;
            Arm(MaxMs);
            log?.Information("[Proteus] test harness: the next {0} move(s) of {1} will fail", moves, string.Join(", ", patterns));
            return untilUtc!.Value;
        }
    }

    private void Arm(int ms)
    {
        untilUtc = DateTime.UtcNow.AddMilliseconds(ms);
        expiry?.Dispose();
        expiry = new Timer(_ => ReleaseAll("expired"), null, ms, Timeout.Infinite);
    }

    /// <summary>Closes every handle and disarms the move fault. Returns each file (released) and what the fault had left.</summary>
    public (List<(string File, string Result)> Released, int MovesLeft, string MoveFault) ReleaseAll(string why = "released")
    {
        lock (gate)
        {
            var released = new List<(string, string)>();
            foreach (var (rel, fs) in held)
            {
                try { fs.Dispose(); released.Add((rel, "released")); }
                catch { released.Add((rel, "gone")); }
            }
            held.Clear();
            int left = HarnessMoveFault.State?.MovesLeft ?? 0;
            string fault = moveFaultArmed ? "disarmed" : "none";
            if (moveFaultArmed) HarnessMoveFault.Disarm();
            moveFaultArmed = false;
            expiry?.Dispose();
            expiry = null;
            untilUtc = null;
            if (released.Count > 0 || fault == "disarmed")
                log?.Information("[Proteus] test harness: output locks {0} ({1} file(s), move fault {2})", why, released.Count, fault);
            return (released, left, fault);
        }
    }

    public void Dispose() => ReleaseAll("released at unload");
}
