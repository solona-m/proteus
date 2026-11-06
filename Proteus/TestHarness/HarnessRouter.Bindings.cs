using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Proteus.TestHarness;

/// <summary>
/// The design-binding routes: read binding state, apply a Glamourer design as a user does, and edit or clear a live colour
/// override. Each change is recorded for put-back (<see cref="HarnessBindings"/>); none writes the binding store or a mod's
/// files. Bodies are checked off the framework thread.
/// </summary>
internal sealed partial class HarnessRouter
{
    private static readonly Regex HexColour = new("^#[0-9A-Fa-f]{6}$", RegexOptions.CultureInvariant);

    /// <summary>The hops a design apply waits at least, so the restore's next-tick unequip has run before it reads After.</summary>
    private const int ApplyMinHops = 3;

    private async Task<HarnessResponse> BindingsView(HarnessRequest request)
    {
        string? mod = request.Query.TryGetValue("mod", out var m) && m.Length > 0 ? m : null;
        string? group = request.Query.TryGetValue("group", out var g) && g.Length > 0 ? g : null;
        string? option = request.Query.TryGetValue("option", out var o) && o.Length > 0 ? o : null;
        bool withNames = false;
        if (request.Query.TryGetValue("names", out var n))
        {
            if (n != "1") return HarnessResponse.Error(400, "invalid-field", "names must be 1");
            withNames = true;
        }
        if ((group == null) != (option == null)) return HarnessResponse.Error(400, "invalid-field", "group and option go together");
        if ((group != null) && mod == null) return HarnessResponse.Error(400, "invalid-field", "group and option need mod");
        if (mod != null && !ModBackup.SafeModDir(mod)) return HarnessResponse.Error(400, "invalid-field", "the mod folder name is not a plain folder name");
        Guid? design = null;
        if (request.Query.TryGetValue("design", out var d))
        {
            if (!Guid.TryParse(d, out var id)) return HarnessResponse.Error(400, "invalid-field", "design must be a GUID");
            design = id;
        }

        var result = await OnFramework(() =>
        {
            if (Game.Designs() is not { } designs) return ChangeResult.Fail(503, "glamourer-unavailable", "Glamourer's designs cannot be read");
            string? dir = null;
            if (mod != null)
            {
                var (found, _, error) = FindMod(mod);
                if (error != null) return error;
                dir = found;
            }
            if (design is { } want && !designs.ContainsKey(want)) return ChangeResult.Fail(404, "design-not-found", $"no Glamourer design {want}");

            var v = Game.BindingView(dir, group, option);
            string? Name(string? s) => withNames && s != null ? changes.Scrub(s) : null;
            object? modBlock = null;
            if (dir != null)
            {
                var st = Game.EditState(dir);
                EffectiveSettingsBlock(dir, out var settings);
                modBlock = new
                {
                    mod = dir, governed = st.Governed, presetDriven = st.PresetDriven, group, option,
                    live = HarnessBindings.RowsJson(Game.LiveRows(dir, group, option)),
                    stored = HarnessBindings.RowsJson(v.StoredRows), settings,
                };
            }
            return ChangeResult.Ok(new
            {
                enabled = v.Enabled, followsAutomation = v.FollowsAutomation, restoresCharacterMods = v.RestoresCharacterMods,
                unequipUnset = v.UnequipUnset, echoWindowOpen = v.EchoMsLeft > 0, echoMsLeft = v.EchoMsLeft,
                active = v.Active is { } a ? new { id = a, fields = v.ActiveFields, name = Name(v.ActiveName) } : null,
                storeSha256 = v.StoreSha256,
                bindings = v.Bindings.Select(b => new { id = b.Id, capturedUtc = b.CapturedUtc, mods = b.Mods, sha256 = b.Sha256, name = Name(b.Name) }),
                designs = designs.OrderBy(kv => kv.Key).Select(kv => new { id = kv.Key, bound = Game.IsBound(kv.Key), name = Name(kv.Value) }),
                mod = modBlock,
                design = design is { } dg ? new { id = dg, bound = Game.IsBound(dg), fields = Game.DesignFields(dg), name = Name(designs[dg]) } : null,
            });
        });
        return Answer(result);
    }

    /// <summary>A mod's effective Penumbra settings (temporary included) for <c>/bindings?mod=</c>; null when unreadable.</summary>
    private void EffectiveSettingsBlock(string dir, out object? settings)
    {
        settings = null;
        if (Game.PlayerCollection() is not { } c) return;
        var read = Game.ReadSettings(c, dir);
        if (read.Kind == Interop.PenumbraBridge.SettingsReadKind.Failed) return;
        settings = read.Settings is { } s
            ? new { enabled = s.Enabled, priority = s.Priority, options = s.Options, temporary = s.Temporary }
            : new { enabled = false, priority = 0, options = new Dictionary<string, List<string>>(), temporary = false };
    }

    private async Task<HarnessResponse> GlamourerApply(HarnessRequest request)
    {
        var (body, bad) = JsonBody(request, "design", "allowAutoWriters");
        if (bad != null) return bad;
        string? raw = OptionalString(body, "design", out bad);
        if (bad != null) return bad;
        if (raw == null || !Guid.TryParse(raw, out var design)) return HarnessResponse.Error(400, "invalid-field", "design must be a GUID");
        bool allow = false;
        if (body.TryGetProperty("allowAutoWriters", out var aw))
        {
            if (aw.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return HarnessResponse.Error(400, "invalid-field", "allowAutoWriters must be true or false");
            allow = aw.GetBoolean();
        }

        PendingApply? pending = null;
        var begun = await OnFramework<ChangeResult?>(() =>
        {
            if (changes.Gate(jobs.Busy, Game.AutoRefit().Busy) is { } refused) return refused;
            var (error, p) = changes.Bindings.BeginApply(design, allow, changes.EquipsRecorded);
            if (error != null) return error;
            pending = p;
            changes.ApplySettling = true;
            return null;
        });
        if (begun != null) return Answer(begun);

        try
        {
            // The restore an apply sets off defers work (a next-tick unequip, the composite, the auto writers): After is
            // read only once that has gone quiet, so put-back compares against what the apply really left.
            var clock = Stopwatch.StartNew();
            bool settled = false;
            for (int hop = 1; ; hop++)
            {
                bool quiet = await OnFramework(changes.Bindings.Quiet);
                if (quiet && hop >= ApplyMinHops) { settled = true; break; }
                if (clock.Elapsed >= jobs.Timings.RedrawWait) break;
                await Task.Delay(100);
            }
            var result = await OnFramework(() => changes.Bindings.FinishApply(pending!, settled));
            return Answer(result);
        }
        finally
        {
            changes.ApplySettling = false;
        }
    }

    private async Task<HarnessResponse> BindingEdit(HarnessRequest request)
    {
        var (body, bad) = JsonBody(request, "mod", "group", "option", "row", "diffuse", "clear");
        if (bad != null) return bad;
        string? mod = OptionalString(body, "mod", out bad);
        if (bad != null) return bad;
        string? group = OptionalString(body, "group", out bad);
        if (bad != null) return bad;
        string? option = OptionalString(body, "option", out bad);
        if (bad != null) return bad;
        string? diffuse = OptionalString(body, "diffuse", out bad);
        if (bad != null) return bad;
        if (mod == null) return HarnessResponse.Error(400, "invalid-field", "mod is required");
        if (!ModBackup.SafeModDir(mod)) return HarnessResponse.Error(400, "invalid-field", "the mod folder name is not a plain folder name");
        if ((group == null) != (option == null)) return HarnessResponse.Error(400, "invalid-field", "group and option go together");

        bool clear = false;
        if (body.TryGetProperty("clear", out var c))
        {
            if (c.ValueKind != JsonValueKind.True) return HarnessResponse.Error(400, "invalid-field", "clear must be true when given");
            clear = true;
        }
        bool hasRow = body.TryGetProperty("row", out var rowEl);
        if (clear == (hasRow || diffuse != null))
            return HarnessResponse.Error(400, "invalid-field", "give row and diffuse, or clear: true, not both");
        int row = 0;
        if (!clear)
        {
            if (!hasRow || diffuse == null) return HarnessResponse.Error(400, "invalid-field", "row and diffuse go together");
            if (!WholeNumber(rowEl, 0, 1023, out var r)) return HarnessResponse.Error(400, "invalid-field", "row must be a whole number from 0 to 1023");
            row = (int)r;
            if (!HexColour.IsMatch(diffuse)) return HarnessResponse.Error(400, "invalid-field", "diffuse must be #RRGGBB");
        }

        var result = await OnFramework(() =>
        {
            if (changes.Gate(jobs.Busy, Game.AutoRefit().Busy) is { } refused) return refused;
            var (dir, _, error) = FindMod(mod);
            if (error != null) return error;
            return clear ? changes.Bindings.ClearOption(dir!, group, option)
                         : changes.Bindings.EditRow(dir!, group, option, row, diffuse!.ToUpperInvariant());
        });
        return Answer(result);
    }
}
