using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using Proteus.Interop;

namespace Proteus.Services;

/// <summary>
/// Shows a brush edit on the character while it is being painted, without redrawing the character.
/// Each preview is written to a new file (the game caches models by resolved path) and redirected with a
/// Penumbra temporary mod, then Glamourer reloads the gear in place. One preview at a time; a push while one
/// is in flight is refused.
/// </summary>
internal sealed class LiveBrushPreview(PenumbraBridge penumbra, CompositorService compositor, IPluginLog log)
{
    private const string Tag = "Proteus live brush";

    /// <summary>Above every regular mod, so the preview wins the game path whatever else redirects it.</summary>
    private const int Priority = int.MaxValue;

    /// <summary>Preview files kept after they are replaced, since the game may still be reading one asynchronously.</summary>
    private const int KeepFiles = 4;

    private static readonly string Dir = Path.Combine(Path.GetTempPath(), "proteus-live-brush");

    private readonly Queue<string> files = new();
    private Task? inFlight;

    /// <summary>
    /// Guards <see cref="generation"/>, <see cref="endedAt"/> and <see cref="holding"/>. A push applies on the
    /// framework thread, but the unload can call <see cref="End"/> from another: without it a push could pass the
    /// <see cref="endedAt"/> check, End then find nothing to remove, and the push put its redirect on for good.
    /// </summary>
    private readonly object gate = new();

    private int generation;

    /// <summary>The last generation <see cref="End"/> took down; a push at or below it must not apply.</summary>
    private int endedAt;

    /// <summary>
    /// Every collection a redirect was put into and not yet taken out of, each from just before it is asked for. Removed
    /// from THOSE collections: the player's can change under a preview (a zone's assignment, a design), and removing
    /// from the new one left the old at <see cref="Priority"/> until the game restarted. One that will not let go stays
    /// here, and every push and End tries it again.
    /// </summary>
    private readonly HashSet<Guid> holding = [];

    /// <summary>A preview is being written or applied.</summary>
    public bool Busy => inFlight is { IsCompleted: false };

    /// <summary>A temporary redirect may be in place and has to be taken down.</summary>
    public bool Active { get { lock (gate) return holding.Count > 0; } }

    /// <summary>Glamourer could not reload gear in place; the caller should fall back to redrawing.</summary>
    public bool Unsupported { get; private set; }

    /// <summary>
    /// Whether previews can reach this kind of part in place. Never for hair, face, ears or tail: Glamourer's gear
    /// reload does not touch customization, and forcing <c>Human.UpdateDrawData</c> changes the hairstyle.
    /// </summary>
    public bool UnsupportedFor(bool customizePart) => customizePart || Unsupported;

    /// <summary>The model's game path is a customization part rather than gear: hair, face, ears or tail.</summary>
    public static bool IsCustomizePart(string gamePath)
        => gamePath.Contains("/obj/hair/", StringComparison.OrdinalIgnoreCase)
        || gamePath.Contains("/obj/face/", StringComparison.OrdinalIgnoreCase)
        || gamePath.Contains("/obj/tail/", StringComparison.OrdinalIgnoreCase)
        || gamePath.Contains("/obj/zear/", StringComparison.OrdinalIgnoreCase);

    /// <summary>Put <paramref name="model"/> on the character in place of the files at <paramref name="gamePaths"/>.</summary>
    /// <param name="customizePart">The model is hair, face, ears or tail — see <see cref="IsCustomizePart"/>.</param>
    /// <returns>False when a preview is still in flight.</returns>
    public bool Push(byte[] model, IReadOnlyCollection<string> gamePaths, bool customizePart)
    {
        if (Busy || gamePaths.Count == 0) return false;
        int gen;
        lock (gate) gen = ++generation;
        var file = Path.Combine(Dir, $"{Environment.ProcessId}-{gen:D6}.mdl");

        inFlight = Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllBytes(file, model);

                bool applied = Plugin.Framework.RunOnFrameworkThread(() =>
                {
                    lock (gate)
                    {
                        // Taken down while this was being written: applying it now would put back what End removed.
                        if (gen <= endedAt) return false;
                        if (penumbra.GetPlayerCollectionId() is not { } collection) return false;

                        // The player moved to another collection since an earlier push: those still have the old preview.
                        holding.RemoveWhere(c => c != collection && penumbra.RemoveTemporaryMod(Tag, c, Priority));

                        // Recorded before asking, so End removes it even if Penumbra took it and still reported failure.
                        holding.Add(collection);
                        var map = gamePaths.ToDictionary(p => p, _ => file, StringComparer.OrdinalIgnoreCase);
                        if (!penumbra.SetTemporaryMod(Tag, collection, map, Priority)) return false;
                    }

                    // The gear reloads in place to pick the new file up — see ReloadGearInPlace.
                    if (!compositor.ReloadGearInPlace())
                    {
                        if (!Unsupported) log.Warning("[Proteus] live brush: Glamourer cannot reload gear in place; previews fall back to redraws");
                        Unsupported = true;
                    }
                    return true;
                }).GetAwaiter().GetResult();

                lock (files)
                {
                    files.Enqueue(file);
                    while (files.Count > KeepFiles) TryDelete(files.Dequeue());
                }
                if (!applied && gen > endedAt) log.Warning("[Proteus] live brush: Penumbra refused the preview redirect");
            }
            catch (Exception ex)
            {
                log.Warning(ex, "[Proteus] live brush: preview failed");
                TryDelete(file);
            }
        });
        return true;
    }

    /// <summary>
    /// Take the preview down, so the character draws the mod's own file again.
    /// </summary>
    /// <param name="redraw">Redraw afterwards, the only sure way to replace the game's cached copy. False on teardown.</param>
    public void End(bool redraw)
    {
        // Not waited on (the push finishes on the framework thread, which may be this one); the generation mark makes
        // it stand down, and the gate makes the mark and the redirect it may already have added one reading.
        Guid[] held;
        int mark;
        lock (gate)
        {
            mark = endedAt = generation;
            if (holding.Count == 0) return;
            held = [.. holding];
        }

        var gone = held.Where(c => penumbra.RemoveTemporaryMod(Tag, c, Priority)).ToList();
        foreach (var stuck in held.Except(gone))
            log.Warning("[Proteus] live brush: Penumbra did not take the preview down from collection {0}", stuck);

        // A push begun since may have added its own redirect after those removals; then it is still Active, and its
        // file is in use.
        bool allGone;
        lock (gate)
        {
            if (generation != mark) return;
            holding.ExceptWith(gone);
            allGone = holding.Count == 0;
        }
        if (gone.Count > 0 && redraw) compositor.RedrawForChangedModel();

        // A collection that would not let go still points at the preview files, so they stay; the next End tries again.
        if (!allGone) return;

        lock (files)
            while (files.Count > 0) TryDelete(files.Dequeue());
    }

    /// <summary>A loaded model's file is one of these previews — the garment being brushed, drawn from its preview.</summary>
    public static bool IsPreviewFile(string path)
        => BodyShapeReader.PathKey(path).StartsWith(BodyShapeReader.PathKey(Dir), StringComparison.Ordinal);

    /// <summary>Clear preview files a previous session left behind — a crash or an unload mid-preview.</summary>
    public static void CleanUp()
    {
        try
        {
            if (!Directory.Exists(Dir)) return;
            foreach (var f in Directory.GetFiles(Dir, "*.mdl")) TryDelete(f);
        }
        catch { /* temp space; nothing depends on it being tidy */ }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* still open in the game; left for the next CleanUp */ }
    }
}
