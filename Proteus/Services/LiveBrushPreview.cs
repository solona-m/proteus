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
    private int generation;

    /// <summary>The last generation <see cref="End"/> took down; a push at or below it must not apply.</summary>
    private int endedAt;

    /// <summary>A preview is being written or applied.</summary>
    public bool Busy => inFlight is { IsCompleted: false };

    /// <summary>A temporary redirect is in place and has to be taken down.</summary>
    public bool Active { get; private set; }

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
        int gen = ++generation;
        var file = Path.Combine(Dir, $"{Environment.ProcessId}-{gen:D6}.mdl");

        inFlight = Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllBytes(file, model);

                bool applied = Plugin.Framework.RunOnFrameworkThread(() =>
                {
                    // Taken down while this was being written: applying it now would put back what End removed.
                    if (gen <= endedAt) return false;
                    var map = gamePaths.ToDictionary(p => p, _ => file, StringComparer.OrdinalIgnoreCase);
                    if (!penumbra.SetPlayerTemporaryMod(Tag, map, Priority)) return false;
                    Active = true;

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
        // Not waited on (the push finishes on this framework thread); the generation mark makes it stand down.
        endedAt = generation;
        if (!Active) return;
        Active = false;

        penumbra.RemovePlayerTemporaryMod(Tag, Priority);
        if (redraw) compositor.RedrawForChangedModel();

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
