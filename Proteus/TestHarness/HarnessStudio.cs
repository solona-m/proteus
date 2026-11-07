using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using Proteus.Gui;

namespace Proteus.TestHarness;

/// <summary>
/// The pure parts of the Studio routes: body validation, the drag's matrix, the model match and the put-back plan.
/// No game, no panel: tested headlessly.
/// </summary>
internal static class HarnessStudio
{
    internal static readonly string[] SetFields = ["tool", "mode", "view", "mirror", "adjacent", "falloffMm", "moveSkin", "unlockAll", "lock", "unlock", "part"];
    internal static readonly string[] DragFields = ["offsetMm", "axis", "degrees", "factor", "factors", "undo"];

    // ── bodies ──────────────────────────────────────────────────────────────

    /// <summary>A <c>studio-set</c> body; every field optional, at least one given. Fields outside <see cref="SetFields"/>
    /// are refused by the router before this.</summary>
    internal static (StudioSetRequest? Request, string? Error) ParseSet(JsonElement body)
    {
        string? tool = null, mode = null, view = null, part = null;
        bool? mirror = null, adjacent = null, moveSkin = null;
        float? falloffMm = null;
        bool unlockAll = false;
        var lockList = new List<string>();
        var unlockList = new List<string>();
        int given = 0;

        foreach (var p in body.EnumerateObject())
        {
            given++;
            switch (p.Name)
            {
                case "tool":
                    if (!OneOf(p.Value, out tool, "move", "rotate", "scale")) return (null, "tool must be move, rotate or scale");
                    break;
                case "mode":
                    if (!OneOf(p.Value, out mode, "parts", "polygons", "all")) return (null, "mode must be parts, polygons or all");
                    break;
                case "view":
                    if (!OneOf(p.Value, out view, "character", "model")) return (null, "view must be character or model");
                    break;
                case "mirror":
                    if (!Bool(p.Value, out var m)) return (null, "mirror must be true or false");
                    mirror = m;
                    break;
                case "adjacent":
                    if (!Bool(p.Value, out var a)) return (null, "adjacent must be true or false");
                    adjacent = a;
                    break;
                case "falloffMm":
                    if (p.Value.ValueKind != JsonValueKind.Number || !p.Value.TryGetDouble(out var f)
                        || !double.IsFinite(f) || f < PartsPanel.MinBrushMm || f > PartsPanel.MaxBrushMm)
                        return (null, $"falloffMm must be a number from {PartsPanel.MinBrushMm} to {PartsPanel.MaxBrushMm}");
                    falloffMm = (float)f;
                    break;
                case "moveSkin":
                    if (!Bool(p.Value, out var s)) return (null, "moveSkin must be true or false");
                    moveSkin = s;
                    break;
                case "unlockAll":
                    if (p.Value.ValueKind != JsonValueKind.True) return (null, "unlockAll must be true");
                    unlockAll = true;
                    break;
                case "lock":
                    if (!Labels(p.Value, lockList)) return (null, "lock must be a non-empty array of part labels");
                    break;
                case "unlock":
                    if (!Labels(p.Value, unlockList)) return (null, "unlock must be a non-empty array of part labels");
                    break;
                case "part":
                    if (p.Value.ValueKind != JsonValueKind.String || p.Value.GetString() is not { Length: > 0 } label)
                        return (null, "part must be a part label");
                    part = label;
                    break;
                default:
                    return (null, $"unknown field {p.Name}");
            }
        }
        if (given == 0) return (null, "give at least one of " + string.Join(", ", SetFields));
        if (lockList.Intersect(unlockList, StringComparer.Ordinal).FirstOrDefault() is { } both)
            return (null, $"{both} is in both lock and unlock");
        return (new StudioSetRequest(tool, mode, view, mirror, adjacent, moveSkin, unlockAll, lockList, unlockList, part, falloffMm), null);
    }

    /// <summary>A <c>studio-drag</c> body: exactly one kind of drag, or <c>undo</c> alone.</summary>
    internal static (StudioDragRequest? Request, string? Error) ParseDrag(JsonElement body)
    {
        bool has(string name) => body.TryGetProperty(name, out _);
        foreach (var p in body.EnumerateObject())
            if (!DragFields.Contains(p.Name, StringComparer.Ordinal)) return (null, $"unknown field {p.Name}");

        int kinds = (has("offsetMm") ? 1 : 0) + (has("axis") || has("degrees") ? 1 : 0)
                  + (has("factor") || has("factors") ? 1 : 0) + (has("undo") ? 1 : 0);
        if (kinds == 0) return (null, "give offsetMm, axis and degrees, factor or factors, or undo");
        if (kinds > 1) return (null, "give one kind of drag: offsetMm, axis and degrees, factor or factors, or undo");

        if (body.TryGetProperty("undo", out var undo))
        {
            if (undo.ValueKind != JsonValueKind.True) return (null, "undo must be true");
            return (new StudioDragRequest("undo", default, default, 0f, default), null);
        }
        if (body.TryGetProperty("offsetMm", out var off))
        {
            if (!Vector(off, out var mm) || !Within(mm, 1000f)) return (null, "offsetMm must be [x, y, z], each within ±1000");
            return (new StudioDragRequest("move", mm / 1000f, default, 0f, default), null);
        }
        if (has("axis") || has("degrees"))
        {
            if (!body.TryGetProperty("axis", out var axisEl) || !body.TryGetProperty("degrees", out var degEl))
                return (null, "axis and degrees go together");
            Vector3 axis;
            if (axisEl.ValueKind == JsonValueKind.String)
            {
                axis = axisEl.GetString() switch { "x" => Vector3.UnitX, "y" => Vector3.UnitY, "z" => Vector3.UnitZ, _ => default };
                if (axis == default) return (null, "axis must be \"x\", \"y\", \"z\" or [x, y, z]");
            }
            else if (!Vector(axisEl, out axis) || axis.LengthSquared() < 1e-12f || !float.IsFinite(axis.LengthSquared()))
                return (null, "axis must be \"x\", \"y\", \"z\" or a non-zero [x, y, z]");
            if (!Number(degEl, out var degrees) || MathF.Abs(degrees) > 360f) return (null, "degrees must be a number within ±360");
            return (new StudioDragRequest("rotate", default, Vector3.Normalize(axis), degrees, default), null);
        }
        if (has("factor") && has("factors")) return (null, "give factor or factors, not both");
        Vector3 factors;
        if (body.TryGetProperty("factor", out var f))
        {
            if (!Number(f, out var one) || one < 0.1f || one > 10f) return (null, "factor must be a number from 0.1 to 10");
            factors = new Vector3(one);
        }
        else if (!Vector(body.GetProperty("factors"), out factors)
                 || factors.X < 0.1f || factors.Y < 0.1f || factors.Z < 0.1f || factors.X > 10f || factors.Y > 10f || factors.Z > 10f)
            return (null, "factors must be [x, y, z], each from 0.1 to 10");
        return (new StudioDragRequest("scale", default, default, 0f, factors), null);
    }

    /// <summary>The model-space matrix of a Rotate or Scale drag about <paramref name="pivot"/>: the gizmos' own builders.</summary>
    internal static Matrix4x4 DragTransform(StudioDragRequest drag, Vector3 pivot)
        => drag.Kind == "rotate"
            ? RotateGizmo.About(pivot, drag.Axis, drag.Degrees * MathF.PI / 180f)
            : PartScaleDrag.Build(pivot, drag.Factors);

    /// <summary>
    /// The model a request names, among the Studio's list (file, game path): by file or game path, either slash, any
    /// case. A game path two files share is ambiguous; give the file.
    /// </summary>
    internal static (int Index, string? Error) MatchModel(IReadOnlyList<(string File, string GamePath)> models, string wanted)
    {
        static string Norm(string s) => s.Replace('\\', '/').Trim('/');
        string w = Norm(wanted);
        for (int i = 0; i < models.Count; i++)
            if (string.Equals(Norm(models[i].File), w, StringComparison.OrdinalIgnoreCase)) return (i, null);
        var byPath = Enumerable.Range(0, models.Count)
            .Where(i => string.Equals(Norm(models[i].GamePath), w, StringComparison.OrdinalIgnoreCase)).ToList();
        return byPath.Count switch
        {
            1 => (byPath[0], null),
            0 => (-1, "model-not-in-mod"),
            _ => (-1, "model-ambiguous"),
        };
    }

    // ── put back ────────────────────────────────────────────────────────────

    /// <summary>What put-back finds, gathered in the same framework call that acts on it.</summary>
    /// <param name="Patched">Every file the mod's Studio record lists now, with its SHA-256 now (null: unreadable).</param>
    /// <param name="Saved">The files the session's saves wrote, with the hash each had after the harness's last save.</param>
    /// <param name="ModelIsHarness">The panel still has the session's model open.</param>
    internal sealed record StudioFacts(IReadOnlyDictionary<string, string?> Patched, IReadOnlyDictionary<string, string> Saved,
                                       bool ModelIsHarness, int EditVersionNow, int EditVersionLast, bool SaveOwed,
                                       bool DragOpen, bool ApplyingSizes);

    /// <summary>The put-back plan.</summary>
    /// <param name="Files">revert / changed / none.</param>
    /// <param name="Panel">restore (close the model, restore the snapshot) / changed (leave the panel as it is).</param>
    /// <param name="ReloadByName">After a revert, reload the session mod by name only: the panel shows something else.</param>
    internal sealed record StudioPlan(string Files, string Panel, string? Code, bool ReloadByName);

    /// <summary>
    /// Revert only what is provably the harness's: every patched file one it saved and still hashing as it wrote it, and
    /// — while the panel shows the harness's model — that model untouched since its last drag, nothing owed or open.
    /// Anything else is reported <c>changed</c>, and neither reverted nor closed.
    /// </summary>
    internal static StudioPlan PlanPutBack(StudioFacts f)
    {
        string? fileCode = null;
        if (f.ApplyingSizes) fileCode = "applying-sizes";
        else if (f.Patched.Keys.FirstOrDefault(p => !f.Saved.ContainsKey(p)) != null) fileCode = "not-harness-saved";
        else if (f.Patched.Any(p => p.Value == null || !string.Equals(p.Value, f.Saved[p.Key], StringComparison.OrdinalIgnoreCase)))
            fileCode = "hash-changed";

        if (!f.ModelIsHarness)
        {
            // The user has opened something else: the panel is theirs; the files are judged on their own.
            if (fileCode != null) return new StudioPlan("changed", "changed", fileCode, false);
            return f.Patched.Count == 0 ? new StudioPlan("none", "changed", null, false)
                                        : new StudioPlan("revert", "changed", null, true);
        }

        string? modelCode = f.DragOpen ? "drag-open"
                          : f.SaveOwed ? "save-owed"
                          : f.EditVersionNow != f.EditVersionLast ? "model-edited"
                          : null;
        if (fileCode != null || modelCode != null)
            return new StudioPlan("changed", "changed", fileCode ?? modelCode, false);
        return new StudioPlan(f.Patched.Count == 0 ? "none" : "revert", "restore", null, false);
    }

    // ── json helpers ────────────────────────────────────────────────────────

    private static bool OneOf(JsonElement v, out string? value, params string[] allowed)
    {
        value = v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        return value != null && allowed.Contains(value, StringComparer.Ordinal);
    }

    private static bool Bool(JsonElement v, out bool value)
    {
        value = v.ValueKind == JsonValueKind.True;
        return v.ValueKind is JsonValueKind.True or JsonValueKind.False;
    }

    private static bool Number(JsonElement v, out float value)
    {
        value = 0f;
        if (v.ValueKind != JsonValueKind.Number || !v.TryGetDouble(out var d) || !double.IsFinite(d)) return false;
        value = (float)d;
        return float.IsFinite(value);
    }

    private static bool Vector(JsonElement v, out Vector3 value)
    {
        value = default;
        if (v.ValueKind != JsonValueKind.Array || v.GetArrayLength() != 3) return false;
        var xyz = new float[3];
        int i = 0;
        foreach (var item in v.EnumerateArray())
            if (!Number(item, out xyz[i++])) return false;
        value = new Vector3(xyz[0], xyz[1], xyz[2]);
        return true;
    }

    private static bool Within(Vector3 v, float limit) => MathF.Abs(v.X) <= limit && MathF.Abs(v.Y) <= limit && MathF.Abs(v.Z) <= limit;

    private static bool Labels(JsonElement v, List<string> into)
    {
        if (v.ValueKind != JsonValueKind.Array || v.GetArrayLength() == 0) return false;
        foreach (var item in v.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || item.GetString() is not { Length: > 0 } label) return false;
            into.Add(label);
        }
        return true;
    }
}
