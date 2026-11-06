using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dalamud.Plugin.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Proteus.Services;

namespace Proteus.TestHarness;

/// <summary>What a binding put-back did: Glamourer state, each permanent mod setting, the live binding state.</summary>
/// <param name="Glamourer">restored / partial / changed / failed / none / skipped-settling.</param>
/// <param name="Live">restored / changed / skipped-inconsistent / failed / none / skipped-settling.</param>
internal sealed record BindingPutBack(string Glamourer, IReadOnlyList<UndoEntry> Permanent, string Live, string? Record, bool Restored);

/// <summary>A design apply between its first and final framework hop.</summary>
internal sealed record PendingApply(Guid Design, Guid? ActiveBefore, long AppliedCountBefore, Guid? Collection,
                                    IReadOnlyDictionary<string, PermanentSetting>? PermanentBefore);

/// <summary>
/// The design-binding half of the harness's session changes (ADR 0002): Glamourer design applies, live binding edits and
/// permanent Proteus-mod settings, each undone by put-back only while it is still what the harness left. Before the first
/// change that outlives the game (a design apply's restore, a permanent setting), the Before values are written to
/// <c>backups/&lt;session&gt;/binding.json</c> for manual recovery should put-back never run. Never writes the binding store.
/// Framework thread only.
/// </summary>
internal sealed class HarnessBindings(IHarnessGame game, Func<string, string> scrub, IPluginLog? log, string? recordRoot,
                                      string session, int build)
{
    private JObject? glamourerBefore, glamourerAfter;
    private DesignBindingService.Carriers carriersBefore;
    private HarnessLiveSnapshot? liveBefore;
    private Guid? liveAfterActive;
    private Guid? collection;
    private readonly Dictionary<string, (PermanentSetting Before, PermanentSetting After)> permanent = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Guid> applied = [];
    private int edits, cleared;
    private string? recordPath;
    private int recordIndex;
    private bool recordHasGlamourer;
    private DateTime? since;

    public bool HasSession => glamourerBefore != null || liveBefore != null || permanent.Count > 0;

    // ── apply ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The first hop of a design apply: the checks, the record, the Before readings, then Glamourer's apply. Null error
    /// with a pending apply on success.
    /// </summary>
    public (ChangeResult? Error, PendingApply? Pending) BeginApply(Guid design, bool allowAutoWriters, bool equipsRecorded)
    {
        if (!game.PlayerPresent()) return (ChangeResult.Fail(409, "not-logged-in", "there is no character to apply a design to"), null);
        if (game.Designs() is not { } designs) return (ChangeResult.Fail(503, "glamourer-unavailable", "Glamourer's designs cannot be read"), null);
        if (!designs.ContainsKey(design)) return (ChangeResult.Fail(404, "design-not-found", $"no Glamourer design {design}"), null);
        if (game.ShaderUpgrade().Busy) return (ChangeResult.Fail(409, "shader-upgrade-busy", "the gear shader upgrade is working"), null);
        if (game.VariantRepoint().Busy) return (ChangeResult.Fail(409, "variant-repoint-busy", "Gear variants is working"), null);
        if (game.HairPort().Busy) return (ChangeResult.Fail(409, "hair-port-busy", "the automatic hair path is working"), null);
        if (game.PresetsPinned())
            return (ChangeResult.Fail(409, "presets-pinned", "a per-mod preset is pinned; a design apply would drop it for good. Unpin it first"), null);
        if (equipsRecorded)
            return (ChangeResult.Fail(409, "put-back-first", "harness equips are recorded; put back before applying a design"), null);
        var writers = game.AutoWritersOn();
        if (writers.Count > 0 && !allowAutoWriters)
            return (ChangeResult.Fail(409, "auto-writers-on",
                $"{string.Join(", ", writers)} switched on: they write into worn mods after a design apply and put-back cannot undo that. "
              + "Pass \"allowAutoWriters\": true and clean up as adversarial-qa.md says"), null);
        if (game.GlamourerState() is not { } state || game.DesignJson(design) is not { } designJson)
            return (ChangeResult.Fail(503, "glamourer-unavailable", "Glamourer's state or the design cannot be read"), null);
        if (ModelChange(designJson, state, hairToo: writers.Contains("auto hair port")) is { } field)
            return (ChangeResult.Fail(409, "model-change", $"the design changes {field}: a model change is never applied by the harness"), null);

        var coll = game.PlayerCollection();
        var permBefore = coll is { } c ? game.PermanentSettings(c) : null;
        var carriers = game.CarriersNow();
        var live = game.LiveSnapshot();
        if (EnsureRecord(glamourerBefore ?? state, glamourerBefore == null ? carriers : carriersBefore, liveBefore ?? live, coll, permBefore) is { } failed)
            return (failed, null);

        bool first = glamourerBefore == null;
        var countBefore = game.DesignAppliedCount;
        if (game.ApplyDesign(design) is { } ec)
        {
            // Nothing changed: a record written for this attempt alone must not stand for a later session's Before.
            if (!HasSession && recordPath != null) { recordPath = null; recordHasGlamourer = false; recordIndex--; }
            return (ChangeResult.Fail(409, "glamourer-refused", $"Glamourer refused the apply: {ec}"), null);
        }

        if (first) { glamourerBefore = state; carriersBefore = carriers; }
        liveBefore ??= live;
        collection ??= coll;
        since ??= DateTime.UtcNow;
        log?.Information("[Proteus] test harness: applied Glamourer design {0}", design);
        return (null, new PendingApply(design, live.Active, countBefore, coll, permBefore));
    }

    /// <summary>Whether the deferred work an apply sets off has gone quiet. Framework thread.</summary>
    public bool Quiet()
        => game.CompositorIdle() && !game.AutoRefit().Busy && !game.ShaderUpgrade().Busy && !game.VariantRepoint().Busy
        && !game.HairPort().Busy;

    /// <summary>The final hop: the After readings, the permanent settings the apply's restore wrote, and the answer.</summary>
    public ChangeResult FinishApply(PendingApply p, bool settled)
    {
        glamourerAfter = game.GlamourerState() ?? glamourerAfter;
        var live = game.LiveSnapshot();
        liveAfterActive = live.Active;
        applied.Add(p.Design);
        if (p.Collection is { } c && p.PermanentBefore is { } before && game.PermanentSettings(c) is { } after)
            foreach (var mod in before.Keys.Union(after.Keys, StringComparer.OrdinalIgnoreCase))
            {
                var was = before.GetValueOrDefault(mod) ?? PermanentSetting.Inherits;
                var now = after.GetValueOrDefault(mod) ?? PermanentSetting.Inherits;
                if (!was.Equals(now)) NotePermanent(mod, was, now);
            }

        long echo = game.EchoMsLeft();
        return ChangeResult.Ok(new Dictionary<string, object?>
        {
            ["design"] = p.Design, ["activeBefore"] = p.ActiveBefore, ["activeAfter"] = live.Active,
            ["finalizedSeen"] = game.DesignAppliedCount > p.AppliedCountBefore, ["settled"] = settled,
            ["echoWindowOpen"] = echo > 0, ["echoMsLeft"] = echo,
        });
    }

    /// <summary>
    /// The applied Customize field that would change the character's model (race, gender, clan, body type; and the
    /// hairstyle when <paramref name="hairToo"/>), or null.
    /// </summary>
    internal static string? ModelChange(JObject design, JObject state, bool hairToo)
    {
        string[] fields = hairToo ? ["Race", "Gender", "Clan", "BodyType", "Hairstyle"] : ["Race", "Gender", "Clan", "BodyType"];
        foreach (var f in fields)
        {
            if (design["Customize"]?[f] is not JObject d || d["Apply"]?.ToObject<bool>() != true || d["Value"] is not { } v) continue;
            if (!JToken.DeepEquals(v, state["Customize"]?[f]?["Value"])) return f;
        }
        return null;
    }

    // ── permanent mod settings ──────────────────────────────────────────────

    /// <summary>Write a Proteus mod's permanent enabled/priority, recording its Before first.</summary>
    public ChangeResult WritePermanent(Guid coll, string modDir, bool? enabled, int? priority)
    {
        if (!game.ProteusDirs().Contains(modDir))
            return ChangeResult.Fail(409, "not-a-proteus-mod", $"{modDir} is not a Proteus mod; only Proteus mods take permanent settings");
        if (game.PermanentSettings(coll) is not { } all)
            return ChangeResult.Fail(503, "penumbra-unavailable", "Penumbra could not read the mod's permanent settings");
        var cur = all.GetValueOrDefault(modDir) ?? PermanentSetting.Inherits;
        if (EnsureRecord(glamourerBefore, carriersBefore, liveBefore ?? game.LiveSnapshot(), coll, all) is { } failed) return failed;

        var want = new PermanentSetting(enabled ?? cur.Enabled, priority ?? cur.Priority, cur.Options, Own: true);
        if (game.WritePermanent(coll, modDir, want) is { } ec)
            return ChangeResult.Fail(409, "penumbra-refused", $"Penumbra refused the setting: {ec}");
        var now = game.PermanentSettings(coll)?.GetValueOrDefault(modDir) ?? want;
        NotePermanent(modDir, cur, now);
        collection ??= coll;
        since ??= DateTime.UtcNow;
        game.RequestRedraw();
        log?.Information("[Proteus] test harness: {0} permanently enabled={1} priority={2}", modDir, now.Enabled, now.Priority);
        return ChangeResult.Ok(new Dictionary<string, object?>
        {
            ["mod"] = modDir, ["collection"] = coll, ["permanent"] = true, ["enabled"] = now.Enabled, ["priority"] = now.Priority,
            ["before"] = new { enabled = cur.Enabled, priority = cur.Priority, own = cur.Own }, ["redrawRequested"] = true,
        });
    }

    private void NotePermanent(string mod, PermanentSetting was, PermanentSetting now)
        => permanent[mod] = (permanent.TryGetValue(mod, out var old) ? old.Before : was, now);

    // ── live edits ──────────────────────────────────────────────────────────

    private ChangeResult? EditRefusal(string modDir)
    {
        var s = game.EditState(modDir);
        if (!s.FeatureOn) return ChangeResult.Fail(409, "binding-off", "design binding (or Proteus) is switched off");
        if (!s.HasActive) return ChangeResult.Fail(409, "no-active-binding", "no bound design is active");
        if (s.PresetDriven) return ChangeResult.Fail(409, "preset-driven", $"a pinned preset drives {modDir}");
        if (!s.Governed) return ChangeResult.Fail(409, "not-bound", $"the active binding didn't capture {modDir}; its edits would go into the mod");
        return null;
    }

    /// <summary>Set one live colour row's sub-row A diffuse, through the colour editor's path.</summary>
    public ChangeResult EditRow(string modDir, string? group, string? option, int row, string diffuse)
    {
        if (EditRefusal(modDir) is { } refused) return refused;
        if (game.LiveRows(modDir, group, option) is not { Count: > 0 } rows)
            return ChangeResult.Fail(409, "no-override-rows", "that option has no live override rows to edit");
        if (!rows.Any(r => r.Row == row)) return ChangeResult.Fail(404, "row-not-found", $"no live row {row}");

        liveBefore ??= game.LiveSnapshot();
        since ??= DateTime.UtcNow;
        var copy = rows.Select(r => r.Clone()).ToList();
        var target = copy.First(r => r.Row == row);
        (target.SubRowA ??= new ColorTableSubRowPreset()).Diffuse = diffuse;
        if (!game.SetLiveRows(modDir, group, option, copy))
            return ChangeResult.Fail(409, "not-bound", "the binding no longer governs the mod");
        edits++;
        liveAfterActive = game.LiveSnapshot().Active;
        return LiveAnswer(modDir, group, option);
    }

    /// <summary>Clear one option's live override, through Reset's (live-only) path.</summary>
    public ChangeResult ClearOption(string modDir, string? group, string? option)
    {
        if (EditRefusal(modDir) is { } refused) return refused;
        liveBefore ??= game.LiveSnapshot();
        since ??= DateTime.UtcNow;
        game.ClearLiveOption(modDir, group, option);
        cleared++;
        liveAfterActive = game.LiveSnapshot().Active;
        return LiveAnswer(modDir, group, option);
    }

    private ChangeResult LiveAnswer(string modDir, string? group, string? option)
        => ChangeResult.Ok(new Dictionary<string, object?>
        {
            ["mod"] = modDir, ["group"] = group, ["option"] = option, ["governed"] = true,
            ["live"] = RowsJson(game.LiveRows(modDir, group, option)),
        });

    internal static object? RowsJson(IEnumerable<ColorTableRowPreset>? rows)
        => rows?.Select(r => new { row = r.Row, diffuse = r.SubRowA?.Diffuse, emissive = r.SubRowA?.Emissive ?? 0f }).ToList();

    // ── the on-disk record ──────────────────────────────────────────────────

    /// <summary>
    /// Write the session's Before record before its first persistent change, and once more when the first design apply
    /// brings the Glamourer state a permanent-first record lacked. Temp file, then move. Null when written (or not owed).
    /// </summary>
    private ChangeResult? EnsureRecord(JObject? glamourer, DesignBindingService.Carriers carriers, HarnessLiveSnapshot live,
                                       Guid? coll, IReadOnlyDictionary<string, PermanentSetting>? permanentNow)
    {
        if (recordPath != null && (recordHasGlamourer || glamourer == null)) return null;
        if (recordRoot == null) return ChangeResult.Fail(503, "record-failed", "the harness has no backups folder for its record");
        try
        {
            var dir = Path.Combine(recordRoot, session);
            Directory.CreateDirectory(dir);
            // One file per binding session within this harness session: a finished one is never overwritten.
            if (recordPath == null) recordIndex++;
            var path = Path.Combine(dir, recordIndex == 1 ? "binding.json" : $"binding-{recordIndex}.json");
            // A rewrite keeps the first record's live and permanent Before values: they predate this apply.
            var prior = recordPath != null ? JObject.Parse(File.ReadAllText(path)) : null;
            var record = new JObject
            {
                ["session"] = session, ["writtenUtc"] = DateTime.UtcNow, ["build"] = build,
                ["glamourerBefore"] = glamourer?.DeepClone(),
                ["carriersBefore"] = glamourer == null ? null : JObject.FromObject(new
                {
                    slots = carriers.OwnedSlots ?? [], glassesRow = carriers.GlassesRow, accessoryItems = carriers.AccessoryItems ?? [],
                    glassesSlotOwned = carriers.GlassesSlotOwned,
                }),
                ["live"] = prior?["live"] ?? JObject.FromObject(new
                {
                    active = live.Active, suspended = live.Suspended, lastActive = live.LastActive,
                    bag = live.BagJson is { } b ? JToken.Parse(b) : null,
                }),
                ["collection"] = prior?["collection"] ?? (coll is { } c ? c.ToString() : null),
                ["permanentBefore"] = prior?["permanentBefore"] ?? (permanentNow is { } p
                    ? JObject.FromObject(p.ToDictionary(kv => kv.Key, kv => new
                      {
                          own = kv.Value.Own, enabled = kv.Value.Enabled, priority = kv.Value.Priority, options = kv.Value.Options,
                      }))
                    : null),
            };
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, record.ToString(Formatting.Indented));
            File.Move(tmp, path, overwrite: true);
            recordPath = path;
            recordHasGlamourer = glamourer != null;
            log?.Information("[Proteus] test harness: binding session record {0}: active {1}, {2} Proteus mod setting(s), glamourer state {3}",
                             scrub(path), live.Active?.ToString() ?? "none", permanentNow?.Count ?? 0, glamourer != null ? "captured" : "none");
            return null;
        }
        catch (Exception e)
        {
            log?.Warning(e, "[Proteus] test harness: the binding session record could not be written");
            return ChangeResult.Fail(503, "record-failed", "the session's undo record could not be written; nothing was changed");
        }
    }

    // ── put back ────────────────────────────────────────────────────────────

    /// <summary>
    /// Undo the binding session: stop the restore's deferred work, put back the Glamourer state, then the permanent settings
    /// and the live state — those two only once the Glamourer state is back (or was never changed), so the character never
    /// ends up wearing one look with another's binding. Null when there is no session. Only unfinished steps stay recorded.
    /// </summary>
    public BindingPutBack? PutBack(bool skipHalfRecorded)
    {
        if (!HasSession) return null;
        if (skipHalfRecorded)
        {
            log?.Warning("[Proteus] test harness: an apply was still settling; binding session left as recorded in {0}",
                         recordPath is { } p ? scrub(p) : "(no record)");
            return new BindingPutBack("skipped-settling", [], "skipped-settling", recordPath is { } r ? scrub(r) : null, false);
        }

        game.QuiesceBinding();
        bool restoredAny = false;

        string glam;
        try { glam = PutBackGlamourer(); }
        catch (Exception e) { log?.Warning(e, "[Proteus] test harness: putting back the Glamourer state failed"); glam = "failed"; }
        if (glam is "restored" or "changed") { glamourerBefore = null; glamourerAfter = null; }
        if (glam == "restored") restoredAny = true;
        // Only a look that is back (or was never changed) takes its binding and mod settings back. After "changed" they
        // never can: they are abandoned (logged, the record keeps their Before values). After "partial" or "failed"
        // everything stays recorded for the next put-back.
        bool consistent = glam is "restored" or "none";
        bool abandon = glam == "changed";

        var perms = new List<UndoEntry>();
        foreach (var (mod, rec) in permanent.ToList())
        {
            UndoEntry entry;
            try { entry = consistent ? PutBackPermanent(mod, rec.Before, rec.After) : new UndoEntry(mod, "skipped-inconsistent"); }
            catch (Exception e) { log?.Warning(e, "[Proteus] test harness: putting back {0} failed", mod); entry = new UndoEntry(mod, "failed", "internal"); }
            if (entry.Result is "restored" or "changed" || abandon) permanent.Remove(mod);
            if (entry.Result == "restored") restoredAny = true;
            perms.Add(entry);
        }

        string live;
        try
        {
            live = liveBefore == null ? "none"
                 : !consistent ? "skipped-inconsistent"
                 : game.LiveSnapshot().Active != liveAfterActive ? "changed"
                 : Restore(liveBefore);
        }
        catch (Exception e) { log?.Warning(e, "[Proteus] test harness: putting back the live binding state failed"); live = "failed"; }
        if (live is "restored" or "changed" or "none" || abandon) { liveBefore = null; liveAfterActive = null; }
        if (abandon)
            log?.Warning("[Proteus] test harness: the character's look changed since the design apply; its mod settings and "
                       + "binding state were left as they are (Before values in {0})", recordPath is { } rp0 ? scrub(rp0) : "(no record)");
        if (live == "restored") restoredAny = true;

        var record = recordPath is { } rp ? scrub(rp) : null;
        if (!HasSession) EndSession();
        if (restoredAny) game.RequestRedraw();
        log?.Information("[Proteus] test harness: put back binding session: glamourer {0}, {1} permanent setting(s), live {2}",
                         glam, perms.Count, live);
        return new BindingPutBack(glam, perms, live, record, restoredAny);
    }

    private string Restore(HarnessLiveSnapshot s)
    {
        game.RestoreLive(s);
        return "restored";
    }

    private void EndSession()
    {
        applied.Clear();
        edits = cleared = 0;
        recordPath = null;
        recordHasGlamourer = false;
        collection = null;
        since = null;
    }

    private string PutBackGlamourer()
    {
        if (glamourerBefore is not { } before) return "none";
        if (game.GlamourerState() is not { } now) return "failed";
        var carriersNow = game.CarriersNow();
        var after = glamourerAfter ?? before;
        if (ChangedOutsideCarriers(after, now, carriersBefore, carriersNow)) return "changed";

        var skip = (carriersBefore.OwnedSlots ?? []).Concat(carriersNow.OwnedSlots ?? [])
                   .Concat(AccessorySlots(before, carriersBefore, carriersNow)).Concat(AccessorySlots(now, carriersBefore, carriersNow))
                   .Distinct(StringComparer.Ordinal).ToList();
        bool skipGlasses = carriersBefore.GlassesSlotOwned || carriersNow.GlassesSlotOwned;
        if (game.RestoreGlamourerState(before, skip, skipGlasses) != null) return "failed";

        if (game.GlamourerState() is not { } back) return "partial";
        bool materials = JToken.DeepEquals(before["Materials"], back["Materials"]);
        if (materials && !ChangedOutsideCarriers(before, back, carriersBefore, game.CarriersNow())) return "restored";
        glamourerAfter = back;   // a retry compares against what the harness itself last wrote
        return "partial";
    }

    /// <summary>Whether any compared field differs between two states, Proteus's carriers (at either moment) aside.</summary>
    internal static bool ChangedOutsideCarriers(JObject a, JObject b, DesignBindingService.Carriers c1, DesignBindingService.Carriers c2)
    {
        var keys = DesignBindingService.StateFieldKeys(a);
        keys.UnionWith(DesignBindingService.StateFieldKeys(b));
        keys.RemoveWhere(k => DesignBindingService.IsCarrierKey(k, a, b, c1) || DesignBindingService.IsCarrierKey(k, a, b, c2));
        return keys.Any(k => DesignBindingService.FieldChanged(a, b, k));
    }

    /// <summary>The gear slots a state fills with one of Proteus's invisible accessory carriers.</summary>
    private static IEnumerable<string> AccessorySlots(JObject state, params DesignBindingService.Carriers[] carriers)
    {
        if (state["Equipment"] is not JObject equip) yield break;
        foreach (var p in equip.Properties())
            if (p.Value["ItemId"] is { Type: JTokenType.Integer } id
                && carriers.Any(c => c.AccessoryItems?.Contains(id.ToObject<ulong>()) == true))
                yield return p.Name;
    }

    private UndoEntry PutBackPermanent(string mod, PermanentSetting before, PermanentSetting after)
    {
        if (collection is not { } coll) return new UndoEntry(mod, "failed", "no-collection");
        if (game.PermanentSettings(coll) is not { } all) return new UndoEntry(mod, "failed", "penumbra-unavailable");
        var now = all.GetValueOrDefault(mod) ?? PermanentSetting.Inherits;
        if (!now.Equals(after)) return new UndoEntry(mod, "changed");
        return game.WritePermanent(coll, mod, before) is { } ec ? new UndoEntry(mod, "failed", ec) : new UndoEntry(mod, "restored");
    }

    // ── views ───────────────────────────────────────────────────────────────

    /// <summary><c>/state.harnessChanges.binding</c>; null with no session.</summary>
    public object? View() => !HasSession ? null : new
    {
        since, record = recordPath is { } p ? scrub(p) : null, applied, edits, cleared,
        permanent = permanent.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList(),
        glamourer = glamourerBefore != null, live = liveBefore != null,
    };

    /// <summary>What is still recorded, for the warning when put-back cannot run.</summary>
    public IEnumerable<string> Leftovers()
    {
        if (glamourerBefore != null) yield return "glamourer state";
        if (liveBefore != null) yield return "live design binding";
        foreach (var m in permanent.Keys) yield return $"{m} (permanent)";
        if (HasSession && recordPath != null) yield return $"Before values in {scrub(recordPath)}";
    }
}
