using System;
using System.Collections.Generic;
using System.IO;

namespace Proteus.Services;

/// <summary>
/// The game's own body, as a <see cref="BodySizeCatalog"/> the refit cannot tell from a body mod's.
/// <para/>
/// Gear the game ships is fitted to this body, so it is the "made for" side of every refit of a vanilla item — the
/// half that has no mod folder to be read out of. Exactly one size per slot: the game ships one body per race, which
/// is the whole reason its gear needs refitting at all.
/// <para/>
/// The models are EXTRACTED to a folder rather than handed about as bytes because everything downstream speaks file
/// paths — <see cref="BodySizeMatch"/>'s cache is keyed by path, and the detect, validate and plan passes each open
/// their own copy on a worker thread. A path is the smaller thing to provide, and it keeps the game's data reader on
/// the one thread that may touch it.
/// </summary>
internal static class VanillaBodyCatalog
{
    /// <summary>
    /// What stands where a body mod's folder name goes. Two colons cannot begin a Windows folder name, so this can
    /// never collide with a real mod directory, and code that looks a mod up by it fails loudly rather than reading
    /// some other mod.
    /// </summary>
    internal const string Key = "::vanilla";

    /// <summary>The group every option is filed under, so the picker can say where these came from.</summary>
    internal const string GroupName = "The game";

    /// <summary>
    /// Where the extracts live. Deliberately NOT under Penumbra's mods root: these stand for what the game draws with
    /// nothing on top, and a folder there could be redirected, indexed, or picked up as a mod.
    /// </summary>
    internal static string Dir { get; } = Path.Combine(Path.GetTempPath(), "proteus-vanilla-body");

    private static readonly object Gate = new();

    /// <summary>
    /// Throw last session's extracts away. Called once at start-up.
    /// <para/>
    /// This IS the invalidation: a game patch cannot happen while the plugin is loaded, so extracting once per session
    /// is exactly right, and needs no version key that could be got wrong. A folder left behind by a crash is
    /// overwritten rather than trusted.
    /// </summary>
    internal static void CleanUp()
    {
        lock (Gate)
        {
            extracted.Clear();
            try
            {
                if (Directory.Exists(Dir)) Directory.Delete(Dir, recursive: true);
            }
            catch (IOException)
            {
                // Someone has a file open. The extract below overwrites what it needs anyway.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>Race code to the slots already written out this session, so the game is read once per file.</summary>
    private static readonly Dictionary<string, List<BodyOption>> extracted = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The game's body for one race code (<c>"0201"</c>), extracting it on first use.
    /// </summary>
    /// <param name="race">The body's race code, as <see cref="BodySizeCatalog.RaceOf"/> reads it off a game path.</param>
    /// <param name="read">Reads a game path out of the game's own data — <c>TextureLoader.LoadRawFile(null, path)</c>.</param>
    /// <returns>
    /// A catalog over the extract folder, empty when the game has no body for that race. An empty one answers
    /// <see cref="BodySizeCatalog.IsBody"/> false, which is what the picker checks.
    /// </returns>
    internal static BodySizeCatalog Read(string race, Func<string, byte[]?> read)
    {
        lock (Gate)
        {
            if (!extracted.TryGetValue(race, out var options))
            {
                options = Extract(race, read);
                extracted[race] = options;
            }
            return new BodySizeCatalog(Dir, options);
        }
    }

    /// <summary>The game path of one slot of one race's body — the same path a body mod replaces.</summary>
    internal static string GamePathOf(string race, string slot)
        => $"chara/equipment/e0000/model/c{race}e0000{slot}.mdl";

    private static List<BodyOption> Extract(string race, Func<string, byte[]?> read)
    {
        var options = new List<BodyOption>();
        foreach (string slot in Slots)
        {
            string gamePath = GamePathOf(race, slot);
            byte[]? bytes;
            try
            {
                bytes = read(gamePath);
            }
            catch (Exception)
            {
                // The game's data reader throws for a path it does not have as readily as it returns null.
                continue;
            }
            if (bytes is not { Length: > 0 }) continue;

            string rel = $"c{race}e0000{slot}.mdl";
            string full = Path.Combine(Dir, rel);
            try
            {
                Directory.CreateDirectory(Dir);
                PenumbraModMeta.AtomicWrite(full, bytes);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            options.Add(new BodyOption(GroupName, Section: "", Name: SizeName, rel, gamePath, slot));
        }
        return options;
    }

    /// <summary>What the one size is called. A body mod's sizes are named by its author; the game's is just the body.</summary>
    private const string SizeName = "The game's own body";

    private static readonly string[] Slots = ["_top", "_dwn", "_glv", "_sho"];
}
