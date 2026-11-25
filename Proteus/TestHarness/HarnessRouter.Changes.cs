using System;
using System.Collections.Generic;
using System.IO;
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
        var (body, bad) = JsonBody(request, "slot", "item", "set", "variant", "dyes");
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

        // Optional stains (Glamourer stain ids, channel 1 and 2): what a dyed check needs; put-back restores the original's.
        byte dye1 = 0, dye2 = 0;
        if (body.TryGetProperty("dyes", out var dyesEl))
        {
            if (dyesEl.ValueKind != JsonValueKind.Array || dyesEl.GetArrayLength() != 2
                || !WholeNumber(dyesEl[0], 0, 255, out var d1) || !WholeNumber(dyesEl[1], 0, 255, out var d2))
                return HarnessResponse.Error(400, "invalid-field", "dyes must be two whole numbers from 0 to 255");
            dye1 = (byte)d1;
            dye2 = (byte)d2;
        }

        var result = await OnFramework(() => changes.Gate(jobs.Busy, Game.AutoRefit().Busy) ?? changes.Equip(slot, sentId, requested, dye1, dye2));
        return Answer(result);
    }

    private async Task<HarnessResponse> ModSettings(HarnessRequest request)
    {
        var (body, bad) = JsonBody(request, "mod", "enabled", "priority", "permanent", "collection", "options");
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
        // Options for the hold: a temporary selection, never saved, never with a permanent write.
        List<(string Group, IReadOnlyList<string> Options)>? askedOptions = null;
        if (body.TryGetProperty("options", out var optionsEl))
        {
            if (ParseOptions(optionsEl, out var parsed) is { } why) return HarnessResponse.Error(400, "invalid-field", why);
            if (permanent) return HarnessResponse.Error(400, "invalid-field", "options are for a temporary hold only, never with permanent");
            askedOptions = parsed;
        }
        if (enabled == null && priority == null && askedOptions == null)
            return HarnessResponse.Error(400, "invalid-field", "give enabled, priority, options or a combination");
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

        // With options: the mod found on the framework thread, its groups read and the selection resolved off it (a read
        // of its meta.json), then the hold in the usual hop, the mod found again there.
        Dictionary<string, List<string>>? selection = null;
        if (askedOptions != null)
        {
            var (root, findError) = await OnFramework(() =>
            {
                var (d, m, e) = FindMod(mod);
                return (e == null ? Path.Combine(m!.ModsRoot, d!) : null, e);
            });
            if (findError != null) return Answer(findError);
            var resolved = await Task.Run(() => HarnessOptionLookup.ResolveSelection(HarnessOptionLookup.ReadModGroups(root!), askedOptions));
            if (!resolved.Ok) return HarnessResponse.Error(resolved.Status, resolved.Code!, changes.Scrub(resolved.Message ?? ""));
            selection = resolved.Selection;
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
                return changes.Hold(other, dir!, enabled, priority, selection);
            }
            if (Game.PlayerCollection() is not { } collection)
                return ChangeResult.Fail(409, "no-collection", "the character has no Penumbra collection");
            // Permanent: a Proteus mod's own setting, recorded (on disk first) and written back by put-back.
            return permanent ? changes.Bindings.WritePermanent(collection, dir!, enabled, priority)
                             : changes.Hold(collection, dir!, enabled, priority, selection);
        });
        return Answer(result);
    }

    /// <summary>
    /// A hold's <c>options</c>: a non-empty object, each key a group name, each value an array of non-empty option names.
    /// Null when well formed; else why not.
    /// </summary>
    private static string? ParseOptions(JsonElement el, out List<(string Group, IReadOnlyList<string> Options)> parsed)
    {
        parsed = [];
        if (el.ValueKind != JsonValueKind.Object) return "options must be an object of group name to an array of option names";
        foreach (var prop in el.EnumerateObject())
        {
            if (prop.Name.Length == 0) return "options: a group name is empty";
            if (prop.Value.ValueKind != JsonValueKind.Array) return $"options: group {prop.Name} must name an array of options";
            var names = new List<string>();
            foreach (var o in prop.Value.EnumerateArray())
            {
                if (o.ValueKind != JsonValueKind.String || o.GetString() is not { Length: > 0 } name)
                    return $"options: group {prop.Name} must list option names as non-empty strings";
                names.Add(name);
            }
            parsed.Add((prop.Name, names));
        }
        return parsed.Count == 0 ? "options must name at least one group" : null;
    }

    private async Task<HarnessResponse> PutBack(HarnessRequest request)
    {
        var (_, bad) = JsonBody(request);
        if (bad != null) return bad;
        string? busy = null;
        var report = await OnFramework(() =>
        {
            if (changes.ApplySettling) { busy = "a design apply is still settling; wait for its answer"; return null; }
            if (changes.PoseSettling) { busy = "a pose change is still settling; wait for its answer"; return null; }
            var r = changes.PutBack();
            // Set in this same hop, so nothing lands between the stand-up and its confirming read.
            if (r.Pose.Any) changes.PoseSettling = true;
            return r;
        });
        if (report == null) return HarnessResponse.Error(409, "busy", busy ?? "busy");

        // The pose put-back was only sent: one wait off the framework thread, then one hop confirms both.
        (string Emote, string Camera) pose = (report.Pose.Emote, report.Pose.Camera);
        string? confirm = null;
        if (report.Pose.Any)
        {
            try
            {
                await Task.Delay(jobs.Timings.PutBackConfirm);
                if (changes.Stopping) confirm = "harness-stopping";
                else pose = await OnFramework(() => changes.ConfirmPose(report.Pose));
            }
            finally { changes.PoseSettling = false; }
        }
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
            autoRefit = new
            {
                result = report.AutoRefit.Result, refitsWhileOverridden = report.AutoRefit.RefitsWhileOverridden,
                preference = report.AutoRefitPreference,
                jobBackedMods = report.JobBackedMods.Select(e => new { mod = e.Name, job = e.Code }),
            },
            output = report.Output is { } o
                ? new
                {
                    locks = o.Locks.Select(l => new { file = l.Name, result = l.Result }),
                    moveFault = new { result = o.MoveFault }, fault = new { result = o.Fault },
                    autoRedraw = new { result = o.AutoRedraw },
                }
                : null,
            pose = new { emote = pose.Emote, camera = pose.Camera, confirm },
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
