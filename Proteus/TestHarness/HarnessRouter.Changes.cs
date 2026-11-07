using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Proteus.Services;

namespace Proteus.TestHarness;

/// <summary>
/// The routes that change the character for the session: equip a gear slot, hold a mod's settings, and put it all back.
/// Bodies are checked off the framework thread; the gate and the change run in one framework hop.
/// </summary>
internal sealed partial class HarnessRouter
{
    private async Task<HarnessResponse> Equip(HarnessRequest request)
    {
        var (body, bad) = JsonBody(request, "slot", "item", "set", "variant");
        if (bad != null) return bad;
        string? rawSlot = OptionalString(body, "slot", out bad);
        if (bad != null) return bad;
        if (HarnessGear.Canonical(rawSlot) is not { } slot)
            return HarnessResponse.Error(400, "invalid-field", $"slot must be one of {string.Join(", ", HarnessGear.Slots)}");

        bool hasItem = body.TryGetProperty("item", out var itemEl);
        bool hasSet = body.TryGetProperty("set", out var setEl);
        bool hasVariant = body.TryGetProperty("variant", out var variantEl);
        if (hasItem == (hasSet || hasVariant))
            return HarnessResponse.Error(400, "invalid-field", "give item, or set and variant, not both");

        ulong sentId;
        (int Set, int Variant)? requested = null;
        if (hasItem)
        {
            if (!WholeNumber(itemEl, 1, HarnessGear.FirstGlamourerPseudoId - 1, out var item))
                return HarnessResponse.Error(400, "invalid-field",
                                             $"item must be a whole number from 1 to {HarnessGear.FirstGlamourerPseudoId - 1} (custom ids go through set and variant)");
            sentId = item;
        }
        else
        {
            if (!hasSet || !hasVariant) return HarnessResponse.Error(400, "invalid-field", "set and variant go together");
            if (!WholeNumber(setEl, 1, 65535, out var set)) return HarnessResponse.Error(400, "invalid-field", "set must be a whole number from 1 to 65535");
            if (!WholeNumber(variantEl, 0, 255, out var variant)) return HarnessResponse.Error(400, "invalid-field", "variant must be a whole number from 0 to 255");
            requested = ((int)set, (int)variant);
            sentId = HarnessGear.CustomId(slot, (int)set, (int)variant);
        }

        var result = await OnFramework(() => changes.Gate(jobs.Busy, Game.AutoRefit().Busy) ?? changes.Equip(slot, sentId, requested));
        return Answer(result);
    }

    private async Task<HarnessResponse> ModSettings(HarnessRequest request)
    {
        var (body, bad) = JsonBody(request, "mod", "enabled", "priority", "permanent", "collection");
        if (bad != null) return bad;
        string? mod = OptionalString(body, "mod", out bad);
        if (bad != null) return bad;
        if (mod == null) return HarnessResponse.Error(400, "invalid-field", "mod is required");
        bool? enabled = null;
        int? priority = null;
        if (body.TryGetProperty("enabled", out var e))
        {
            if (e.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return HarnessResponse.Error(400, "invalid-field", "enabled must be true or false");
            enabled = e.GetBoolean();
        }
        if (body.TryGetProperty("priority", out var p))
        {
            if (p.ValueKind != JsonValueKind.Number || !p.TryGetInt32(out var pr))
                return HarnessResponse.Error(400, "invalid-field", "priority must be a whole number that fits in 32 bits");
            priority = pr;
        }
        bool permanent = false;
        if (body.TryGetProperty("permanent", out var pm))
        {
            if (pm.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return HarnessResponse.Error(400, "invalid-field", "permanent must be true or false");
            permanent = pm.GetBoolean();
        }
        if (enabled == null && priority == null) return HarnessResponse.Error(400, "invalid-field", "give enabled, priority or both");
        if (!ModBackup.SafeModDir(mod)) return HarnessResponse.Error(400, "invalid-field", "the mod folder name is not a plain folder name");
        // Another collection (a sync plugin's, say): a temporary hold there too, never permanent.
        string? rawCollection = OptionalString(body, "collection", out bad);
        if (bad != null) return bad;
        Guid? asked = null;
        if (rawCollection != null)
        {
            if (!Guid.TryParse(rawCollection, out var g)) return HarnessResponse.Error(400, "invalid-field", "collection must be a collection id (a guid)");
            if (permanent) return HarnessResponse.Error(400, "invalid-field", "permanent writes are for the player's collection only");
            asked = g;
        }

        var result = await OnFramework(() =>
        {
            if (changes.Gate(jobs.Busy, Game.AutoRefit().Busy) is { } refused) return refused;
            var (dir, _, error) = FindMod(mod);
            if (error != null) return error;
            if (asked is { } other && other != Game.PlayerCollection())
            {
                if (Game.Collections() is not { } listed)
                    return ChangeResult.Fail(503, "penumbra-unavailable", "Penumbra could not list its collections");
                if (!listed.Any(c => c.Id == other))
                    return ChangeResult.Fail(404, "collection-not-found", $"Penumbra has no collection {other}");
                return changes.Hold(other, dir!, enabled, priority);
            }
            if (Game.PlayerCollection() is not { } collection)
                return ChangeResult.Fail(409, "no-collection", "the character has no Penumbra collection");
            // Permanent: a Proteus mod's own setting, recorded (on disk first) and written back by put-back.
            return permanent ? changes.Bindings.WritePermanent(collection, dir!, enabled, priority)
                             : changes.Hold(collection, dir!, enabled, priority);
        });
        return Answer(result);
    }

    private async Task<HarnessResponse> PutBack(HarnessRequest request)
    {
        var (_, bad) = JsonBody(request);
        if (bad != null) return bad;
        var report = await OnFramework(() => changes.ApplySettling ? null : changes.PutBack());
        if (report == null) return HarnessResponse.Error(409, "busy", "a design apply is still settling; wait for its answer");
        var b = report.Binding;
        return Scrubbed(200, new
        {
            gear = report.Gear.Select(g => new { slot = g.Name, result = g.Result, code = g.Code }),
            holds = report.Holds.Select(h => new { mod = h.Name, result = h.Result, code = h.Code }),
            sweep = report.Sweep, redrawRequested = report.RedrawRequested,
            studio = StudioPutBackBlock(report),
            binding = b == null ? null : new
            {
                glamourer = b.Glamourer, live = b.Live, record = b.Record,
                permanent = b.Permanent.Select(e => new { mod = e.Name, result = e.Result, code = e.Code }),
            },
            matcap = new
            {
                settings = (b?.Permanent ?? []).Where(e => report.MatcapMods.Contains(e.Name, StringComparer.OrdinalIgnoreCase))
                                               .Select(e => new { mod = e.Name, result = e.Result, code = e.Code }),
                images = report.MatcapImages.Select(e => new { name = e.Name, result = e.Result, code = e.Code }),
            },
            sidecar = report.Sidecar.Select(e => new { mod = e.Mod, result = e.Result, code = e.Code, defaults = e.Defaults }),
            autoRefit = new { result = report.AutoRefit.Result, refitsWhileOverridden = report.AutoRefit.RefitsWhileOverridden },
        });
    }

    /// <summary>
    /// The Penumbra folder a request names, matched case-insensitively: Penumbra there, the folder Penumbra's, not
    /// Proteus's own output mod, a plain name. Shared by refit, restore and hold. Framework thread.
    /// </summary>
    private (string? Dir, ModList? Mods, ChangeResult? Error) FindMod(string mod)
    {
        if (Game.Mods() is not { } mods) return (null, null, ChangeResult.Fail(503, "penumbra-unavailable", "Penumbra is not available"));
        var dir = mods.Mods.Keys.FirstOrDefault(k => string.Equals(k, mod, StringComparison.OrdinalIgnoreCase));
        if (dir == null)
            return (null, null, ChangeResult.Fail(404, "mod-not-found", $"no Penumbra mod folder '{mod}' (folder names only, not display names)"));
        if (string.Equals(dir, Game.ManagedModDir, StringComparison.OrdinalIgnoreCase))
            return (null, null, ChangeResult.Fail(400, "managed-mod", "Proteus's own output mod is never refitted, hair-ported, restored or held"));
        if (!ModBackup.SafeModDir(dir))
            return (null, null, ChangeResult.Fail(400, "invalid-field", "the mod folder name is not a plain folder name"));
        return (dir, mods, null);
    }

    /// <summary>A change's answer, scrubbed: mod folders and other plugins' sources can carry names.</summary>
    private static HarnessResponse Answer(ChangeResult r)
        => r.Status == 200 ? Scrubbed(200, r.Body)
         : HarnessResponse.Error(r.Status, r.Code!, LogAnonymizer.Scrub(r.Message ?? ""));

    /// <summary>A JSON integer (no fraction, no string) within [min, max].</summary>
    private static bool WholeNumber(JsonElement v, ulong min, ulong max, out ulong value)
    {
        value = 0;
        if (v.ValueKind != JsonValueKind.Number || !v.TryGetUInt64(out var n)) return false;
        if (n < min || n > max) return false;
        value = n;
        return true;
    }
}
