using System.Linq;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace Proteus.Services;

/// <summary>
/// Resolves a concrete Glasses-slot item to use as the invisible facewear host: Glamourer needs a real row id,
/// and the redirect needs its model set. The lowest row with a real model is picked, so the choice is stable
/// across sessions (needed to detect and clean up our own injection).
/// </summary>
public static class InvisibleGlasses
{
    /// <param name="Variant">The item's material variant: the game asks for the shell's material under
    /// chara/equipment/e{set}/material/v{Variant}/.</param>
    public readonly record struct Identity(ulong ItemId, int ModelSet, int Variant);

    private static readonly object Gate = new();
    private static Identity? cached;
    private static System.Collections.Generic.HashSet<int>? cachedSets;
    private static bool warned;

    /// <summary>
    /// Every facewear model set from the Glasses sheet, which tells a facewear "_met" from head gear sharing the
    /// same path. Null if the sheet isn't readable yet, so the caller skips filtering. Cached.
    /// </summary>
    public static System.Collections.Generic.HashSet<int>? FacewearModelSets(IDataManager data)
    {
        lock (Gate)
        {
            if (cachedSets != null) return cachedSets;
            try
            {
                var sheet = data.GetExcelSheet<Glasses>();
                if (sheet == null) return null;
                var sets = new System.Collections.Generic.HashSet<int>();
                foreach (var row in sheet)
                {
                    int set = (int)row.Model & 0xFFFF;   // Model packs set | variant<<16
                    if (set > 0) sets.Add(set);
                }
                if (sets.Count == 0) return null;
                cachedSets = sets;
                return cachedSets;
            }
            catch { return null; }
        }
    }

    /// <summary>
    /// The chosen (item id, model set), or null if the Glasses sheet is unavailable/empty. Locked (called from
    /// two threads) and memoised on success only, so an early call does not disable the feature.
    /// </summary>
    public static Identity? Resolve(IDataManager data, IPluginLog log)
    {
        lock (Gate)
        {
            if (cached is { } hit) return hit;
            try
            {
                var sheet = data.GetExcelSheet<Glasses>();
                if (sheet == null)
                {
                    WarnOnce(log, "Glasses sheet unavailable");
                    return null;
                }

                foreach (var row in sheet.OrderBy(r => r.RowId))
                {
                    // The Model column packs set | variant<<16, like equipment ItemModelMain.
                    int packed = (int)row.Model;
                    int set = packed & 0xFFFF;
                    if (set <= 0) continue;
                    int variant = (packed >> 16) & 0xFFFF;
                    cached = new Identity(row.RowId, set, variant);
                    log.Information("[Proteus] invisible glasses: using item #{0} (packed {1} -> set e{2:D4}, variant v{3:D4})",
                        row.RowId, packed, set, variant);
                    return cached;
                }
                WarnOnce(log, "no Glasses row with a model found");
                return null;
            }
            catch (System.Exception ex)
            {
                WarnOnce(log, $"sheet read failed: {ex.Message}");
                return null;
            }
        }
    }

    private static void WarnOnce(IPluginLog log, string reason)
    {
        if (warned) return;
        warned = true;
        log.Warning("[Proteus] invisible glasses: {0} — will retry on a later composite", reason);
    }
}
