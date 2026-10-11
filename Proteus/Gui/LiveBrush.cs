using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

    /// <summary>
    /// The ray under the mouse in the model's space, wherever the mouse is — on the model or off it — while a stroke is
    /// under way; null between strokes or where it cannot be had. What the grab brush drags along.
    /// </summary>
    (Vector3 Origin, Vector3 Dir)? MouseRay { get; }
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

    /// <summary>The vertices <see cref="spareBase"/> maps, listed once per mesh so a per-frame pass visits only them.</summary>
    private readonly List<int> spareVertices = [];
    private Vector3[] edited = [];
    private Vector3[] world = [];

    // ── pick mode: every worn model from a mod ──
    private readonly Dictionary<string, (SkinnedMesh Mesh, bool[] Skin)> pickMeshes = new(StringComparer.OrdinalIgnoreCase);
    private Vector3[] pickWorld = [];

    public Vector3? Cursor { get; private set; }
    public bool Painting { get; private set; }
    public bool StrokeEnded { get; private set; }
    public Vector3 ToViewer { get; private set; } = Vector3.UnitZ;
    public (Vector3 Origin, Vector3 Dir)? MouseRay { get; private set; }

    /// <summary>
    /// The world-to-model transform at the point a stroke began, held for the stroke's life, the way the Move gizmo
    /// holds its anchor: an idle sway must not shift the ray under a mouse that is holding still. Null between strokes.
    /// </summary>
    private Matrix4x4? strokeUnskin;

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
    /// <param name="scaleDrag">The Scale tool: as <paramref name="moveGizmo"/>, but the handle is the chosen part
    /// itself — pressed and dragged sideways. Null for every other tool.</param>
    /// <param name="rotateGizmo">The Rotate tool: as <paramref name="moveGizmo"/>, with rings instead of arrows. Null for
    /// every other tool. At most one of the three handles is ever set.</param>
    /// <param name="partLocked">Greys the parts this names (by <paramref name="partOf"/>'s id) instead of the solve's
    /// locks — Body size, whose holds are a set of their own. Skin is greyed too, since a hold can take skin. Null to
    /// grey the solve's locks.</param>
    /// <param name="lockedVersion">With <paramref name="partLocked"/>: changes whenever its set does.</param>
    /// <param name="polygons">The Move tools in polygon mode: the selected polygons, which are tinted, anchor the gizmo and
    /// are the Scale tool's handle, in place of <paramref name="partTicked"/>'s part. Null in part mode.</param>
    /// <param name="polygonClicked">In polygon mode: a click on the garment, with the polygon it landed on — in place of
    /// <paramref name="lockClicked"/>.</param>
    /// <param name="polygonPickable">In polygon mode: which polygons a click can take. The rest — skin, locked parts —
    /// do not stop the ray, so cloth clipped behind them can still be picked.</param>
    /// <param name="moveFalloff">The Move tools with their falloff on: how much of a move each vertex takes (ModelPartReader
    /// order), drawn as the brush's rainbow in place of the chosen part's tint. Null for the plain tint.</param>
    /// <param name="grabBrush">The brush is the grab: its rainbow shows the grab's own reach and falloff.</param>
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
                           string? graftedGamePath = null, PartScaleDrag? scaleDrag = null,
                           RotateGizmo? rotateGizmo = null,
                           Func<int, bool>? partLocked = null, int lockedVersion = 0,
                           IReadOnlySet<PolygonSelection.Key>? polygons = null,
                           Action<PolygonSelection.Key>? polygonClicked = null,
                           Func<PolygonSelection.Key, bool>? polygonPickable = null,
                           float[]? moveFalloff = null, bool grabBrush = false)
    {
        this.moveFalloff = moveFalloff;
        this.grabBrush = grabBrush;
        this.polygons = polygons;
        this.polygonClicked = polygonClicked;
        this.polygonPickable = polygonPickable;
        this.partLocked = partLocked;
        this.lockedVersion = lockedVersion;
        this.moveGizmo = moveGizmo;
        this.scaleDrag = scaleDrag;
        this.rotateGizmo = rotateGizmo;
        this.movePivot = movePivot;
        this.graftedGamePath = graftedGamePath;
        brushArmedFrame = ImGui.GetFrameCount();
        targetKey = BodyShapeReader.PathKey(modelFile);
        targetBytes = modelBytes;
        // A new solve on the same file — reopened after a save — has its own authored normals to judge fronts by.
        if (!ReferenceEquals(volume, solve)) Array.Clear(triangleFront);
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

    /// <summary>See <see cref="ArmBrush"/>: the Move tools' falloff by vertex, or null.</summary>
    private float[]? moveFalloff;

    /// <summary>See <see cref="ArmBrush"/>: the brush is the grab.</summary>
    private bool grabBrush;

    /// <summary>See <see cref="ArmBrush"/>: the game path of a model worn through a Proteus shell, or null.</summary>
    private string? graftedGamePath;
    private Func<int, int>? partOf;
    private Action<int>? lockClicked;

    // ── the Move tools in polygon mode ──
    private IReadOnlySet<PolygonSelection.Key>? polygons;
    private Action<PolygonSelection.Key>? polygonClicked;
    private Func<PolygonSelection.Key, bool>? polygonPickable;

    /// <summary>The polygon live triangle <paramref name="t"/> is, by its corners.</summary>
    private static PolygonSelection.Key PolygonOf(SkinnedMesh m, int t)
        => PolygonSelection.Key.Of(m.BaseTriangles[t * 3], m.BaseTriangles[t * 3 + 1], m.BaseTriangles[t * 3 + 2]);
    private Func<int, bool>? partLocked;
    private int lockedVersion;

    // ── the Move, Rotate and Scale tools ──
    private TranslateGizmo? moveGizmo;
    private PartScaleDrag? scaleDrag;
    private RotateGizmo? rotateGizmo;
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

    /// <summary>Whether <c>lockedTriangles</c> holds Body size's holds rather than the solve's locks.</summary>
    private bool lockedTrianglesHeld;

    /// <summary>Opacity of the grey over a locked part: enough to tell it apart, faint like the rest of the overlay.</summary>
    private const float LockedOpacity = 0.3f;

    // ── the part under the mouse while Shift is held ──
    private readonly List<int> hotTriangles = [];
    private int hotPart = int.MinValue;
    private SkinnedMesh? hotMesh;

    /// <summary>Smallest the brush ring is drawn, in pixels, so a millimetre brush on a distant character still shows.</summary>
    private const float MinRingPixels = 4f;

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

    /// <summary>
    /// Keep pick mode live for this frame: the next click on a worn garment names its file — a mod's, or for the
    /// game's own gear its game path, which the Studio copies into a mod of its own to edit.
    /// </summary>
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
        MouseRay = null;
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
            strokeUnskin = null;
            moveGizmo?.Release();
            moveGizmo = null;
            scaleDrag?.Release();
            scaleDrag = null;
            rotateGizmo?.Release();
            rotateGizmo = null;
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
            strokeUnskin = null;
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

        if (moveGizmo != null || scaleDrag != null || rotateGizmo != null)
        {
            UpdateMove(projection, m);
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

        // The mouse's ray in the model, held to the pose the stroke began under — also on the frame the stroke ends,
        // so the mouse's last movement before the release still counts.
        // A press always takes the pose afresh, whatever an earlier stroke left behind.
        if (Painting && ImGui.IsMouseClicked(ImGuiMouseButton.Left)) strokeUnskin = null;
        if (Painting && strokeUnskin == null && hit is { } first
            && Matrix4x4.Invert(poser.SkinAt(m, m.Triangles[first.Triangle * 3], pose.Root), out var held))
            strokeUnskin = held;
        if ((Painting || StrokeEnded) && strokeUnskin is { } unskinRay
            && ScreenProjection.TryScreenRay(io.MousePos - ImGui.GetMainViewport().Pos, out var rayFrom, out var rayDir))
            MouseRay = (Vector3.Transform(rayFrom, unskinRay), Vector3.TransformNormal(rayDir, unskinRay));
        if (!Painting) strokeUnskin = null;

        // A grab held: the cloth it took hold of, wherever the mouse has gone — on the garment, off it, or over other cloth.
        // On the release frame too: the grab is ended a moment later, and the brush disc stays hidden until it is, so
        // dropping the wash here would blink both off for a frame.
        if ((Painting || StrokeEnded) && volume.GrabFalloff() is { } grabbed) DrawMoveFalloffWash(projection, m, grabbed);

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
        if (scaleDrag is { Active: true }) scaleDrag.Release();
        if (rotateGizmo is { Active: not RotateGizmo.Handle.None }) rotateGizmo.Release();
        // The pose the stroke's ray was held to goes with it, or the next stroke would see through this one's pose.
        strokeUnskin = null;
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
    private void UpdateMove(ScreenProjection projection, SkinnedMesh m)
    {
        var io = ImGui.GetIO();
        // Picking polygons looks THROUGH whatever cannot be picked — the skin a clip pokes out behind, a locked part —
        // rather than being stopped by it.
        var hit = polygonClicked != null && polygonPickable is { } pickable
            ? MouseHit(projection, world, m.Triangles,
                       t => (t >= skinTriangle.Length || !skinTriangle[t]) && pickable(PolygonOf(m, t)), out bool overUi)
            : MouseHit(projection, world, m.Triangles, skinTriangle, out overUi);

        if (moveFalloff is { } falloff) DrawMoveFalloffWash(projection, m, falloff);
        else DrawTickedWash(projection, m);
        DrawLockedWash(projection, m);
        var origin = ImGui.GetMainViewport().Pos;
        bool dragging = moveGizmo is { Active: not TranslateGizmo.Handle.None } || scaleDrag is { Active: true }
                        || rotateGizmo is { Active: not RotateGizmo.Handle.None };

        if (movePivot is { } pivot)
        {
            if (!dragging && AnchorVertex(m, pivot) is var anchor and >= 0)
            {
                var skin = poser.SkinAt(m, anchor, pose.Root);
                if (Matrix4x4.Invert(skin, out var unskin)) { moveSkin = skin; moveUnskin = unskin; }
            }

            var toWorld = moveSkin;
            var toModel = moveUnskin;
            Func<Vector3, Vector2?> toScreen =
                p => projection.WorldToScreen(Vector3.Transform(p, toWorld), out var s) ? s + origin : null;
            Func<Vector2, (Vector3, Vector3)?> screenRay =
                s => ScreenProjection.TryScreenRay(s - origin, out var o, out var d)
                    ? (Vector3.Transform(o, toModel), Vector3.TransformNormal(d, toModel))
                    : null;
            bool pressed = ImGui.IsMouseClicked(ImGuiMouseButton.Left), down = ImGui.IsMouseDown(ImGuiMouseButton.Left);
            bool allowed = !overUi && !io.KeyAlt;

            moveGizmo?.Update(pivot, toScreen, screenRay, io.MousePos, mouseAllowed: allowed,
                              pressed: pressed, down: down, background: true);
            rotateGizmo?.Update(pivot, toScreen, screenRay, io.MousePos, mouseAllowed: allowed,
                                pressed: pressed, down: down, background: true);

            // Scale: the handle is the chosen part itself.
            bool overPart = hit is { } over
                            && (polygons != null
                                    ? polygons.Contains(PolygonOf(m, over.Triangle))
                                    : partOf != null && partTicked != null
                                      && partOf(m.BaseTriangles[over.Triangle * 3]) is var part and >= 0 && partTicked(part));
            scaleDrag?.Update(pivot, toScreen, io.MousePos, overPart, mouseAllowed: allowed,
                              pressed: pressed, down: down, background: true);
        }

        if (moveGizmo is { Capturing: true } || scaleDrag is { Capturing: true } || rotateGizmo is { Capturing: true })
        {
            ImGui.SetNextFrameWantCaptureMouse(true);
            Hovering = true;
            return;
        }

        if (hit is not { } h || overUi || io.KeyAlt) return;
        Hovering = true;
        ImGui.SetNextFrameWantCaptureMouse(true);
        if (polygonClicked != null)
        {
            FillTriangles(projection, m, [h.Triangle], 0x60FFE0B0u);   // ABGR: a pale blue, the one polygon a click takes
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left)) polygonClicked(PolygonOf(m, h.Triangle));
            return;
        }
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
        HashSet<int>? corners = polygons != null ? [.. PolygonSelection.CornersOf(polygons)] : null;
        if (corners == null && (partOf == null || partTicked == null)) return -1;
        int best = -1;
        float bestD = float.MaxValue;
        for (int v = 0; v < m.VertexCount; v++)
        {
            if (spareBase[v] >= 0) continue;
            if (corners != null)
            {
                if (!corners.Contains(v)) continue;
            }
            else if (partOf!(v) is var part && (part < 0 || !partTicked!(part))) continue;
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
                || (!SameFile(BodyShapeReader.PathKey(name), targetKey)
                    && !LiveBrushPreview.IsPreviewFile(LiveCharacter.FilePath(name))))
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

    /// <summary>
    /// Whether the game's name for a drawn file and the path on disk are one file. Exact first; failing that, with every
    /// character outside ASCII dropped from both. The game hands a path back in its own encoding, so a folder named with
    /// "—" or "·" (body refits saved before their folders were kept to ASCII) comes back as other characters entirely,
    /// and an exact comparison never matches a file the character is plainly wearing.
    /// </summary>
    internal static bool SameFile(string? drawn, string? target)
    {
        if (drawn == null || target == null) return false;
        if (drawn == target) return true;
        static string Ascii(string s) => string.Concat(s.Where(c => c is >= ' ' and <= '~'));
        return Ascii(drawn) == Ascii(target);
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
        Array.Clear(triangleFront);
        BuildVertexTriangles(m);

        // A shape's spare vertex stands in for a base vertex, so it moves with that vertex's edit.
        spareBase = new int[m.VertexCount];
        Array.Fill(spareBase, -1);
        for (int i = 0; i < m.Triangles.Length; i++)
            if (m.Triangles[i] != m.BaseTriangles[i]) spareBase[m.Triangles[i]] = m.BaseTriangles[i];
        spareVertices.Clear();
        for (int v = 0; v < m.VertexCount; v++)
            if (spareBase[v] >= 0) spareVertices.Add(v);

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

    /// <summary>The nearest triangle under the mouse that <paramref name="accept"/> takes; the rest are see-through.</summary>
    private static LiveMeshHit? MouseHit(ScreenProjection projection, Vector3[] posed, int[] triangles,
                                         Func<int, bool> accept, out bool overUi)
    {
        overUi = ImGui.IsWindowHovered(ImGuiHoveredFlags.AnyWindow);
        var mouse = ImGui.GetIO().MousePos - ImGui.GetMainViewport().Pos;
        if (!ScreenProjection.TryScreenRay(mouse, out var origin, out var dir)) return null;
        return LiveMeshPicker.Raycast(posed, triangles, origin, dir, accept);
    }

    /// <summary>
    /// The brush on the character: a ring lying on the surface, over a translucent rainbow across the cloth it reaches
    /// — red where it moves the surface most, through to violet at the rim (<see cref="BrushRainbow"/>).
    /// </summary>
    private void DrawBrush(ScreenProjection projection, LiveMeshHit hit, SkinnedMesh m, Matrix4x4 skin)
    {
        // A grab held is drawn as what it holds (see UpdateBrush), not as a ring around whatever is under the mouse.
        if (Cursor is not { } c || volume is not { Grabbing: false } solve) return;
        var mc = new Vector3(-c.X, c.Y, c.Z);
        bool mirrored = mirror && MathF.Abs(c.X) >= solve.MirrorMinX;   // exactly when the brush itself mirrors

        // Under the rings, so they stay crisp on top of it.
        DrawFalloffWash(projection, m, c, mc, mirrored);

        // Ring radius in the world: the model-space radius scaled by the pose at the hit.
        float scale = new Vector3(skin.M11, skin.M12, skin.M13).Length();
        float r = radius * (scale > 1e-6f ? scale : 1f);
        uint ringColour = Painting ? 0xC0FFFFFFu : 0x80FFFFFFu;
        DrawRing(projection, hit.World, hit.Normal, r, ringColour);

        // The mirrored ring, fainter: at the garment's vertex nearest the mirrored centre, facing the camera. The
        // mirror is taken in the model's space, so it lands on the matching spot even though the pose is not
        // symmetric.
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
    }

    /// <summary>Opacity of the falloff rainbow at the middle of the brush: plain to read, the cloth still showing through.</summary>
    private const float FalloffWashOpacity = 0.45f;

    /// <summary>Each vertex's falloff weight this frame; reused, so a frame makes no garbage.</summary>
    private float[] falloffAt = [];

    /// <summary>The brush's weight by distance, sampled once per brush — see <see cref="BrushWeightTable"/>.</summary>
    private readonly BrushWeightTable brushWeights = new();

    /// <summary>
    /// Per triangle of the current mesh: +1 when its winding's face points the way its corner normals do, −1 when the
    /// other way, 2 where the normals say nothing (a card whose faces cancel), 0 not yet worked out. A property of how
    /// the triangle was authored — its winding against its normals — so it is worked out once, from the authored shape,
    /// and no drag can turn it over.
    /// </summary>
    private sbyte[] triangleFront = [];

    /// <summary>Which side of triangle <paramref name="t"/> is its front, as ±1; 0 when it has none to speak of.</summary>
    private int FrontOf(SkinnedMesh m, MeshVolumeSolve solve, int t)
    {
        if (triangleFront.Length < m.TriangleCount) triangleFront = new sbyte[m.TriangleCount];
        if (triangleFront[t] == 0)
        {
            var bases = m.BaseTriangles;
            int o = t * 3, a = bases[o], b = bases[o + 1], c = bases[o + 2];
            // Both from the solve's own file, as authored — positions and normals — so neither an edit since nor a
            // reopened, re-saved model can pair one with the other turned over.
            var na = solve.AuthoredNormalAt(a);
            var nb = solve.AuthoredNormalAt(b);
            var nc = solve.AuthoredNormalAt(c);
            var normals = new Vector3(na.X + nb.X + nc.X, na.Y + nb.Y + nc.Y, na.Z + nb.Z + nc.Z);
            Vector3 P(int v) { var q = solve.AuthoredPositionAt(v); return new Vector3(q.X, q.Y, q.Z); }
            float side = Vector3.Dot(Vector3.Cross(P(b) - P(a), P(c) - P(a)), normals);
            triangleFront[t] = side > 0f ? (sbyte)1 : side < 0f ? (sbyte)-1 : (sbyte)2;
        }
        return triangleFront[t] == 2 ? 0 : triangleFront[t];
    }

    // Per vertex, for the wash this frame: where it lands on screen, once projected. Valid where its stamp is this
    // frame's, so nothing is cleared per frame. Reused.
    private Vector2[] washScreen = [];
    private bool[] washOnScreen = [];
    private int[] washVertexStamp = [];
    private int[] washTriangleStamp = [];
    private int washStamp;

    /// <summary>The vertices the falloff reaches this frame — where the wash looks for triangles.</summary>
    private readonly List<int> washReached = [];
    private readonly List<int> washTriangles = [];

    // Which triangles each vertex is a corner of, as offsets into one array; built once per mesh.
    private int[] vertexTriangleStart = [];
    private int[] vertexTriangles = [];

    private void BuildVertexTriangles(SkinnedMesh m)
    {
        var tris = m.Triangles;
        var start = new int[m.VertexCount + 1];
        foreach (int v in tris) start[v + 1]++;
        for (int v = 0; v < m.VertexCount; v++) start[v + 1] += start[v];
        var fill = (int[])start.Clone();
        var list = new int[tris.Length];
        for (int i = 0; i < tris.Length; i++) list[fill[tris[i]]++] = i / 3;
        vertexTriangleStart = start;
        vertexTriangles = list;
    }

    /// <summary>
    /// The rainbow over the cloth the brush reaches — both discs when mirrored, the stronger of the two per point, as
    /// the solve weighs them. A shape key's spare vertex takes the weight of the vertex it stands in for, which is the
    /// one the solve moves.
    /// </summary>
    private void DrawFalloffWash(ScreenProjection projection, SkinnedMesh m, Vector3 c, Vector3 mc, bool mirrored)
    {
        if (radius <= 0f) return;
        if (falloffAt.Length < m.VertexCount) falloffAt = new float[m.VertexCount];

        brushWeights.Ensure(radius, grabBrush);
        washReached.Clear();
        // A pass over every vertex, deliberately not a spatial index: this same frame has already copied and posed
        // every vertex of the garment (FillEdited, LiveMeshPoser.Pose), so one more distance each does not change what
        // the frame costs, and an index would have to be rebuilt on every frame the cloth is being edited.
        for (int v = 0; v < m.VertexCount; v++)
        {
            if (spareBase[v] >= 0) continue;
            float d2 = Vector3.DistanceSquared(edited[v], c);
            float w;
            if (!mirrored) w = brushWeights.At(d2);
            else
            {
                float m2 = Vector3.DistanceSquared(edited[v], mc);
                // The painting brushes take the stronger disc; the grab blends its two sides as it will drag them.
                if (!grabBrush) w = brushWeights.At(MathF.Min(d2, m2));
                else
                {
                    float t = MeshVolumeSolve.GrabSide(edited[v].X, c.X, radius, volume!.MirrorMinX);
                    w = t * brushWeights.At(d2) + (1f - t) * brushWeights.At(m2);
                }
            }
            // Locked cloth stays clear, as in the model view: no brush moves it.
            if (w > 0f && volume!.IsLocked(v)) w = 0f;
            falloffAt[v] = w;
            if (w > 0f) washReached.Add(v);
        }
        if (washReached.Count == 0) return;
        foreach (int v in spareVertices)
            if ((falloffAt[v] = falloffAt[spareBase[v]]) > 0f) washReached.Add(v);
        DrawRainbowWash(projection, m);
    }

    /// <summary>
    /// The Move tools' falloff as the same rainbow: red on the chosen part, fading to violet where the cloth it carries
    /// stops following. A shape key's spare vertex takes the weight of the vertex it stands in for.
    /// </summary>
    private void DrawMoveFalloffWash(ScreenProjection projection, SkinnedMesh m, float[] falloff)
    {
        if (falloffAt.Length < m.VertexCount) falloffAt = new float[m.VertexCount];
        washReached.Clear();
        for (int v = 0; v < m.VertexCount; v++)
        {
            int at = spareBase[v] >= 0 ? spareBase[v] : v;
            float w = at < falloff.Length ? falloff[at] : 0f;
            falloffAt[v] = w;
            if (w > 0f) washReached.Add(v);
        }
        if (washReached.Count > 0) DrawRainbowWash(projection, m);
    }

    /// <summary>
    /// <see cref="falloffAt"/> as a translucent rainbow over the garment, coloured per corner so it blends smoothly
    /// across each triangle. Only the triangles at a vertex in <see cref="washReached"/> are looked at. Skin is left
    /// clear: nothing that draws this moves it.
    /// <para/>
    /// So is cloth turned away from the camera — the back of a top, behind the body — which would otherwise show
    /// through the front. Judged per triangle by the face it presents NOW, posed, so a flap a drag is turning shows
    /// the side it is turning to; which of its sides counts as the front is <see cref="FrontOf"/>.
    /// </summary>
    private void DrawRainbowWash(ScreenProjection projection, SkinnedMesh m)
    {
        if (volume == null) return;
        var solve = volume;
        if (washScreen.Length < m.VertexCount)
        {
            washScreen = new Vector2[m.VertexCount];
            washOnScreen = new bool[m.VertexCount];
            washVertexStamp = new int[m.VertexCount];
        }
        if (washTriangleStamp.Length < m.TriangleCount) washTriangleStamp = new int[m.TriangleCount];
        washStamp++;

        var camera = projection.CameraPosition;
        var tris = m.Triangles;
        washTriangles.Clear();
        foreach (int v in washReached)
            for (int k = vertexTriangleStart[v]; k < vertexTriangleStart[v + 1]; k++)
            {
                int t = vertexTriangles[k];
                if (washTriangleStamp[t] == washStamp) continue;   // already seen from another corner
                washTriangleStamp[t] = washStamp;
                if (t < skinTriangle.Length && skinTriangle[t]) continue;
                int o = t * 3;
                int a = tris[o], b = tris[o + 1], c = tris[o + 2];
                if (TurnedAway(t, a, b, c)) continue;
                if (!OnScreen(a) || !OnScreen(b) || !OnScreen(c)) continue;
                washTriangles.Add(t);
            }
        if (washTriangles.Count == 0) return;

        var dl = ImGui.GetBackgroundDrawList();
        var origin = ImGui.GetMainViewport().Pos;
        var uv = ImGui.GetFontTexUvWhitePixel();
        // Thinned past the same budget as the other washes, so a dense garment under a big brush stays smooth.
        int stride = Math.Max(1, washTriangles.Count / MaxWashTriangles);
        for (int i = 0; i < washTriangles.Count; i += stride)
        {
            int o = washTriangles[i] * 3;
            dl.PrimReserve(3, 3);
            for (int k = 0; k < 3; k++)
            {
                int v = tris[o + k];
                dl.PrimVtx(washScreen[v] + origin, uv, WashColour(falloffAt[v]));
            }
        }

        // Each vertex projected once, however many triangles it is a corner of.
        bool OnScreen(int v)
        {
            if (washVertexStamp[v] != washStamp)
            {
                washVertexStamp[v] = washStamp;
                washOnScreen[v] = projection.WorldToScreen(world[v], out washScreen[v]);
            }
            return washOnScreen[v];
        }

        // The posed face turned from the camera — see FrontOf for which of its sides counts as the front.
        bool TurnedAway(int t, int a, int b, int c)
        {
            int front = FrontOf(m, solve, t);
            if (front == 0) return false;
            var posed = Vector3.Cross(world[b] - world[a], world[c] - world[a]);
            return Vector3.Dot(posed, camera - world[a]) * front < 0f;
        }

        static uint WashColour(float w) => BrushRainbow.Abgr(w, FalloffWashOpacity * BrushRainbow.Fade(w));
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

        if (partLocked != null && partOf != null)
        {
            DrawHeldWash(projection, m, partLocked, partOf);
            return;
        }

        // The solve too: a model re-opened builds a new one, whose version count starts again.
        if (lockedTrianglesVersion != solve.LockVersion || !ReferenceEquals(lockedTrianglesMesh, m)
            || !ReferenceEquals(lockedTrianglesSolve, solve) || lockedTrianglesHeld)
        {
            lockedTrianglesHeld = false;
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

    /// <summary>
    /// Body size's holds as the same grey, by part rather than by the solve's per-vertex locks. Shares the locked
    /// wash's cache, flagged so switching between the two rebuilds it.
    /// </summary>
    private void DrawHeldWash(ScreenProjection projection, SkinnedMesh m, Func<int, bool> held, Func<int, int> part)
    {
        if (!lockedTrianglesHeld || lockedTrianglesVersion != lockedVersion || !ReferenceEquals(lockedTrianglesMesh, m))
        {
            lockedTrianglesHeld = true;
            lockedTriangles.Clear();
            var baseTris = m.BaseTriangles;
            for (int t = 0; t < m.TriangleCount; t++)
            {
                int p = part(baseTris[t * 3]);
                if (p >= 0 && held(p)) lockedTriangles.Add(t);
            }
            lockedTrianglesVersion = lockedVersion;
            lockedTrianglesMesh = m;
        }
        FillTriangles(projection, m, lockedTriangles, ((uint)(LockedOpacity * 255f) << 24) | 0x00303030u);
    }

    /// <summary>Ticked parts, tinted, under Toggle Parts — rebuilt only when the ticked set or the garment changes.</summary>
    private void DrawTickedWash(ScreenProjection projection, SkinnedMesh m)
    {
        if (polygons == null && (partOf == null || partTicked == null)) return;
        if (tickedTrianglesVersion != tickedVersion || !ReferenceEquals(tickedTrianglesMesh, m))
        {
            tickedTriangles.Clear();
            var baseTris = m.BaseTriangles;
            for (int t = 0; t < m.TriangleCount; t++)
            {
                if (t < skinTriangle.Length && skinTriangle[t]) continue;
                if (polygons != null)
                {
                    if (polygons.Contains(PolygonOf(m, t))) tickedTriangles.Add(t);
                    continue;
                }
                int part = partOf!(baseTris[t * 3]);
                if (part >= 0 && partTicked!(part)) tickedTriangles.Add(t);
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

            // A model no mod redirects is the game's own, and then its resource name IS the game path — which is
            // both how it is recognised and the only handle anything downstream has on it.
            bool inMods = HatCompatService.InMods(file, modsRoot, out _, out _);
            bool gameGear = !inMods
                         && (file.StartsWith("chara/equipment/", StringComparison.OrdinalIgnoreCase)
                             || file.StartsWith("chara/accessory/", StringComparison.OrdinalIgnoreCase));
            if (!inMods && !gameGear) continue;

            var shapes = EnabledShapes(model, handle);
            var key = BodyShapeReader.PathKey(file) + "|" + string.Join(",", shapes);
            if (!pickMeshes.TryGetValue(key, out var entry))
            {
                var bytes = File.Exists(file) ? File.ReadAllBytes(file)
                          : gameGear              ? data.GetFile(file)?.Data
                          : null;
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
