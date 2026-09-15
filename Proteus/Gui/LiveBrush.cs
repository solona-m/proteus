using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
using Proteus.Interop;
using Proteus.Localization;
using Model = FFXIVClientStructs.FFXIV.Client.Graphics.Render.Model;
using Proteus.Services;
using XivLiveMesh;

namespace Proteus.Gui;

/// <summary>What a brush stroke needs from wherever it is painted: the model viewer or the live character.</summary>
public interface IBrushSurface
{
    /// <summary>The brush centre on the surface, in the model's own (bind-pose) space; null when off the model.</summary>
    Vector3? Cursor { get; }

    /// <summary>The brush is down and being dragged.</summary>
    bool Painting { get; }

    /// <summary>True for the one frame a stroke is released.</summary>
    bool StrokeEnded { get; }

    /// <summary>Unit direction toward the viewer, in the model's space.</summary>
    Vector3 ToViewer { get; }
}

/// <summary>
/// The brush, painted straight onto the character in the game world.
/// <para/>
/// Each frame it poses the garment being edited onto the character's skeleton (<see cref="LiveMeshPoser"/>),
/// casts the mouse ray against it, and turns the hit back into the model's bind-pose space through the
/// triangle and barycentrics it landed on — so the brush's own geometry, <see cref="MeshVolumeSolve"/>, works
/// exactly as it does in the viewer. It poses the EDITED surface, so the brush follows a pull while it is being
/// painted, before the game has been handed the change.
/// <para/>
/// Armed per frame by the Studio tab. Updated from the UI draw BEFORE any window, so the tab reads a state
/// that belongs to this frame, and so "is the mouse over a window" means the real windows rather than the
/// one currently being drawn.
/// <para/>
/// Clicks are taken from the game only while the mouse is over the garment or a stroke is under way, by
/// <see cref="ImGui.SetNextFrameWantCaptureMouse"/>, which Dalamud honours by not passing the button to the
/// game. Everywhere else the game keeps the mouse, and holding Alt gives it back even over the garment.
/// </summary>
public sealed unsafe class LiveBrush(IObjectTable objects, IDataManager data, PenumbraBridge penumbra, IPluginLog log)
    : IBrushSurface
{
    private readonly LivePose pose = new();
    private readonly LiveMeshPoser poser = new();
    private PbdFile? pbd;
    private bool pbdTried;

    // ── armed by the Studio tab ──
    private int brushArmedFrame = -10, pickArmedFrame = -10;
    private string? targetKey;
    private byte[]? targetBytes;
    private MeshVolumeSolve? volume;
    private float radius;
    private string? modsRoot;
    private Action<string>? onPicked;

    // ── the target garment, rebuilt when it or its enabled shapes change ──
    private string? meshKey;
    private SkinnedMesh? mesh;
    private bool[] skinTriangle = [];
    private int[] spareBase = [];
    private Vector3[] edited = [];
    private Vector3[] world = [];

    // ── pick mode: every worn model from a mod ──
    private readonly Dictionary<string, (SkinnedMesh Mesh, bool[] Skin)> pickMeshes = new(StringComparer.OrdinalIgnoreCase);
    private Vector3[] pickWorld = [];

    public Vector3? Cursor { get; private set; }
    public bool Painting { get; private set; }
    public bool StrokeEnded { get; private set; }
    public Vector3 ToViewer { get; private set; } = Vector3.UnitZ;

    /// <summary>Why the brush cannot paint right now, for the tab to show; null when it can.</summary>
    public string? Problem { get; private set; }

    /// <summary>True while the mouse is over the garment, so the tab can say so.</summary>
    public bool Hovering { get; private set; }

    /// <summary>
    /// Keep the brush live for this frame on one model.
    /// </summary>
    /// <param name="modelFile">The model file on disk, as the mod supplies it.</param>
    /// <param name="modelBytes">Its bytes as the brush opened it — the file the edit's vertex order belongs to.</param>
    /// <param name="brushRadius">In the model's units (metres).</param>
    /// <param name="showWind">Draw the painted wind over the garment — while the wind brush is the tool.</param>
    /// <param name="mirror">The brush also paints the mirror image across the midline: draw a second ring there.</param>
    /// <param name="partOf">Which part a vertex (ModelPartReader order) belongs to, as any stable id — lets Shift
    /// light up the whole part under the mouse; null for no part locking.</param>
    /// <param name="lockClicked">A Shift-click on the garment — or with <paramref name="pickParts"/>, any click — with a
    /// vertex of the triangle it landed on.</param>
    /// <param name="pickParts">Toggle Parts on the character: nothing paints; the part under the mouse lights up and a
    /// click on it goes to <paramref name="lockClicked"/>, the way a click on the model view ticks a part.</param>
    /// <param name="partTicked">With <paramref name="pickParts"/>: whether a part (by <paramref name="partOf"/>'s id) is
    /// ticked, for the tint over the ticked parts.</param>
    /// <param name="tickedVersion">Changes whenever the ticked set does, so the tint is rebuilt only then.</param>
    /// <param name="moveGizmo">The Move tool: nothing paints. The part <paramref name="partTicked"/> names is tinted, a
    /// click on the garment goes to <paramref name="lockClicked"/> to choose the part, and this gizmo is drawn at
    /// <paramref name="movePivot"/> on the posed character and driven by the mouse. Null for every other tool.</param>
    /// <param name="movePivot">The gizmo's centre in the model's space; null when no part is chosen yet.</param>
    /// <param name="graftedGamePath">
    /// Set for a model the character wears through Proteus rather than as a file of its own — an imported
    /// content piece, whose geometry the game only ever draws copied into a Proteus shell. The file is then
    /// posed on the character's skeleton without being found among the drawn models, and this is the path its
    /// race is read from. Null for an ordinary model, which must be drawn to be painted.
    /// </param>
    internal void ArmBrush(string modelFile, byte[] modelBytes, MeshVolumeSolve solve, float brushRadius,
                           bool showWind = false, bool mirror = false, Func<int, int>? partOf = null,
                           Action<int>? lockClicked = null, bool pickParts = false,
                           Func<int, bool>? partTicked = null, int tickedVersion = 0,
                           TranslateGizmo? moveGizmo = null, Vector3? movePivot = null,
                           string? graftedGamePath = null)
    {
        this.moveGizmo = moveGizmo;
        this.movePivot = movePivot;
        this.graftedGamePath = graftedGamePath;
        brushArmedFrame = ImGui.GetFrameCount();
        targetKey = BodyShapeReader.PathKey(modelFile);
        targetBytes = modelBytes;
        volume = solve;
        radius = brushRadius;
        this.showWind = showWind;
        this.mirror = mirror;
        this.partOf = partOf;
        this.lockClicked = lockClicked;
        this.pickParts = pickParts;
        this.partTicked = partTicked;
        this.tickedVersion = tickedVersion;
    }

    private bool mirror;
    /// <summary>See <see cref="ArmBrush"/>: the game path of a model worn through a Proteus shell, or null.</summary>
    private string? graftedGamePath;
    private Func<int, int>? partOf;
    private Action<int>? lockClicked;

    // ── the Move tool ──
    private TranslateGizmo? moveGizmo;
    private Vector3? movePivot;

    /// <summary>
    /// The pose the gizmo is carried into the world by: the skinning of the chosen part's vertex nearest the pivot.
    /// Re-read every frame between drags so the gizmo follows the character, and HELD for a drag, so an idle sway
    /// does not shift the handle under the mouse while it is being dragged.
    /// </summary>
    private Matrix4x4 moveSkin = Matrix4x4.Identity, moveUnskin = Matrix4x4.Identity;

    // ── Toggle Parts on the character ──
    private bool pickParts;
    private Func<int, bool>? partTicked;
    private int tickedVersion;
    private readonly List<int> tickedTriangles = [];
    private int tickedTrianglesVersion = int.MinValue;
    private SkinnedMesh? tickedTrianglesMesh;

    /// <summary>Opacity of the tint over ticked parts: plain to see, like the model view's accent.</summary>
    private const float TickedOpacity = 0.4f;

    // ── the locked-part wash ──
    private readonly List<int> lockedTriangles = [];
    private int lockedTrianglesVersion = -1;
    private SkinnedMesh? lockedTrianglesMesh;
    private MeshVolumeSolve? lockedTrianglesSolve;

    /// <summary>Opacity of the grey over a locked part: enough to tell it apart, faint like the rest of the overlay.</summary>
    private const float LockedOpacity = 0.3f;

    // ── the part under the mouse while Shift is held ──
    private readonly List<int> hotTriangles = [];
    private int hotPart = int.MinValue;
    private SkinnedMesh? hotMesh;

    /// <summary>Smallest the brush ring is drawn, in pixels, so a millimetre brush on a distant character still shows.</summary>
    private const float MinRingPixels = 4f;

    /// <summary>Below this ring size, in pixels, the falloff dots are left out — they would be one smudge.</summary>
    private const float DotsMinRingPixels = 12f;

    // ── the wind wash ──
    private bool showWind;

    /// <summary>Triangles with any painted wind, rebuilt when the wind or the mesh changes rather than per frame.</summary>
    private readonly List<int> windTriangles = [];
    private int windTrianglesVersion = -1;
    private SkinnedMesh? windTrianglesMesh;

    /// <summary>Most wash triangles drawn per frame; beyond it every n-th, so a fully painted dense garment stays smooth.</summary>
    private const int MaxWashTriangles = 40000;

    /// <summary>Wash opacity at full wind: enough to read, not so much the garment underneath disappears.</summary>
    private const float WashOpacity = 0.45f;

    /// <summary>Keep pick mode live for this frame: the next click on a worn garment names its file.</summary>
    public void ArmPick(string penumbraModsRoot, Action<string> picked)
    {
        pickArmedFrame = ImGui.GetFrameCount();
        modsRoot = penumbraModsRoot;
        onPicked = picked;
    }

    public void Update()
    {
        StrokeEnded = false;
        Hovering = false;
        int frame = ImGui.GetFrameCount();
        bool brushArmed = brushArmedFrame >= frame - 1;
        bool pickArmed = pickArmedFrame >= frame - 1;

        if (!brushArmed)
        {
            // The tab stopped drawing mid-stroke (closed, switched away). Nobody is left to finish the stroke,
            // and the tab flushes its pending save on the way out, so simply let go.
            Painting = false;
            Cursor = null;
            Problem = null;
            moveGizmo?.Release();
            moveGizmo = null;
        }
        if (!brushArmed && !pickArmed) return;

        try
        {
            // The brush has first claim on the mouse. Picking only answers where the brush does not: over a
            // different worn garment, or with no brush tool selected — so a click on the garment being edited
            // paints it, and a click on anything else worn opens that instead.
            if (brushArmed) UpdateBrush();
            if (pickArmed && !Painting && !Hovering) UpdatePick(brushArmed ? targetKey : null);
        }
        catch (Exception ex)
        {
            brushArmedFrame = pickArmedFrame = -10;
            Painting = false;
            Cursor = null;
            Problem = ex.Message;
            log.Error(ex, "[Proteus] live brush failed");
        }
    }

    private void UpdateBrush()
    {
        Cursor = null;
        var cb = LiveCharacter.Player(objects);
        if (cb == null || volume == null || targetBytes == null) { Problem = Strings.Parts.LiveNoCharacter; EndIfPainting(); return; }
        if (!pose.Read(cb)) { Problem = Strings.Parts.LiveNoCharacter; EndIfPainting(); return; }
        if (!EnsureTarget(cb)) { EndIfPainting(); return; }
        if (!ScreenProjection.TryCapture(out var projection)) { Problem = Strings.Parts.LiveNoCharacter; EndIfPainting(); return; }
        Problem = null;

        var m = mesh!;
        FillEdited(m);
        if (!pbdTried) { pbdTried = true; pbd = LiveCharacter.LoadPbd(penumbra, data, log); }
        poser.Pose(m, pose, pbd, world, 0, edited);

        if (moveGizmo != null)
        {
            UpdateMove(projection, m, moveGizmo);
            return;
        }

        // The washes first, so the ring lies on top of them, and whether or not the mouse is over the garment.
        if (pickParts) DrawTickedWash(projection, m);
        else
        {
            if (showWind) DrawWindWash(projection, m);
            DrawLockedWash(projection, m);
        }

        var io = ImGui.GetIO();
        var hit = MouseHit(projection, world, m.Triangles, skinTriangle, out bool overUi);

        // Picking a part instead of painting: every click under Toggle Parts, and a Shift-click under a brush (a lock).
        bool locking = lockClicked != null && (pickParts || io.KeyShift);

        // A stroke in progress owns the mouse until the button comes up, wherever the cursor wanders.
        if (Painting)
        {
            ImGui.SetNextFrameWantCaptureMouse(true);
            if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                Painting = false;
                StrokeEnded = true;
            }
        }
        else if (hit != null && !overUi && !io.KeyAlt)
        {
            ImGui.SetNextFrameWantCaptureMouse(true);
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                if (locking) lockClicked!(m.BaseTriangles[hit.Value.Triangle * 3]);
                else Painting = true;
            }
        }

        if (hit is not { } h || (overUi && !Painting)) return;
        Hovering = true;

        if (locking && !Painting)
        {
            DrawHotPart(projection, m, h.Triangle);
            return;
        }

        // Back into the model through the triangle's UNSHAPED corners, which are the vertices the solve edits.
        int o = h.Triangle * 3;
        var baseTris = m.BaseTriangles;
        Cursor = h.Interpolate(edited[baseTris[o]], edited[baseTris[o + 1]], edited[baseTris[o + 2]]);

        // Toward the camera, carried into the model's space through the pose at the hit.
        var skin = poser.SkinAt(m, m.Triangles[o], pose.Root);
        if (Matrix4x4.Invert(skin, out var unskin))
        {
            var toCam = Vector3.TransformNormal(projection.CameraPosition - h.World, unskin);
            if (toCam.LengthSquared() > 1e-12f) ToViewer = Vector3.Normalize(toCam);
        }

        DrawBrush(projection, h, m, skin);
    }

    /// <summary>A stroke cannot continue on a garment that is no longer there to paint on.</summary>
    private void EndIfPainting()
    {
        if (moveGizmo is { Active: not TranslateGizmo.Handle.None }) moveGizmo.Release();
        if (!Painting) return;
        Painting = false;
        StrokeEnded = true;
    }

    /// <summary>
    /// The Move tool on the character: the chosen part tinted, the gizmo at its pivot, and a click elsewhere on the
    /// garment choosing another part.
    /// <para/>
    /// The gizmo works in the model's own space, the way the solve does, and is carried into the world through ONE
    /// vertex's skinning — the chosen part's vertex nearest the pivot. A part is skinned by more than one bone, so
    /// that is exact only at that vertex; it is also exactly what the eye reads the handle against, and every axis
    /// the gizmo shows is the model's axis as the pose has turned it there, so dragging along an arrow moves the part
    /// along that arrow on screen.
    /// </summary>
    private void UpdateMove(ScreenProjection projection, SkinnedMesh m, TranslateGizmo gizmo)
    {
        DrawTickedWash(projection, m);
        DrawLockedWash(projection, m);

        var io = ImGui.GetIO();
        var hit = MouseHit(projection, world, m.Triangles, skinTriangle, out bool overUi);
        var origin = ImGui.GetMainViewport().Pos;

        if (movePivot is { } pivot)
        {
            if (gizmo.Active == TranslateGizmo.Handle.None && AnchorVertex(m, pivot) is var anchor and >= 0)
            {
                var skin = poser.SkinAt(m, anchor, pose.Root);
                if (Matrix4x4.Invert(skin, out var unskin)) { moveSkin = skin; moveUnskin = unskin; }
            }

            var toWorld = moveSkin;
            var toModel = moveUnskin;
            gizmo.Update(
                pivot,
                p => projection.WorldToScreen(Vector3.Transform(p, toWorld), out var s) ? s + origin : null,
                s => ScreenProjection.TryScreenRay(s - origin, out var o, out var d)
                    ? (Vector3.Transform(o, toModel), Vector3.TransformNormal(d, toModel))
                    : null,
                io.MousePos, mouseAllowed: !overUi && !io.KeyAlt,
                pressed: ImGui.IsMouseClicked(ImGuiMouseButton.Left), down: ImGui.IsMouseDown(ImGuiMouseButton.Left),
                background: true);
        }

        if (gizmo.Capturing)
        {
            ImGui.SetNextFrameWantCaptureMouse(true);
            Hovering = true;
            return;
        }

        if (hit is not { } h || overUi || io.KeyAlt) return;
        Hovering = true;
        ImGui.SetNextFrameWantCaptureMouse(true);
        DrawHotPart(projection, m, h.Triangle);
        if (ImGui.IsMouseClicked(ImGuiMouseButton.Left)) lockClicked?.Invoke(m.BaseTriangles[h.Triangle * 3]);
    }

    // The anchor found for this pivot, selection and mesh — a scan of every vertex, so not repeated per frame.
    private int anchorVertex = -1;
    private Vector3 anchorPivot;
    private int anchorVersion = int.MinValue;
    private SkinnedMesh? anchorMesh;

    /// <summary>The chosen part's unshaped vertex nearest <paramref name="pivot"/>; -1 when the part has none here.</summary>
    private int AnchorVertex(SkinnedMesh m, Vector3 pivot)
    {
        if (anchorPivot == pivot && anchorVersion == tickedVersion && ReferenceEquals(anchorMesh, m)) return anchorVertex;
        anchorPivot = pivot;
        anchorVersion = tickedVersion;
        anchorMesh = m;
        anchorVertex = FindAnchor(m, pivot);
        return anchorVertex;
    }

    private int FindAnchor(SkinnedMesh m, Vector3 pivot)
    {
        if (partOf == null || partTicked == null) return -1;
        int best = -1;
        float bestD = float.MaxValue;
        for (int v = 0; v < m.VertexCount; v++)
        {
            if (spareBase[v] >= 0) continue;
            int part = partOf(v);
            if (part < 0 || !partTicked(part)) continue;
            float d = Vector3.DistanceSquared(edited[v], pivot);
            if (d < bestD) { bestD = d; best = v; }
        }
        return best;
    }

    /// <summary>Find the edited model on the character and (re)build its posing data when it changes.</summary>
    private bool EnsureTarget(CharacterBase* cb)
    {
        foreach (var modelPtr in cb->ModelsSpan)
        {
            var model = modelPtr.Value;
            if (model == null || model->ModelResourceHandle == null) continue;
            var handle = model->ModelResourceHandle;
            var name = handle->FileName.ToString();
            // While painting, the character draws the brush's preview in place of the mod's file — the same
            // garment, same vertex order, under a different name.
            if (string.IsNullOrEmpty(name)
                || (BodyShapeReader.PathKey(name) != targetKey && !LiveBrushPreview.IsPreviewFile(LiveCharacter.FilePath(name))))
                continue;

            var shapes = EnabledShapes(model, handle);
            var key = targetKey + "|" + string.Join(",", shapes);   // the target, not the file: a new preview is not a new mesh
            if (key != meshKey)
            {
                meshKey = key;
                mesh = ModelSkinReader.Read(targetBytes!, shapes, LiveCharacter.GamePathOf(penumbra, name) ?? name);
                if (mesh != null) Prepare(mesh);
            }
            if (mesh == null) { Problem = Strings.Parts.LiveUnreadable; return false; }
            if (volume!.Positions().Length != mesh.VertexCount * 3) { Problem = Strings.Parts.LiveUnreadable; return false; }
            return true;
        }

        // Worn through a Proteus shell: the game never loads this file, so there is nothing in the draw
        // object to match it against. Pose it on the character's skeleton anyway — the shell carries this
        // geometry verbatim, bone names and all, so the posed copy lands exactly where the shell draws it.
        // No shape keys: a shell declares none, so the surface drawn is this file unshaped.
        if (graftedGamePath != null) return EnsureGrafted();

        Problem = Strings.Parts.LiveNotWorn;
        return false;
    }

    /// <summary>The target as <see cref="EnsureTarget"/> builds it, for a model no drawn file corresponds to.</summary>
    private bool EnsureGrafted()
    {
        var key = targetKey + "|grafted";
        if (key != meshKey)
        {
            meshKey = key;
            mesh = ModelSkinReader.Read(targetBytes!, null, graftedGamePath);
            if (mesh != null) Prepare(mesh);
        }
        if (mesh == null) { Problem = Strings.Parts.LiveUnreadable; return false; }
        if (volume!.Positions().Length != mesh.VertexCount * 3) { Problem = Strings.Parts.LiveUnreadable; return false; }
        return true;
    }

    private static HashSet<string> EnabledShapes(Model* model, ModelResourceHandle* handle)
    {
        var shapes = new HashSet<string>(StringComparer.Ordinal);
        uint mask = model->EnabledShapeKeyIndexMask;
        if (mask != 0)
            foreach (var kv in handle->Shapes)
                if (kv.Item2 is >= 0 and < 32 && (mask & (1u << kv.Item2)) != 0)
                    shapes.Add(kv.Item1.ToString());
        return shapes;
    }

    private void Prepare(SkinnedMesh m)
    {
        skinTriangle = SkinTriangles(m);

        // A shape's spare vertex stands in for a base vertex, so it moves with that vertex's edit.
        spareBase = new int[m.VertexCount];
        Array.Fill(spareBase, -1);
        for (int i = 0; i < m.Triangles.Length; i++)
            if (m.Triangles[i] != m.BaseTriangles[i]) spareBase[m.Triangles[i]] = m.BaseTriangles[i];

        if (edited.Length < m.VertexCount) edited = new Vector3[m.VertexCount];
        if (world.Length < m.VertexCount) world = new Vector3[m.VertexCount];
    }

    private static bool[] SkinTriangles(SkinnedMesh m)
    {
        var skinMaterial = new bool[m.MaterialNames.Length];
        for (int i = 0; i < skinMaterial.Length; i++) skinMaterial[i] = SecondSkinWriter.IsBodySkinMaterial(m.MaterialNames[i]);
        var result = new bool[m.TriangleCount];
        for (int t = 0; t < result.Length; t++)
            result[t] = m.TriangleMaterials[t] < skinMaterial.Length && skinMaterial[m.TriangleMaterials[t]];
        return result;
    }

    /// <summary>The solve's current positions — the model as edited so far — with spares carried along.</summary>
    private void FillEdited(SkinnedMesh m)
    {
        var p = volume!.Positions();
        for (int v = 0; v < m.VertexCount; v++) edited[v] = new Vector3(p[v * 3], p[v * 3 + 1], p[v * 3 + 2]);
        for (int v = 0; v < m.VertexCount; v++)
            if (spareBase[v] is var b and >= 0)
                edited[v] = m.Positions[v] + (edited[b] - m.Positions[b]);
    }

    /// <summary>
    /// The garment under the mouse, or null. Skin still stops the ray — cloth behind an arm is not under the
    /// mouse — but a skin hit is no hit.
    /// </summary>
    private static LiveMeshHit? MouseHit(ScreenProjection projection, Vector3[] posed, int[] triangles, bool[] skin,
                                         out bool overUi)
    {
        overUi = ImGui.IsWindowHovered(ImGuiHoveredFlags.AnyWindow);
        var mouse = ImGui.GetIO().MousePos - ImGui.GetMainViewport().Pos;
        if (!ScreenProjection.TryScreenRay(mouse, out var origin, out var dir)) return null;
        var hit = LiveMeshPicker.Raycast(posed, triangles, origin, dir);
        return hit is { } h && h.Triangle < skin.Length && skin[h.Triangle] ? null : hit;
    }

    /// <summary>
    /// The brush on the character: a ring lying on the surface, and a faint wash over the points it reaches.
    /// Kept dim on purpose — the character is the thing being looked at.
    /// </summary>
    private void DrawBrush(ScreenProjection projection, LiveMeshHit hit, SkinnedMesh m, Matrix4x4 skin)
    {
        var dl = ImGui.GetBackgroundDrawList();
        var origin = ImGui.GetMainViewport().Pos;

        // Ring radius in the world: the model-space radius scaled by the pose at the hit.
        float scale = new Vector3(skin.M11, skin.M12, skin.M13).Length();
        float r = radius * (scale > 1e-6f ? scale : 1f);
        uint ringColour = Painting ? 0xC0FFFFFFu : 0x80FFFFFFu;
        float pixels = DrawRing(projection, hit.World, hit.Normal, r, ringColour);

        // The mirrored ring, fainter: at the garment's vertex nearest the mirrored centre, facing the camera. The
        // mirror is taken in the model's space, so it lands on the matching spot even though the pose is not
        // symmetric.
        if (Cursor is not { } c) return;
        var mc = new Vector3(-c.X, c.Y, c.Z);
        bool mirrored = mirror && MathF.Abs(c.X) >= radius * 0.05f;
        if (mirrored)
        {
            int nearest = -1;
            float best = radius * radius;
            for (int v = 0; v < m.VertexCount; v++)
            {
                if (spareBase[v] >= 0) continue;
                float d2 = Vector3.DistanceSquared(edited[v], mc);
                if (d2 < best) { best = d2; nearest = v; }
            }
            if (nearest >= 0)
            {
                var toCamera = projection.CameraPosition - world[nearest];
                if (toCamera.LengthSquared() > 1e-12f)
                    DrawRing(projection, world[nearest], Vector3.Normalize(toCamera), r, Painting ? 0x80FFFFFFu : 0x50FFFFFFu);
            }
        }

        // Faint dots where the brush reaches, stronger toward the middle — both discs when mirrored, the stronger
        // of the two per point, as the solve weighs them. Left out when the ring is too small to hold them.
        if (pixels < DotsMinRingPixels) return;
        float r2 = radius * radius;
        int drawn = 0;
        for (int v = 0; v < m.VertexCount && drawn < 6000; v++)
        {
            if (spareBase[v] >= 0) continue;
            float d2 = Vector3.DistanceSquared(edited[v], c);
            if (mirrored) d2 = MathF.Min(d2, Vector3.DistanceSquared(edited[v], mc));
            if (d2 >= r2) continue;
            float w = MeshVolumeSolve.Falloff(MathF.Sqrt(d2) / radius);
            if (w <= 0f || !projection.WorldToScreen(world[v], out var s)) continue;
            dl.AddCircleFilled(s + origin, 1.5f, ((uint)(w * 0x38) << 24) | 0x5A5AFFu);
            drawn++;
        }
    }

    /// <summary>
    /// A ring of world radius <paramref name="r"/> lying in the plane across <paramref name="normal"/>, drawn no
    /// smaller than <see cref="MinRingPixels"/> on screen.
    /// </summary>
    /// <returns>The ring's true radius on screen, in pixels, before that floor; 0 when it could not be drawn.</returns>
    private static float DrawRing(ScreenProjection projection, Vector3 centre, Vector3 normal, float r, uint colour)
    {
        var dl = ImGui.GetBackgroundDrawList();
        var origin = ImGui.GetMainViewport().Pos;
        var n = normal;
        var t1 = Vector3.Normalize(Vector3.Cross(n, MathF.Abs(n.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX));
        var t2 = Vector3.Cross(n, t1);

        // How big the ring comes out on screen, measured along the tangent that shows it widest.
        float pixels = 0f;
        if (projection.WorldToScreen(centre, out var sc))
        {
            if (projection.WorldToScreen(centre + t1 * r, out var s1)) pixels = MathF.Max(pixels, Vector2.Distance(sc, s1));
            if (projection.WorldToScreen(centre + t2 * r, out var s2)) pixels = MathF.Max(pixels, Vector2.Distance(sc, s2));
        }
        float drawn = pixels > 0f && pixels < MinRingPixels ? r * MinRingPixels / pixels : r;

        const int Segments = 48;
        Span<Vector2> ring = stackalloc Vector2[Segments];
        int count = 0;
        for (int i = 0; i < Segments; i++)
        {
            float a = i * MathF.Tau / Segments;
            var p = centre + n * 0.002f + (t1 * MathF.Cos(a) + t2 * MathF.Sin(a)) * drawn;
            if (!projection.WorldToScreen(p, out var s)) { count = 0; break; }
            ring[count++] = s + origin;
        }
        for (int i = 0; i < count; i++) dl.AddLine(ring[i], ring[(i + 1) % count], colour, 1.5f);
        return count > 0 ? pixels : 0f;
    }

    /// <summary>
    /// Locked parts as a faint grey over the garment, whenever the brush is armed. A triangle is locked when all
    /// three of its corners are — a seam point welded to a locked part is locked too, but the unlocked cloth
    /// beside it is not greyed for sharing it.
    /// </summary>
    private void DrawLockedWash(ScreenProjection projection, SkinnedMesh m)
    {
        if (volume == null) return;
        var solve = volume;

        // The solve too: a model re-opened builds a new one, whose version count starts again.
        if (lockedTrianglesVersion != solve.LockVersion || !ReferenceEquals(lockedTrianglesMesh, m)
            || !ReferenceEquals(lockedTrianglesSolve, solve))
        {
            lockedTrianglesSolve = solve;
            lockedTriangles.Clear();
            var baseTris = m.BaseTriangles;
            for (int t = 0; t < m.TriangleCount; t++)
            {
                if (t < skinTriangle.Length && skinTriangle[t]) continue;
                int o = t * 3;
                if (solve.IsLocked(baseTris[o]) && solve.IsLocked(baseTris[o + 1]) && solve.IsLocked(baseTris[o + 2]))
                    lockedTriangles.Add(t);
            }
            lockedTrianglesVersion = solve.LockVersion;
            lockedTrianglesMesh = m;
        }
        FillTriangles(projection, m, lockedTriangles, ((uint)(LockedOpacity * 255f) << 24) | 0x00303030u);
    }

    /// <summary>Ticked parts, tinted, under Toggle Parts — rebuilt only when the ticked set or the garment changes.</summary>
    private void DrawTickedWash(ScreenProjection projection, SkinnedMesh m)
    {
        if (partOf == null || partTicked == null) return;
        if (tickedTrianglesVersion != tickedVersion || !ReferenceEquals(tickedTrianglesMesh, m))
        {
            tickedTriangles.Clear();
            var baseTris = m.BaseTriangles;
            for (int t = 0; t < m.TriangleCount; t++)
            {
                if (t < skinTriangle.Length && skinTriangle[t]) continue;
                int part = partOf(baseTris[t * 3]);
                if (part >= 0 && partTicked(part)) tickedTriangles.Add(t);
            }
            tickedTrianglesVersion = tickedVersion;
            tickedTrianglesMesh = m;
        }
        FillTriangles(projection, m, tickedTriangles, ((uint)(TickedOpacity * 255f) << 24) | 0x0040A0FFu);   // ABGR: amber
    }

    /// <summary>The part under the mouse, lit up while Shift is held — what a click would lock or unlock.</summary>
    private void DrawHotPart(ScreenProjection projection, SkinnedMesh m, int triangle)
    {
        if (partOf == null) return;
        var baseTris = m.BaseTriangles;
        int part = partOf(baseTris[triangle * 3]);
        if (part != hotPart || !ReferenceEquals(hotMesh, m))
        {
            hotTriangles.Clear();
            for (int t = 0; t < m.TriangleCount; t++)
            {
                if (t < skinTriangle.Length && skinTriangle[t]) continue;
                if (partOf(baseTris[t * 3]) == part) hotTriangles.Add(t);
            }
            hotPart = part;
            hotMesh = m;
        }
        FillTriangles(projection, m, hotTriangles, 0x40FFE0B0u);   // ABGR: a pale blue
    }

    /// <summary>Fill <paramref name="triangles"/> on the posed garment, thinned past <see cref="MaxWashTriangles"/>.</summary>
    private void FillTriangles(ScreenProjection projection, SkinnedMesh m, List<int> triangles, uint colour)
    {
        if (triangles.Count == 0) return;
        var dl = ImGui.GetBackgroundDrawList();
        var origin = ImGui.GetMainViewport().Pos;
        var tris = m.Triangles;
        int stride = Math.Max(1, triangles.Count / MaxWashTriangles);
        for (int i = 0; i < triangles.Count; i += stride)
        {
            int o = triangles[i] * 3;
            if (!projection.WorldToScreen(world[tris[o]], out var a)) continue;
            if (!projection.WorldToScreen(world[tris[o + 1]], out var b)) continue;
            if (!projection.WorldToScreen(world[tris[o + 2]], out var c)) continue;
            dl.AddTriangleFilled(a + origin, b + origin, c + origin, colour);
        }
    }

    /// <summary>
    /// The painted wind as a translucent red over the garment, stronger where there is more — the whole painted
    /// area, not only what the brush is over. Wind is per vertex; a triangle takes the average of its corners.
    /// </summary>
    private void DrawWindWash(ScreenProjection projection, SkinnedMesh m)
    {
        if (volume == null) return;
        var solve = volume;

        if (windTrianglesVersion != solve.WindVersion || !ReferenceEquals(windTrianglesMesh, m))
        {
            windTriangles.Clear();
            var baseTris = m.BaseTriangles;
            for (int t = 0; t < m.TriangleCount; t++)
            {
                if (t < skinTriangle.Length && skinTriangle[t]) continue;
                int o = t * 3;
                if (solve.WindAt(baseTris[o]) > 0f || solve.WindAt(baseTris[o + 1]) > 0f || solve.WindAt(baseTris[o + 2]) > 0f)
                    windTriangles.Add(t);
            }
            windTrianglesVersion = solve.WindVersion;
            windTrianglesMesh = m;
        }
        if (windTriangles.Count == 0) return;

        var dl = ImGui.GetBackgroundDrawList();
        var origin = ImGui.GetMainViewport().Pos;
        var tris = m.Triangles;
        var bases = m.BaseTriangles;
        int stride = Math.Max(1, windTriangles.Count / MaxWashTriangles);
        for (int i = 0; i < windTriangles.Count; i += stride)
        {
            int o = windTriangles[i] * 3;
            float wind = (solve.WindAt(bases[o]) + solve.WindAt(bases[o + 1]) + solve.WindAt(bases[o + 2])) / 3f;
            uint alpha = (uint)(Math.Clamp(wind, 0f, 1f) * WashOpacity * 255f);
            if (alpha == 0) continue;
            if (!projection.WorldToScreen(world[tris[o]], out var a)) continue;
            if (!projection.WorldToScreen(world[tris[o + 1]], out var b)) continue;
            if (!projection.WorldToScreen(world[tris[o + 2]], out var c)) continue;
            dl.AddTriangleFilled(a + origin, b + origin, c + origin, (alpha << 24) | 0x002828EBu);   // ABGR: red
        }
    }

    // ── pick mode ──────────────────────────────────────────────────────────

    /// <param name="exclude">The garment the brush is on, which the brush answers for; null for none.</param>
    private void UpdatePick(string? exclude)
    {
        var cb = LiveCharacter.Player(objects);
        if (cb == null || modsRoot == null || !pose.Read(cb) || !ScreenProjection.TryCapture(out var projection)) return;
        if (!pbdTried) { pbdTried = true; pbd = LiveCharacter.LoadPbd(penumbra, data, log); }

        LiveMeshHit? best = null;
        string? bestFile = null;
        bool overUi = false;
        foreach (var modelPtr in cb->ModelsSpan)
        {
            var model = modelPtr.Value;
            if (model == null || model->ModelResourceHandle == null) continue;
            var handle = model->ModelResourceHandle;
            var name = handle->FileName.ToString();
            if (string.IsNullOrEmpty(name)) continue;
            var file = LiveCharacter.FilePath(name);
            if (BodyShapeReader.PathKey(file) == exclude) continue;
            if (!HatCompatService.InMods(file, modsRoot, out _, out _)) continue;

            var shapes = EnabledShapes(model, handle);
            var key = BodyShapeReader.PathKey(file) + "|" + string.Join(",", shapes);
            if (!pickMeshes.TryGetValue(key, out var entry))
            {
                var bytes = File.Exists(file) ? File.ReadAllBytes(file) : null;
                var read = bytes != null ? ModelSkinReader.Read(bytes, shapes, LiveCharacter.GamePathOf(penumbra, name) ?? name) : null;
                if (read == null) continue;
                pickMeshes[key] = entry = (read, SkinTriangles(read));
            }

            var m = entry.Mesh;
            if (pickWorld.Length < m.VertexCount) pickWorld = new Vector3[m.VertexCount];
            poser.Pose(m, pose, pbd, pickWorld);
            if (MouseHit(projection, pickWorld, m.Triangles, entry.Skin, out overUi) is { } h
                && (best == null || h.Distance < best.Value.Distance))
            {
                best = h;
                bestFile = file;
            }
        }

        if (best is not { } hit || overUi || ImGui.GetIO().KeyAlt) return;
        Hovering = true;
        ImGui.SetNextFrameWantCaptureMouse(true);

        var dl = ImGui.GetBackgroundDrawList();
        var origin = ImGui.GetMainViewport().Pos;
        if (projection.WorldToScreen(hit.World, out var s))
        {
            dl.AddCircle(s + origin, 10f, 0xC0FFFFFFu, 24, 1.5f);
            dl.AddText(s + origin + new Vector2(14, -8), 0xE0FFFFFFu, Path.GetFileName(bestFile!));
        }

        if (ImGui.IsMouseClicked(ImGuiMouseButton.Left)) onPicked?.Invoke(bestFile!);
    }
}
