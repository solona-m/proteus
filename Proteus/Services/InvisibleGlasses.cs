using System.Linq;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace Proteus.Services;

/// <summary>
/// Resolves a concrete Glasses-slot item to use as the invisible facewear host: Glamourer needs a real
/// Glasses-sheet row id to equip (fabricated ids are rejected), and the redirect needs that row's model set
/// (the e#### the game loads as c{cc}e{set}_met.mdl). The shell REPLACES that model, so the item is never
/// seen — any valid row works; we pick the lowest row with a real model deterministically so the choice is
/// stable across sessions (needed to detect/clean up our own injection).
/// </summary>
public static class InvisibleGlasses
{
    public readonly record struct Identity(ulong ItemId, int ModelSet);

    private static readonly object Gate = new();
    private static Identity? cached;
    private static bool warned;

    /// <summary>
    /// The chosen (item id, model set), or null if the Glasses sheet is unavailable/empty. Called from both
    /// the background composite thread and the framework thread (disable/unload cleanup), so it's locked —
    /// and only a SUCCESSFUL resolve is memoised. Caching a failure would permanently disable the feature
    /// for the session if the first call landed before the sheet was readable; the warning is one-shot so
    /// retrying can't spam the log.
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
                    // The Model column packs set | variant<<16 (same as equipment ItemModelMain), so the
                    // actual equipment set is the low 16 bits — e.g. 71037 (0x1157D) → set 5501, variant 1.
                    int packed = (int)row.Model;
                    int set = packed & 0xFFFF;
                    if (set <= 0) continue;
                    cached = new Identity(row.RowId, set);
                    log.Information("[Proteus] invisible glasses: using item #{0} (packed {1} -> set e{2:D4})",
                        row.RowId, packed, set);
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
