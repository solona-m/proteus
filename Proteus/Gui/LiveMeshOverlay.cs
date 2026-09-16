using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using Proteus.Interop;
using Proteus.Services;
using XivLiveMesh;

namespace Proteus.Gui;

/// <summary>
/// <c>/proteus livemesh</c>: draw one of the local player's models, posed on the CPU, as a wireframe over the
/// character.
/// <para/>
/// The go/no-go check for brushing on the live character. Everything that follows — picking a point under
/// the mouse, mapping it back into the model — assumes the CPU pose lands where the game draws. If the
/// wireframe sits on the cloth, it does; if it floats off, the overlay says which input to suspect (race
/// codes, deformer steps, bones the skeleton lacks).
/// </summary>
public sealed unsafe class LiveMeshOverlay(IObjectTable objects, IDataManager data, PenumbraBridge penumbra,
                                           IChatGui chat, IPluginLog log)
{
    /// <summary>Most triangles drawn per frame; a denser model is drawn with every n-th triangle.</summary>
    private const int MaxDrawnTriangles = 30000;

    private readonly LivePose pose = new();
    private readonly LiveMeshPoser poser = new();

    private bool enabled;
    private string filter = "";

    private string? meshKey;
    private SkinnedMesh? mesh;
    private ushort modelRace;
    private Vector3[] world = [];
    private PbdFile? pbd;
    private bool pbdTried;

    /// <summary>Handle the arguments after <c>/proteus livemesh</c>. Returns the line to print.</summary>
    public string Command(string args)
    {
        var a = args.Trim();
        switch (a.ToLowerInvariant())
        {
            case "off":
                enabled = false;
                return "live mesh overlay off";
            case "nodeform":
                poser.ApplyDeformer = false;
                return "racial deformer OFF (compare alignment)";
            case "deform":
                poser.ApplyDeformer = true;
                return "racial deformer on";
            default:
                enabled = true;
                filter = a;
                meshKey = null;   // re-pick
                return $"live mesh overlay on for '{(a.Length > 0 ? a : "top")}' (a slot name, slot number or file name) — " +
                       "'/proteus livemesh off' to stop, 'nodeform' / 'deform' to compare";
        }
    }

    public void Draw()
    {
        if (!enabled) return;
        try { DrawCore(); }
        catch (Exception ex)
        {
            enabled = false;
            log.Error(ex, "[Proteus] livemesh overlay failed; turned off");
            chat.PrintError($"[Proteus] livemesh overlay failed: {ex.Message}");
        }
    }

    private void DrawCore()
    {
        var dl = ImGui.GetForegroundDrawList();
        var origin = ImGui.GetMainViewport().Pos;
        var textAt = origin + new Vector2(20, 60);

        // Every way this can come up empty SAYS so on screen. A silent overlay is indistinguishable from one
        // that is drawing somewhere off the character, and that is exactly the question it exists to answer.
        void Status(string text) => dl.AddText(textAt, 0xFF00FFFF, "livemesh: " + text);

        var cb = LiveCharacter.Player(objects);
        if (cb == null) { Status("no local player draw object"); return; }

        if (!pose.Read(cb)) { Status("could not read the skeleton"); return; }
        if (!EnsureMesh(cb, out var why) || mesh == null) { Status(why); return; }
        if (!ScreenProjection.TryCapture(out var projection)) { Status("no active camera"); return; }

        if (world.Length < mesh.VertexCount) world = new Vector3[mesh.VertexCount];
        EnsurePbd();
        poser.Pose(mesh, pose, pbd, world, modelRace);

        uint colour = 0x9900FFFF;   // ABGR: yellow, part transparent
        int stride = Math.Max(1, mesh.TriangleCount / MaxDrawnTriangles);
        var tris = mesh.Triangles;
        for (int t = 0; t < mesh.TriangleCount; t += stride)
        {
            int o = t * 3;
            if (!projection.WorldToScreen(world[tris[o]], out var a)) continue;
            if (!projection.WorldToScreen(world[tris[o + 1]], out var b)) continue;
            if (!projection.WorldToScreen(world[tris[o + 2]], out var c)) continue;
            a += origin; b += origin; c += origin;
            dl.AddLine(a, b, colour);
            dl.AddLine(b, c, colour);
            dl.AddLine(c, a, colour);
        }

        var root = pose.Root;
        var scale = new Vector3(root.M11, root.M12, root.M13).Length();
        dl.AddText(textAt, 0xFF00FFFF,
            $"livemesh: {meshKey}\nrace model c{modelRace:D4} -> body c{pose.GenderRace:D4}, deformer steps " +
            $"{poser.DeformerSteps}{(poser.ApplyDeformer ? "" : " (OFF)")}, pbd {(pbd != null ? "loaded" : "missing")}\n" +
            $"bones: mesh {mesh.BoneNames.Length}, missing {poser.MissingBones}, skeleton {pose.BoneCount}; " +
            $"root scale {scale:F3}\nverts {mesh.VertexCount}, tris {mesh.TriangleCount} (drawing 1/{stride})");
    }

    /// <summary>Slot names in the order a human's models are stored, for matching a filter like "top".</summary>
    private static readonly string[] SlotNames = ["met", "top", "glv", "dwn", "sho", "ear", "nek", "wrs", "rir", "ril"];

    /// <summary>
    /// Find the model to draw and (re)build its mesh when the model or its enabled shapes change.
    /// <para/>
    /// Matched by SLOT first: a mod's file is named whatever its author chose, so "top" has to mean the model in
    /// the body slot, not a file with "top" in its name. A number picks a slot index; anything else that is
    /// not a slot name is matched against the loaded file names.
    /// </summary>
    private bool EnsureMesh(CharacterBase* cb, out string why)
    {
        var want = filter.Length > 0 ? filter : "top";
        int slot = Array.IndexOf(SlotNames, want.ToLowerInvariant());
        if (slot < 0 && int.TryParse(want, out var n)) slot = n;

        var seen = new List<string>();
        int i = -1;
        foreach (var modelPtr in cb->ModelsSpan)
        {
            i++;
            var model = modelPtr.Value;
            if (model == null || model->ModelResourceHandle == null) continue;
            var handle = model->ModelResourceHandle;
            var name = handle->FileName.ToString();
            if (string.IsNullOrEmpty(name)) continue;
            seen.Add($"  {i}: {name}");
            bool match = slot >= 0 ? i == slot : name.IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0;
            if (!match) continue;

            var shapes = new HashSet<string>(StringComparer.Ordinal);
            uint mask = model->EnabledShapeKeyIndexMask;
            if (mask != 0)
                foreach (var kv in handle->Shapes)
                    if (kv.Item2 is >= 0 and < 32 && (mask & (1u << kv.Item2)) != 0)
                        shapes.Add(kv.Item1.ToString());

            var key = name + "|" + string.Join(",", shapes);
            if (key == meshKey)
            {
                why = mesh == null ? $"could not read slot {i}: {name}" : "";
                return mesh != null;
            }

            var bytes = LiveCharacter.ModelBytes(handle, name);
            if (bytes == null) { why = $"no bytes for slot {i}: {name}"; return false; }

            var gamePath = LiveCharacter.GamePathOf(penumbra, name);
            mesh = ModelSkinReader.Read(bytes, shapes, gamePath ?? name);
            modelRace = mesh?.GenderRace ?? 0;
            meshKey = key;
            var line = mesh == null
                ? $"livemesh: could not read {name}"
                : $"livemesh: {name} (game path {gamePath ?? "?"}), {mesh.VertexCount} verts, shapes [{string.Join(",", shapes)}]";
            chat.Print($"[Proteus] {line}");
            log.Information("[Proteus] {0}", line);
            why = line;
            return mesh != null;
        }
        why = $"no loaded model matches '{want}'. Loaded:\n" + string.Join("\n", seen);
        return false;
    }

    private void EnsurePbd()
    {
        if (pbdTried) return;
        pbdTried = true;
        pbd = LiveCharacter.LoadPbd(penumbra, data, log);
    }
}
