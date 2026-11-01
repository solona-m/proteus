using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Proteus.Services;

namespace Proteus.TestHarness;

/// <summary>What a scrubbed name was.</summary>
internal enum NameKind { Collection, Character }

/// <summary>
/// Every collection and character name seen this harness session, kept so <c>/log</c> can scrub them even after a
/// relog or a character switch (the plugin logs collection names, which are often character names). Thread-safe.
/// </summary>
internal sealed class SessionNames
{
    /// <summary>Penumbra's built-in collection: not personal, and scrubbing it would garble every "default" in the log.</summary>
    internal const string BuiltInDefault = "Default";
    internal const int MinLength = 3;

    private readonly object gate = new();
    private readonly Dictionary<string, NameKind> names = new(StringComparer.OrdinalIgnoreCase);
    private List<(Regex Pattern, string Replacement)>? replacements;

    public void Add(string? name, NameKind kind)
    {
        if (name == null) return;
        name = name.Trim();
        if (name.Length < MinLength) return;
        if (kind == NameKind.Collection && string.Equals(name, BuiltInDefault, StringComparison.OrdinalIgnoreCase)) return;
        lock (gate)
        {
            if (names.ContainsKey(name)) return;
            names[name] = kind;
            replacements = null;
        }
    }

    public void AddAll(IEnumerable<string>? collections, string? character)
    {
        if (collections != null) foreach (var c in collections) Add(c, NameKind.Collection);
        Add(character, NameKind.Character);
    }

    /// <summary>Every known name replaced as a whole token: "Rue" goes, "true" and "Rue_S" stay.</summary>
    public string Scrub(string text)
    {
        List<(Regex Pattern, string Replacement)> list;
        lock (gate)
        {
            // Longest first, so "Rue Mod" is replaced before "Rue" could split it.
            replacements ??= names.OrderByDescending(n => n.Key.Length)
                                  .Select(n => (new Regex(@"(?<![A-Za-z0-9_])" + Regex.Escape(n.Key) + @"(?![A-Za-z0-9_])",
                                                          RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
                                                n.Value == NameKind.Collection ? "<COLLECTION>" : "<CHARACTER>"))
                                  .ToList();
            list = replacements;
        }
        foreach (var (p, r) in list) text = p.Replace(text, r);
        return text;
    }

    internal int Count { get { lock (gate) return names.Count; } }
}

/// <summary>
/// The plugin's log lines while the harness listens, numbered from 1 per session, the newest
/// <see cref="Capacity"/> kept. Scrubbed on read (the Windows user name, then every session name), never stored
/// scrubbed: a name learned later still comes out of an earlier line.
/// </summary>
internal sealed class LogRing(SessionNames names, int capacity = LogRing.DefaultCapacity)
{
    internal const int DefaultCapacity = 10_000;
    internal const int DefaultMax = 200;
    internal const int MaxMax = 2000;

    public int Capacity { get; } = capacity;
    private readonly object gate = new();
    private readonly Queue<(long Seq, string Text)> lines = new();
    private long last;

    public void Add(string text)
    {
        lock (gate)
        {
            lines.Enqueue((++last, text));
            while (lines.Count > Capacity) lines.Dequeue();
        }
    }

    /// <summary>A page of lines.</summary>
    /// <param name="Next">The cursor to pass as <c>since</c> next time: the last line returned, or <c>since</c>
    /// unchanged when none.</param>
    /// <param name="Oldest">The oldest line still held (0 when none).</param>
    /// <param name="Dropped">Lines after <c>since</c> were lost to the ring before this read.</param>
    internal sealed record Page(long Next, long Oldest, bool Dropped, IReadOnlyList<(long Seq, string Text)> Lines);

    /// <param name="since">Lines after this one; null for the newest <paramref name="max"/>.</param>
    public Page Read(long? since, int max)
    {
        max = Math.Clamp(max, 1, MaxMax);
        List<(long Seq, string Text)> picked;
        long oldest, newest;
        lock (gate)
        {
            oldest = lines.Count > 0 ? lines.Peek().Seq : 0;
            newest = last;
            picked = since is { } s
                ? lines.Where(l => l.Seq > s).Take(max).ToList()
                : lines.Skip(Math.Max(0, lines.Count - max)).ToList();
        }
        bool dropped = since is { } from && oldest > 0 && from < oldest - 1;
        long next = picked.Count > 0 ? picked[^1].Seq : since ?? newest;
        var scrubbed = picked.Select(l => (l.Seq, names.Scrub(LogAnonymizer.Scrub(l.Text)))).ToList();
        return new Page(next, oldest, dropped, scrubbed);
    }
}
