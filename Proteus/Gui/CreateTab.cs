using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Utility.Raii;
using Proteus.Localization;
using Proteus.Services;

namespace Proteus.Gui;

internal sealed class CreateTab
{
    private readonly ModCreationService modCreation;
    private readonly FileDialogManager fileDialog;

    public CreateTab(ModCreationService modCreation, FileDialogManager fileDialog)
    {
        this.modCreation = modCreation;
        this.fileDialog = fileDialog;
    }

    private string _createName = "";
    private string _createAuthor = "";
    private string _createMaterial = "";
    private string _createDiffuse = "";   // "" = no file picked
    private string _createMask = "";
    private string _createNormal = "";
    private string _createIndex = "";
    private bool _createWholeSkin;        // the textures ARE the skin → the normal replaces, never compounds
    private bool _createWholeSkinLocked;  // user ticked it by hand — stop auto-detecting over their answer
    private string _createWholeSkinProbedFor = "";   // the diffuse+normal pair the last probe was for
    private Task<bool>? _createWholeSkinProbe;       // decodes two images, so never on the frame thread
    private bool _createMaterialLocked;   // stop auto-detecting once we have a real body (or the user edits)
    private string _createMaterialAuto = "";  // the value we last auto-filled, to tell a user edit apart
    private long _createDetectNextTick;   // throttle the detect poll while the character isn't drawn yet
    private string? _createStatus;        // last create result message
    private bool _createStatusOk;
    // The face texture is a doubled sheet (both sides of the head); auto-detected from the art's shape, locked once the user touches it.
    private bool _createFaceSplit;
    private bool _createFaceSplitLocked;      // an explicit tick — the detect never writes over it again
    private string _createFaceSplitProbedFor = "";   // (material, first art) the current verdict belongs to
    // Whether the art glows and how; never probed, since nothing in the image says the author wanted it lit.
    private GlowStyle _createGlow = GlowStyle.None;

    // ── Material-target picker ──
    // Built once when the picker opens: BeginCombo is true every frame it stays open. Null = not built.
    private List<(string Path, string Label, bool Skin)>? _matPickerItems;
    private bool _matPickerWasOpen;   // last frame's open state — gives the rising edge for the rebuild
    private bool _matPickerStale;     // list came from the cached snapshot, not a live query

    // ── Texture slots the picked material declares ──
    // null = not read or unreadable ⇒ fail open and offer every row, so hand-typed paths still work.
    private MtrlTexturePaths? _createSlots;
    private string _createSlotsFor = "";    // the material _createSlots was resolved for
    // Throttle, not a debounce: caps .mtrl re-reads while typing; programmatic changes bypass it.
    private long _createSlotsNextTick;

    /// <summary>Author a basic skin-overlay mod: name + author + up to three textures → a new Penumbra mod.</summary>
    internal void DrawCreateTab()
    {
        DrawMaterialPicker();
        DrawTextureRows();
        DrawSkinOptions();
        DrawGlowOptions();
        DrawCreateButton();
    }

    private void DrawMaterialPicker()
    {
        var cs = Strings.Create;

        ImGui.TextWrapped(cs.Intro);
        ImGui.Separator();

        ImGui.InputText(cs.ModName, ref _createName, 128);
        ImGui.InputText(cs.Author, ref _createAuthor, 128);

        // Auto-fill from the body the character is drawing; keep polling until a real body resolves or the user edits the box.
        if (!_createMaterialLocked)
        {
            if (_createMaterial != _createMaterialAuto)
            {
                _createMaterialLocked = true;   // user typed something — hands off
            }
            else if (Environment.TickCount64 >= _createDetectNextTick)
            {
                _createDetectNextTick = Environment.TickCount64 + 500;
                var detected = modCreation.DetectBodyMaterial();
                if (detected != null)
                {
                    _createMaterial = _createMaterialAuto = detected;
                    _createMaterialLocked = true;
                }
                else if (_createMaterial.Length == 0)
                {
                    // Placeholder only, not locked, so the live detect wins once the character draws.
                    _createMaterial = _createMaterialAuto =
                        modCreation.CachedBodyMaterial() ?? ModCreationService.DefaultBodyMaterial;
                }
            }
        }
        ImGui.SetNextItemWidth(560);
        ImGui.InputText(cs.MaterialTarget, ref _createMaterial, 256);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(cs.MaterialTargetTip);

        // NoPreview renders just the arrow, as a companion to the text box: ImGui has no editable combo.
        ImGui.SameLine();
        // NoPreview needs a width constraint or the popup clips every label, and passing one disables BeginCombo's own height cap, so cap it here.
        var pickerMaxH = ImGui.GetTextLineHeightWithSpacing() * 20 + ImGui.GetStyle().WindowPadding.Y * 2;
        // Wide max: the notices inside are full sentences and the popup clips rather than wraps.
        ImGui.SetNextWindowSizeConstraints(new Vector2(460, 0), new Vector2(950, pickerMaxH));
        bool pickerOpen = ImGui.BeginCombo("##matpick", "", ImGuiComboFlags.NoPreview);
        // Rising edge only; assign after the test so the flag also falls on close.
        if (pickerOpen && !_matPickerWasOpen) RebuildMaterialPicker();
        _matPickerWasOpen = pickerOpen;
        if (pickerOpen)
        {
            var items = _matPickerItems;
            // Only with a list to show: the query also reports stale when the cache is empty.
            if (_matPickerStale && items is { Count: > 0 })
                ImGui.TextDisabled(cs.PickerStale);

            if (items == null || items.Count == 0)
            {
                ImGui.TextDisabled(cs.PickerEmpty);
            }
            else
            {
                if (items[0].Skin) ImGui.TextDisabled(cs.PickerSkin);
                // Starts true when nothing is skin, so a separator never leads the list.
                bool separated = !items[0].Skin;
                foreach (var it in items)
                {
                    if (!it.Skin && !separated) { ImGui.Separator(); separated = true; }
                    // ##path: two races can produce identical labels, and duplicate ids would misroute the click.
                    if (ImGui.Selectable($"{it.Label}##{it.Path}",
                            string.Equals(it.Path, _createMaterial, StringComparison.OrdinalIgnoreCase)))
                    {
                        // Set both, then lock, so the poll can't overwrite the choice.
                        _createMaterial = _createMaterialAuto = it.Path;
                        _createMaterialLocked = true;
                    }
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip(it.Path);
                }
            }
            ImGui.EndCombo();
        }

        ImGui.SameLine();
        if (ImGui.SmallButton(cs.Redetect))
        {
            _createMaterial = _createMaterialAuto = modCreation.DetectBodyMaterial()
                ?? modCreation.CachedBodyMaterial() ?? ModCreationService.DefaultBodyMaterial;
            _createMaterialLocked = true;   // explicit request — take this value and stop polling
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(cs.RedetectTip);
    }

    private void DrawTextureRows()
    {
        var cs = Strings.Create;

        // Which rows the picked material can actually consume.
        if (_createMaterial != _createSlotsFor)
        {
            // Programmatic changes resolve at once; typing is throttled, since each resolve is a Penumbra lookup plus a file read.
            if (_createMaterial == _createMaterialAuto) ResolveCreateSlots();
            else if (_createSlotsNextTick == 0) _createSlotsNextTick = Environment.TickCount64 + 250;
            else if (Environment.TickCount64 >= _createSlotsNextTick) ResolveCreateSlots();
        }
        else _createSlotsNextTick = 0;

        ImGui.Spacing();
        // One label column for all four rows, widened for the widest translated label; measured per frame as font, scale and language can change.
        var colonW = ImGui.CalcTextSize(":").X;
        var labelColumn = MathF.Max(ProteusStyle.S(90f), ProteusStyle.S(12f) + colonW + MathF.Max(
            MathF.Max(ImGui.CalcTextSize(cs.SlotDiffuse).X, ImGui.CalcTextSize(cs.SlotMask).X),
            MathF.Max(ImGui.CalcTextSize(cs.SlotNormal).X, ImGui.CalcTextSize(cs.SlotIndex).X)));

        DrawTextureRow("Diffuse", cs.SlotDiffuse, labelColumn, ref _createDiffuse, SlotEnabled("Diffuse"), cs.NoDiffuse);
        DrawTextureRow("Mask",    cs.SlotMask,    labelColumn, ref _createMask,    SlotEnabled("Mask"),    cs.NoMask);
        DrawTextureRow("Normal",  cs.SlotNormal,  labelColumn, ref _createNormal,  SlotEnabled("Normal"),  cs.NoNormal);
        DrawTextureRow("Index",   cs.SlotIndex,   labelColumn, ref _createIndex,   SlotEnabled("Index"),   cs.NoIndex);
        // Only once a read was attempted for this exact material.
        if (_createSlots == null && _createSlotsFor == _createMaterial && _createMaterial.Length > 0)
            ImGui.TextDisabled(cs.SlotsUnreadable);
    }

    private void DrawSkinOptions()
    {
        var cs = Strings.Create;

        // Auto-detect whole-skin from the art, once per pick and off the frame thread, until the user answers. Skin targets only.
        // A face target is always a whole skin, and the probe can't judge it: a doubled sheet can't resemble the face's own diffuse.
        bool faceTarget = StatusWindow.IsFaceMaterial(_createMaterial);
        var wholeSkinKey = faceTarget
            ? "face " + _createMaterial
            : SlotEnabled("Diffuse") && SlotEnabled("Normal")
                        && _createDiffuse.Length > 0 && _createNormal.Length > 0
                        && StatusWindow.IsSkinMaterial(_createMaterial)
            ? _createMaterial + " " + _createDiffuse + " " + _createNormal
            : "";
        if (!_createWholeSkinLocked && wholeSkinKey != _createWholeSkinProbedFor)
        {
            _createWholeSkinProbedFor = wholeSkinKey;
            _createWholeSkinProbe = null;
            if (faceTarget)
            {
                _createWholeSkin = true;
            }
            else if (wholeSkinKey.Length == 0)
            {
                // Nothing to judge: back to the default rather than keep the last pick's verdict.
                _createWholeSkin = false;
            }
            else
            {
                // Captured: the user can browse again while this runs.
                string m = _createMaterial, d = _createDiffuse, n = _createNormal;
                _createWholeSkinProbe = Task.Run(() => modCreation.LooksLikeWholeSkin(m, d, n));
            }
        }
        if (_createWholeSkinProbe is { IsCompleted: true } wholeSkinProbe)
        {
            _createWholeSkinProbe = null;
            // A faulted probe reads as "not a whole skin", the safe answer.
            if (!_createWholeSkinLocked)
                _createWholeSkin = wholeSkinProbe.Status == TaskStatus.RanToCompletion && wholeSkinProbe.Result;
        }

        // Only affects the normal (stack vs overwrite), but drawn unconditionally so it can be found before a normal is picked.
        if (ImGui.Checkbox(cs.WholeSkin, ref _createWholeSkin))
            _createWholeSkinLocked = true;   // an explicit answer — auto-detect never writes over it again
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(cs.WholeSkinTip);

        // Face targets only, detected from the art's shape: a doubled sheet is twice the width of the face's own texture, whose
        // aspect depends on race. Reads image headers only, once per pick.
        if (StatusWindow.IsFaceMaterial(_createMaterial))
        {
            var firstArt = FirstCreateArt();
            var splitKey = firstArt.Length == 0 ? "" : _createMaterial + " " + firstArt;
            if (!_createFaceSplitLocked && splitKey != _createFaceSplitProbedFor)
            {
                _createFaceSplitProbedFor = splitKey;
                // Nothing picked reverts to unticked.
                _createFaceSplit = splitKey.Length > 0
                                && modCreation.LooksLikeDoubledFaceSheet(_createMaterial, firstArt);
            }

            if (ImGui.Checkbox(cs.FaceAsymmetric, ref _createFaceSplit))
                _createFaceSplitLocked = true;   // an explicit answer — the detect never writes over it
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(cs.FaceAsymmetricTip);
        }
        else if (_createFaceSplit)
        {
            // The target moved off a face; the tick described the old one.
            _createFaceSplit = false;
            _createFaceSplitProbedFor = "";
        }
    }

    private void DrawGlowOptions()
    {
        var cs = Strings.Create;

        // ── glow ─────────────────────────────────────────────────────────────
        // Glow needs a diffuse (its colour is per pixel) and a skin or face target to cut a shell from. Dimmed, not hidden, otherwise.
        bool glowHasArt   = SlotEnabled("Diffuse") && _createDiffuse.Length > 0;
        bool glowCanShell = StatusWindow.IsSkinMaterial(_createMaterial) || StatusWindow.IsFaceMaterial(_createMaterial);
        bool glowAllowed  = glowHasArt && glowCanShell;
        if (!glowAllowed) _createGlow = GlowStyle.None;

        using (ImRaii.PushStyle(ImGuiStyleVar.Alpha,
                   ImGui.GetStyle().Alpha * (glowAllowed ? 1f : 0.5f)))
        using (ImRaii.Disabled(!glowAllowed))
        {
            bool glowing = _createGlow != GlowStyle.None;
            if (ImGui.Checkbox(cs.Glow, ref glowing))
                _createGlow = glowing ? GlowStyle.Always : GlowStyle.None;
        }
        // ReasonTooltip: a disabled item reports no hover under the default flags.
        ProteusStyle.ReasonTooltip(!glowHasArt ? cs.GlowNeedsDiffuse
                                 : !glowCanShell ? cs.GlowNeedsSkin
                                 : cs.GlowTip);

        if (_createGlow != GlowStyle.None)
        {
            ImGui.Indent();
            if (ImGui.RadioButton(cs.GlowAlways, _createGlow == GlowStyle.Always))
                _createGlow = GlowStyle.Always;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(cs.GlowAlwaysTip);

            if (ImGui.RadioButton(cs.GlowDarkOnly, _createGlow == GlowStyle.DarkOnly))
                _createGlow = GlowStyle.DarkOnly;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(cs.GlowDarkOnlyTip);
            ImGui.Unindent();
        }

        ImGui.Separator();
    }

    private void DrawCreateButton()
    {
        var cs = Strings.Create;

        // Only enabled slots count.
        bool anyTexture = (SlotEnabled("Diffuse") && _createDiffuse.Length > 0)
            || (SlotEnabled("Mask") && _createMask.Length > 0)
            || (SlotEnabled("Normal") && _createNormal.Length > 0)
            || (SlotEnabled("Index") && _createIndex.Length > 0);
        bool valid = !string.IsNullOrWhiteSpace(_createName)
            && !string.IsNullOrWhiteSpace(_createMaterial)
            && anyTexture;

        // Penumbra loads a new mod asynchronously, so enabling it is pumped across frames; the button stays inert meanwhile.
        if (modCreation.IsAwaiting)
        {
            if (modCreation.Pump() is { } pumped)
            {
                _createStatus = pumped.Message;
                _createStatusOk = pumped.Ok;
            }
            valid = false;
        }

        using (ImRaii.Disabled(!valid))
            if (ImGui.Button(cs.CreateBtn))
            {
                var r = modCreation.Create(
                    _createName, _createAuthor, _createMaterial,
                    SlotEnabled("Diffuse") ? StatusWindow.NullIfEmpty(_createDiffuse) : null,
                    SlotEnabled("Mask")    ? StatusWindow.NullIfEmpty(_createMask)    : null,
                    SlotEnabled("Normal")  ? StatusWindow.NullIfEmpty(_createNormal)  : null,
                    SlotEnabled("Index")   ? StatusWindow.NullIfEmpty(_createIndex)   : null,
                    _createWholeSkin,
                    _createFaceSplit && StatusWindow.IsFaceMaterial(_createMaterial),
                    _createGlow);
                _createStatus = r.Message;
                _createStatusOk = r.Ok;
                if (r.Ok)   // keep name/author/material for a quick second mod; clear the pickers
                {
                    _createDiffuse = _createMask = _createNormal = _createIndex = "";
                    // Reset the tick and its lock: a hand-set answer must not decide the next mod.
                    _createWholeSkin = _createWholeSkinLocked = false;
                    _createWholeSkinProbedFor = "";
                    _createWholeSkinProbe = null;
                    // Same for the face split, so the shape detect answers for the next art.
                    _createFaceSplit = _createFaceSplitLocked = false;
                    _createFaceSplitProbedFor = "";
                    // Same rule again: the choice belonged to the art just consumed.
                    _createGlow = GlowStyle.None;
                }
            }
        // ReasonTooltip: the button is submitted disabled, so a bare IsItemHovered never fires.
        ProteusStyle.ReasonTooltip(valid ? null : cs.CreateDisabledTip);

        if (_createStatus != null)
            ImGui.TextColored(
                _createStatusOk ? new Vector4(0.4f, 0.9f, 0.4f, 1f) : new Vector4(1f, 0.5f, 0.4f, 1f),
                _createStatus);
    }

    /// <summary>
    /// Read the picked material and narrow the texture rows to what it can consume, dropping any file
    /// browsed into a row that just lost its slot so nothing invisible reaches Create.
    /// </summary>
    private void ResolveCreateSlots()
    {
        _createSlotsFor = _createMaterial;
        _createSlotsNextTick = 0;
        var slots = modCreation.ResolveMaterialSlots(_createMaterial);
        // All-null means UNREADABLE, not "has no textures" — keep null so every row stays live.
        _createSlots = slots.Diffuse == null && slots.Normal == null
                    && slots.Mask == null && slots.Index == null
            ? null : slots;
        if (!SlotEnabled("Diffuse")) _createDiffuse = "";
        if (!SlotEnabled("Mask"))    _createMask = "";
        if (!SlotEnabled("Normal"))  _createNormal = "";
        if (!SlotEnabled("Index"))   _createIndex = "";
    }

    /// <summary>
    /// Can the picked material consume this slot? Index is also allowed on skin and face, which never declare an <c>_id</c> sampler
    /// but take a Proteus index. A null <c>_createSlots</c> (unreadable material) enables everything.
    /// </summary>
    private bool SlotEnabled(string label) => _createSlots == null || label switch
    {
        "Diffuse" => _createSlots.Diffuse != null,
        "Mask"    => _createSlots.Mask    != null,
        "Normal"  => _createSlots.Normal  != null,
        "Index"   => _createSlots.Index != null || StatusWindow.IsSkinMaterial(_createMaterial),
        _         => true,
    };

    /// <summary>One texture slot: file name, Browse and Clear. A slot the material can't consume is dimmed and inert, with <paramref name="disabledReason"/> on hover.</summary>
    /// <param name="slot">Invariant slot token ("Diffuse", "Mask", "Normal", "Index"): the ImGui id and the callback's switch key. Never translated.</param>
    /// <param name="label">What the user reads. Localized.</param>
    /// <param name="labelColumn">Where the file name starts, shared by all four rows so the names form a column.</param>
    private void DrawTextureRow(string slot, string label, float labelColumn, ref string path,
                                bool enabled = true, string? disabledReason = null)
    {
        // Dims the row's text, which ImRaii.Disabled doesn't reach; label and name stay outside the disabled scope so tooltips remain hoverable.
        using var dim = ImRaii.PushStyle(ImGuiStyleVar.Alpha,
            ImGui.GetStyle().Alpha * (enabled ? 1f : 0.5f));

        // Only a disabled row has anything to explain.
        var reason = enabled ? null : disabledReason;

        var shown = path.Length == 0 ? Strings.Common.None : Path.GetFileName(path);
        ImGui.TextUnformatted($"{label}:");
        ProteusStyle.ReasonTooltip(reason);
        ImGui.SameLine(labelColumn);
        ImGui.TextUnformatted(enabled ? shown : Strings.Create.SlotUnused);

        ImGui.SameLine(ProteusStyle.S(360f));
        // ref can't cross the dialog callback; the slot string routes the picked path to its field.
        var captured = slot;
        // ImRaii.Disabled, not a short-circuit: an unsubmitted button can't be hovered for its reason tooltip.
        using (ImRaii.Disabled(!enabled))
        {
            if (ImGui.SmallButton($"{Strings.Common.Browse}##browse_{slot}"))
            {
                fileDialog.OpenFileDialog(
                    string.Format(Strings.Create.PickTextureTitleFmt, label),
                    "Images{.png,.tex,.dds,.jpg,.jpeg,.bmp,.tga}",
                    (ok, paths) =>
                    {
                        if (!ok) return;
                        var picked = paths.FirstOrDefault() ?? "";
                        switch (captured)
                        {
                            case "Diffuse": _createDiffuse = picked; break;
                            case "Mask":    _createMask = picked; break;
                            case "Normal":  _createNormal = picked; break;
                            case "Index":   _createIndex = picked; break;
                        }
                    }, 1);
            }
        }
        ProteusStyle.ReasonTooltip(reason);
        if (enabled && path.Length > 0)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton($"{Strings.Common.Clear}##clear_{slot}"))
                path = "";
        }
    }

    /// <summary>The art the shape detect judges: the first picked file in an enabled slot, in row order, or empty.</summary>
    private string FirstCreateArt()
    {
        if (SlotEnabled("Diffuse") && _createDiffuse.Length > 0) return _createDiffuse;
        if (SlotEnabled("Normal") && _createNormal.Length > 0) return _createNormal;
        if (SlotEnabled("Mask") && _createMask.Length > 0) return _createMask;
        if (SlotEnabled("Index") && _createIndex.Length > 0) return _createIndex;
        return "";
    }

    /// <summary>Short "what is this" tag for a material path, the picker's left column. Path-derived only —
    /// no game data, so it's free to call while drawing.</summary>
    private static string SlotHint(string p)
    {
        // Skin surfaces come from the shared taxonomy, which also keeps weapon paths containing /obj/body/ out of Body.
        if (ShellSurface.KeyFor(p) is { } surface) return ShellSurface.Label(surface.Kind);
        if (p.Contains("chara/weapon/", StringComparison.OrdinalIgnoreCase)) return "Weapon";
        if (p.Contains("chara/equipment/", StringComparison.OrdinalIgnoreCase)) return "Gear";
        if (p.Contains("chara/accessory/", StringComparison.OrdinalIgnoreCase)) return "Accessory";
        return "Other";
    }

    /// <summary>Picker row text: slot tag plus file name (full path in the tooltip), with the body type on skin rows.</summary>
    /// <param name="skin">The caller's already-computed classification.</param>
    private static string PickerLabel(string p, bool skin)
    {
        var name = Path.GetFileName(p);
        var body = skin ? UVRemapService.InferBodyType(p) : null;
        return body != null
            ? $"{SlotHint(p)}  ·  {name}  ({body})"
            : $"{SlotHint(p)}  ·  {name}";
    }

    /// <summary>Snapshot the equipped materials for the picker, skin first. Called only when the picker opens: the walk costs several ms.</summary>
    private void RebuildMaterialPicker()
    {
        var src = modCreation.ListActiveMaterials(out _matPickerStale);
        _matPickerItems = src
            .Select(p => { bool skin = StatusWindow.IsSkinMaterial(p); return (Path: p, Label: PickerLabel(p, skin), Skin: skin); })
            .OrderByDescending(e => e.Skin)                          // skin group on top
            .ThenBy(e => e.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
