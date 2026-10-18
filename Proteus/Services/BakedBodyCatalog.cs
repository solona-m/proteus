using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace Proteus.Services;

/// <summary>
/// The bodies a garment was made on, bent into another race the way the garment itself is (see
/// <see cref="OtherRaceRefit"/>): the "made for" side of a refit for a picked race.
/// <para/>
/// A Midlander man's shirt was cut against a Midlander man's body. Baked for a Highlander, it is that shirt as the game
/// would draw it on him; the body it was cut against, bent by the very same deformer and skeleton, is still the body
/// under it, every gap and overlap between the two kept exactly. A body mod's own Highlander sculpt is another shape —
/// close or not — and reading the baked shirt against it would take that difference for the author's intent.
/// <para/>
/// Written to files, like <see cref="VanillaBodyCatalog"/>, because everything downstream speaks paths. Each is named
/// by what went into it, so a bake already made is never made again, and a changed skeleton or deformer makes another.
/// </summary>
internal static class BakedBodyCatalog
{
    /// <summary>Where the bent bodies live — out of Penumbra's mods root for the same reasons as the vanilla extracts.</summary>
    /// <remarks>Settable for the tests alone, which keep their own.</remarks>
    internal static string Dir { get; set; } = Path.Combine(Path.GetTempPath(), "proteus-baked-body");

    private static readonly object Gate = new();

    /// <summary>Throw last session's bodies away. Called once at start-up, beside <see cref="VanillaBodyCatalog.CleanUp"/>.</summary>
    internal static void CleanUp()
    {
        lock (Gate)
        {
            try
            {
                if (Directory.Exists(Dir)) Directory.Delete(Dir, recursive: true);
            }
            catch (IOException)
            {
                // Someone has a file open; a body is rewritten when it is needed anyway.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    /// <paramref name="source"/>'s bodies of the drawn race, bent to <paramref name="to"/>: the same options under the same
    /// headings, filed under the new race so its lists find them. Bodies that cannot be bent are left out, and say why
    /// in <paramref name="failed"/>.
    /// </summary>
    /// <param name="drawn">The race the garment is drawn as, whose bodies the source is read for.</param>
    /// <param name="slots">The slots to bend; the others are never refitted between and cost nothing.</param>
    internal static BodySizeCatalog Of(BodySizeCatalog source, string drawn, ushort to, RacialModelBake.Wearer who,
                                       IEnumerable<string> slots, out List<string> failed)
    {
        failed = [];
        if (!ushort.TryParse(drawn, out ushort from)) return new BodySizeCatalog(Dir, []);

        string wearerKey = Key(who);
        var options = new List<BodyOption>();
        var made = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);   // source file -> baked rel
        foreach (string slot in slots)
            foreach (var option in source.For(slot, drawn).Where(o => BodySizeCatalog.RaceOf(o.GamePath) == drawn))
            {
                string path = source.PathOf(option);
                if (!made.TryGetValue(path, out string? rel))
                {
                    rel = Bake(path, from, to, who, wearerKey, out string why);
                    made[path] = rel;
                    if (rel == null) failed.Add($"{option.FullLabel}: {why}");
                }
                if (rel == null) continue;
                options.Add(option with { Rel = rel, GamePath = RacialModelBake.WithRace(option.GamePath, to) });
            }
        return new BodySizeCatalog(Dir, options);
    }

    private static string? Bake(string path, ushort from, ushort to, RacialModelBake.Wearer who, string wearerKey,
                                out string why)
    {
        why = "";
        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            why = ex.Message;
            return null;
        }

        string rel = $"{Hash(bytes)}-{wearerKey}-c{from:D4}-c{to:D4}.mdl";
        string full = Path.Combine(Dir, rel);
        lock (Gate)
            if (File.Exists(full)) return rel;

        if (RacialModelBake.Bake(bytes, from, to, who.Pbd, who.ParentOf, out why) is not { } baked) return null;
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                PenumbraModMeta.AtomicWrite(full, baked);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                why = ex.Message;
                return null;
            }
        }
        return rel;
    }

    /// <summary>What a wearer bends by, short: its skeleton's parents. The deformer is the game's own, fixed for the session.</summary>
    private static string Key(RacialModelBake.Wearer who)
        => Hash(System.Text.Encoding.UTF8.GetBytes(string.Join("\n",
               who.Parents.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + ">" + p.Value))))[..8];

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes))[..16].ToLowerInvariant();
}
