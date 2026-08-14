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
    /// <param name="Variant">The item's material variant. The shell's material is referenced from the model
    /// variant-relatively, so the game asks for it under chara/equipment/e{set}/material/v{Variant}/. Needed
    /// because the PENDING-injection host (see SecondSkinService.ChooseHosts) builds the shell before the
    /// pair is equipped, so the live resource tree cannot answer for it and a v0001 guess renders nothing.</param>
    public readonly record struct Identity(ulong ItemId, int ModelSet, int Variant);

    private static readonly object Gate = new();
    private static Identity? cached;
    private static System.Collections.Generic.HashSet<int>? cachedSets;
    private static bool warned;

    /// <summary>
    /// Every facewear/glasses MODEL SET (the e#### the game loads as c{cc}e{set}_met.mdl), from the Glasses
    /// sheet. Head equipment (helmets/hats) share the "_met" model path but are a different slot and are NOT
    /// in this sheet — so this set is how the second skin tells a facewear "_met" (a valid host) from a head
    /// one (which it must ignore, hosting on the facewear slot instead). Returns null if the sheet isn't
    /// readable yet, so the caller skips filtering rather than dropping every candidate. Cached (static data).
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
