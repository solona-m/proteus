#if PROTEUS_TEST_HARNESS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace Proteus.Services;

/// <summary>
/// Test harness only (ADR 0002): make the next N moves of Proteus's own output files into place fail as if the game held
/// them — a sharing violation — so QA can drive #91's write retry and its latch on demand. In memory, armed by
/// <c>POST /actions/output-lock {"failMoves":N}</c>; disarmed when exhausted, on release, at put-back, on <c>off</c> and on
/// unload. Compiled out of every CI build.
/// </summary>
internal static class HarnessMoveFault
{
    private static readonly object Gate = new();
    private static List<Regex> patterns = [];
    private static int movesLeft;

    /// <summary>The patterns armed (as given) and how many failures are left; null when disarmed.</summary>
    public static (IReadOnlyList<string> Patterns, int MovesLeft)? State
    {
        get
        {
            lock (Gate)
                return movesLeft > 0 ? (patterns.Select(p => p.ToString()).ToList(), movesLeft) : null;
        }
    }

    /// <summary>Fail the next <paramref name="moves"/> moves whose target's leaf (or "folder/leaf") matches one of
    /// <paramref name="filePatterns"/> (<c>*</c> wildcards, case-insensitive).</summary>
    public static void Arm(IEnumerable<string> filePatterns, int moves)
    {
        lock (Gate)
        {
            patterns = filePatterns.Select(Compile).ToList();
            movesLeft = moves;
        }
    }

    public static void Disarm()
    {
        lock (Gate)
        {
            patterns = [];
            movesLeft = 0;
        }
    }

    /// <summary>Throws a sharing violation for <paramref name="destination"/> while armed and matching, spending one move.</summary>
    public static void TryFail(string destination)
    {
        lock (Gate)
        {
            if (movesLeft <= 0) return;
            var leaf = Path.GetFileName(destination);
            var folder = Path.GetFileName(Path.GetDirectoryName(destination) ?? "");
            if (!patterns.Any(p => p.IsMatch(leaf) || p.IsMatch(folder + "/" + leaf))) return;
            movesLeft--;
            if (movesLeft == 0) patterns = [];
        }
        throw new IOException("test harness: injected move failure", unchecked((int)0x80070020));
    }

    private static Regex Compile(string pattern)
        => new("^" + Regex.Escape(pattern.Replace('\\', '/')).Replace("\\*", ".*") + "$",
               RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
#endif
