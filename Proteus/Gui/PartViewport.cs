using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;
using Proteus.Services;

namespace Proteus.Gui;

/// <summary>
/// The model, drawn so the user can click the part they mean. Software-rendered: the rasteriser writes a part id
/// per pixel, so a click is an array lookup that cannot disagree with what is on screen.
/// </summary>
public sealed class PartViewport : IDisposable, IBrushSurface
{
    /// <summary>
    /// The shape the viewport starts at. The panel sizes the image from it rather than from the current buffer,
    /// which would feed the last resize back into the next one.
    /// </summary>
    public const float DefaultAspect = (float)DefaultWidth / DefaultHeight;

    private const int DefaultWidth = 460, DefaultHeight = 560;

    /// <summary>
    /// Ceiling on the render buffer, as an AREA. Rasterising is O(pixels) on the UI thread, so this is a time budget;
    /// above it the viewport upscales.
    /// </summary>
    private const int MaxPixels = 900_000;

    /// <summary>Buffer granularity. Snapping UP means the image is always downsampled, never blurred up.</summary>
    private const int Snap = 64;

    /// <summary>
    /// How much larger than the image the buffer is rendered. The downsample is the only antialiasing this rasteriser has.
    /// </summary>
    private const float Supersample = 1.5f;

    /// <summary>Nothing was drawn at this pixel.</summary>
    private const int Empty = -1;

    private readonly ITextureProvider textures;
    private readonly IPluginLog log;

    /// <summary>Resolution of the render buffers; a field so an enlarged window renders at its real size.</summary>
    private int bufW = DefaultWidth, bufH = DefaultHeight;

    // Per pixel: which pickable part is in front, and how lit its surface is. Split so a change of selection
    // recolours without touching geometry.
    private int[] id = [];
    private byte[] shade = [];
    private float[] depth = [];
    private byte[] rgba = [];

    /// <summary>
    /// The point on the model's surface each pixel is looking at, in object space (the space of
    /// <see cref="ModelParts.Positions"/>). Meaningless where <see cref="id"/> is <see cref="Empty"/>.
    /// Per pixel so the brush falloff is one pass in <see cref="Recolourize"/>, with no projection.
    /// </summary>
    private Vector3[] hit = [];

    private IDalamudTextureWrap? wrap;

    // Camera, in the only terms an orbit needs: where it is on the sphere around the model, and how far out.
    private float yaw = MathF.PI, pitch = 0.15f, zoom = 1f;
    private Vector2 pan;

    private string? renderedKey;
    private bool geometryDirty = true, coloursDirty = true;

    /// <summary>
    /// What a click can land on: an island where a submesh has them, the submesh where it does not, never both.
    /// </summary>
    private List<ModelPart> pickable = [];

    /// <summary>
    /// For each pickable part, the label of the SUBMESH it belongs to, null when it is that submesh itself; a submesh
    /// ticked in the list must colour its islands.
    /// </summary>
    private List<string?> parentOf = [];

    /// <summary>
    /// For each pickable part, whether the brush may move it: false for skin, which is never tinted. Worked out once
    /// per model in <see cref="Show"/>, not per colouring pass.
    /// </summary>
    private bool[] brushable = [];

    private Vector2 dragFrom;
    private bool dragging, dragMoved;

    public PartViewport(ITextureProvider textures, IPluginLog log)
    {
        this.textures = textures;
        this.log = log;
        Allocate();
    }

    private void Allocate()
    {
        id = new int[bufW * bufH];
        shade = new byte[bufW * bufH];
        depth = new float[bufW * bufH];
        rgba = new byte[bufW * bufH * 4];
        hit = new Vector3[bufW * bufH];
        scalar = new float[bufW * bufH];
    }

    /// <summary>Parts the user has ticked, by label — drawn in the accent colour.</summary>
    public IReadOnlySet<string> Selected { get; set; } = new HashSet<string>();

    /// <summary>
    /// Parts locked against the brush, by label (a submesh's label covers its islands): drawn dark and left out of
    /// the brush blob and wind wash. Call <see cref="Recolour"/> after it changes.
    /// </summary>
    public IReadOnlySet<string> Locked { get; set; } = new HashSet<string>();

    /// <summary>The brush also paints its mirror image across X = 0: tint both discs.</summary>
    public bool MirrorBrush { get; set; }

    /// <summary>The part under the cursor, or null. Set by <see cref="Draw"/>, and also settable from the
    /// list beside it so hovering a row lights the model up.</summary>
    public string? Hovered { get; set; }

    /// <summary>
    /// Whether the cursor is over the model image this frame, as opposed to <see cref="Hovered"/> being set by the list.
    /// </summary>
    public bool PointerOverModel { get; private set; }

    /// <summary>What a drag on the model means.</summary>
    public enum ViewportMode
    {
        /// <summary>Drag turns the model; a click picks a part.</summary>
        Navigate,

        /// <summary>A drag that STARTS ON THE MODEL paints; one that starts on the background still turns it.</summary>
        Brush,

        /// <summary>
        /// Navigate, except that a press on the Move gizmo drags the gizmo instead — see <see cref="GizmoCapture"/>.
        /// A click still picks the part to move, and a drag anywhere else still turns the model.
        /// </summary>
        Move,
    }

    public ViewportMode Mode { get; set; } = ViewportMode.Navigate;

    /// <summary>
    /// In <see cref="ViewportMode.Move"/>: whether the gizmo has the mouse, asked when a press lands. True gives the
    /// whole drag to the gizmo — no orbit, no pan, no pick on release.
    /// </summary>
    public Func<bool>? GizmoCapture { get; set; }

    /// <summary>The viewport's surface was pressed this frame (its own button, so a press elsewhere is not it).</summary>
    public bool Pressed { get; private set; }

    /// <summary>The press that began on the viewport's surface is still held, wherever the mouse has gone since.</summary>
    public bool Held { get; private set; }

    // The projection the image on screen was rasterised with, for the gizmo: model space to screen and back.
    private Matrix4x4 viewProj;
    private Vector2 rasterPan;
    private bool haveProjection;
    private Vector2 imageOrigin, imageSize;

    /// <summary>A model-space point on the image as it is drawn, in absolute screen pixels; null behind the eye or
    /// before anything has been drawn.</summary>
    public Vector2? ModelToScreen(Vector3 p)
    {
        if (!haveProjection || imageSize.X <= 0f || imageSize.Y <= 0f) return null;
        var clip = Vector4.Transform(new Vector4(p, 1f), viewProj);
        if (clip.W <= 1e-6f) return null;
        float nx = clip.X / clip.W, ny = clip.Y / clip.W;
        // The same mapping Rasterize uses, as a fraction of the buffer and so of the image.
        var f = new Vector2((nx + rasterPan.X + 1f) * 0.5f, (1f - (ny + rasterPan.Y)) * 0.5f);
        return imageOrigin + f * imageSize;
    }

    /// <summary>The model-space ray under an absolute screen pixel of the image; null before anything has been drawn.</summary>
    public (Vector3 Origin, Vector3 Dir)? ScreenRay(Vector2 screen)
    {
        if (!haveProjection || imageSize.X <= 0f || imageSize.Y <= 0f) return null;
        if (!Matrix4x4.Invert(viewProj, out var inverse)) return null;
        var f = (screen - imageOrigin) / imageSize;
        float nx = f.X * 2f - 1f - rasterPan.X;
        float ny = 1f - f.Y * 2f - rasterPan.Y;
        var near = Vector4.Transform(new Vector4(nx, ny, 0f, 1f), inverse);
        var far = Vector4.Transform(new Vector4(nx, ny, 1f, 1f), inverse);
        if (MathF.Abs(near.W) < 1e-12f || MathF.Abs(far.W) < 1e-12f) return null;
        var a = new Vector3(near.X, near.Y, near.Z) / near.W;
        var b = new Vector3(far.X, far.Y, far.Z) / far.W;
        return (a, b - a);
    }

    /// <summary>The drag under way belongs to the gizmo, decided when it was pressed.</summary>
    private bool gizmoDrag;

    /// <summary>Brush radius in object units (metres). Drives the falloff tint only.</summary>
    public float BrushRadius { get; set; }

    /// <summary>Where on the surface the cursor is, in object space, or null when it is off the model.</summary>
    public Vector3? Cursor { get; private set; }

    /// <summary>
    /// Unit direction from the model toward the camera. The bridge brush falls back on it where the surface cannot
    /// say which way is out.
    /// </summary>
    public Vector3 ToViewer => Vector3.Normalize(new Vector3(
        MathF.Cos(pitch) * MathF.Sin(yaw), MathF.Sin(pitch), MathF.Cos(pitch) * MathF.Cos(yaw)));

    /// <summary>The brush is down and being dragged across the model right now.</summary>
    public bool Painting { get; private set; }

    /// <summary>True for the single frame a stroke is released; the panel runs its expensive passes off this edge.</summary>
    public bool StrokeEnded { get; private set; }

    /// <summary>
    /// The geometry changed underneath us, so the projection has to be redone. Unlike <see cref="Clear"/> this keeps
    /// the pickable set and the camera.
    /// </summary>
    public void GeometryChanged() => geometryDirty = true;

    /// <summary>
    /// Vertex positions to draw instead of the model's own, in the same layout as <see cref="ModelParts.Positions"/>.
    /// Null draws the model as it is on disk. The camera still frames the ORIGINAL bounds, so a stroke never nudges the zoom.
    /// </summary>
    public float[]? PositionOverride { get; set; }

    /// <summary>
    /// A 0..1 value per vertex (indexed like <see cref="ModelParts.Positions"/>) drawn as a red wash, or null for none.
    /// Read when the geometry is projected, so call <see cref="GeometryChanged"/> after the values change.
    /// </summary>
    public Func<int, float>? VertexScalar { get; set; }

    /// <summary>Per pixel: <see cref="VertexScalar"/> interpolated across the surface the pixel shows.</summary>
    private float[] scalar = [];

    public void Dispose()
    {
        wrap?.Dispose();
        wrap = null;
    }

    /// <summary>Point the viewport at a model. Cheap and idempotent; only a genuine change resets the view.</summary>
    public void Show(string key, ModelParts model)
    {
        if (renderedKey == key) return;
        renderedKey = key;

        // Islands win over their submesh — see the field. A submesh with no islands is pickable itself.
        var hasIslands = model.Parts.Where(p => p.Island >= 0).Select(p => (p.Mesh, p.Submesh)).ToHashSet();
        pickable = model.Parts.Where(p => p.Island >= 0 || !hasIslands.Contains((p.Mesh, p.Submesh))).ToList();

        var submeshLabel = model.Parts.Where(p => p.Island < 0)
            .ToDictionary(p => (p.Mesh, p.Submesh), p => p.Label);
        parentOf = pickable
            .Select(p => p.Island >= 0 && submeshLabel.TryGetValue((p.Mesh, p.Submesh), out var l) ? l : null)
            .ToList();
        brushable = pickable.Select(p => !SecondSkinWriter.IsBodySkinMaterial(p.Material)).ToArray();

        yaw = MathF.PI; pitch = 0.15f; zoom = 1f; pan = Vector2.Zero;
        geometryDirty = coloursDirty = true;
    }

    /// <summary>Force a repaint of the colours — call when the ticked set changes.</summary>
    public void Recolour() => coloursDirty = true;

    /// <summary>
    /// Forget the current model, so the next <see cref="Show"/> rebuilds even under the same key. Needed after the
    /// file on disk is edited: a split renumbers parts.
    /// </summary>
    public void Clear()
    {
        renderedKey = null;
        pickable = [];
        parentOf = [];
        brushable = [];
        Hovered = null;
    }

    /// <summary>
    /// Match the render buffer to the rectangle the image is about to be drawn into. Quantised, and deferred past any
    /// held mouse button, because a reallocation forces a full re-rasterise on the UI thread.
    /// </summary>
    private void RequestResolution(Vector2 box)
    {
        var (w, h) = Quantise(box);
        if (w == bufW && h == bufH) return;
        if (ImGui.IsAnyMouseDown()) return;

        bufW = w; bufH = h;
        Allocate();
        geometryDirty = coloursDirty = true;
    }

    /// <summary>
    /// The buffer size for a given draw rectangle: supersampled, snapped up to <see cref="Snap"/>, and held
    /// to roughly <see cref="MaxPixels"/>. Pure, so it can be tested without an ImGui context.
    /// </summary>
    internal static (int W, int H) Quantise(Vector2 box)
    {
        float w = MathF.Max(box.X, 1f) * Supersample;
        float h = MathF.Max(box.Y, 1f) * Supersample;

        // Over budget: scale both axes by the same factor, so the aspect the projection is built from is kept.
        float over = w * h / MaxPixels;
        if (over > 1f)
        {
            float s = 1f / MathF.Sqrt(over);
            w *= s; h *= s;
        }

        // Snapping up can put the result a few percent over MaxPixels; it is a time budget, not a hard limit.
        return (Up(w), Up(h));

        static int Up(float v)
            => Math.Clamp(((int)MathF.Ceiling(v) + Snap - 1) / Snap * Snap, Snap * 2, 2048);
    }

    /// <summary>
    /// Draw the viewport into <paramref name="box"/>. Returns the part label the user clicked, or null. A click is a
    /// press and release without movement.
    /// </summary>
    public string? Draw(ModelParts model, Vector2 box)
    {
        Pressed = Held = false;
        if (renderedKey == null) return null;

        // Before the dirty checks, so a reallocation is rasterised in this same call.
        RequestResolution(box);

        if (geometryDirty) { Rasterize(model); geometryDirty = false; coloursDirty = true; }
        if (coloursDirty) { Recolourize(); coloursDirty = false; Upload(); }

        var size = box;
        if (wrap == null) { ImGui.Dummy(size); return null; }

        var origin = ImGui.GetCursorScreenPos();

        // An INVISIBLE BUTTON, not a bare Image: an Image never becomes the active item, so ImGui would drag the window.
        ImGui.InvisibleButton("##viewportSurface", size);
        ImGui.GetWindowDrawList().AddImage(wrap.Handle, origin, origin + size);
        imageOrigin = origin;
        imageSize = size;
        Pressed = ImGui.IsItemActivated();
        Held = ImGui.IsItemActive();

        string? clicked = null;
        StrokeEnded = false;
        var wasCursor = Cursor;
        Cursor = null;
        PointerOverModel = ImGui.IsItemHovered();
        if (PointerOverModel)
        {
            var at = (ImGui.GetMousePos() - origin) / size * new Vector2(bufW, bufH);
            int px = (int)at.X, py = (int)at.Y;
            bool inBuffer = px >= 0 && py >= 0 && px < bufW && py < bufH;
            var under = inBuffer ? id[py * bufW + px] : Empty;

            // Only where something was drawn: hit[] is not cleared between rasterises.
            if (under >= 0) Cursor = hit[py * bufW + px];

            var label = under >= 0 && under < pickable.Count ? pickable[under].Label : null;
            if (label != Hovered) { Hovered = label; coloursDirty = true; }

            // In Brush mode a click only picks a part with Shift held, so a crosshair rather than a hand.
            ImGui.SetMouseCursor(Mode == ViewportMode.Brush
                ? (ImGui.GetIO().KeyShift && label != null ? ImGuiMouseCursor.Hand
                   : Cursor != null ? ImGuiMouseCursor.ResizeAll : ImGuiMouseCursor.Arrow)
                : (label != null ? ImGuiMouseCursor.Hand : ImGuiMouseCursor.Arrow));

            if (ImGui.GetIO().MouseWheel != 0)
            {
                zoom = Math.Clamp(zoom * MathF.Pow(0.9f, ImGui.GetIO().MouseWheel), 0.15f, 6f);
                geometryDirty = true;
            }
        }
        else if (Hovered != null) { Hovered = null; coloursDirty = true; }

        // The falloff tint is stale once the cursor moves; only in Brush mode with a real radius.
        if (Mode == ViewportMode.Brush && BrushRadius > 0f && Cursor != wasCursor) coloursDirty = true;

        // Driven by the BUTTON'S own state, so a press that began elsewhere in the window does not steer the camera.
        if (ImGui.IsItemActivated())
        {
            dragging = true; dragMoved = false; dragFrom = ImGui.GetMousePos();

            // Where the press landed decides what the drag is for the whole of its life.
            Painting = Mode == ViewportMode.Brush && Cursor != null && !ImGui.GetIO().KeyShift;
            gizmoDrag = Mode == ViewportMode.Move && GizmoCapture?.Invoke() == true;
        }

        if (dragging)
        {
            var now = ImGui.GetMousePos();
            var moved = now - dragFrom;
            if (moved.LengthSquared() > 9f) dragMoved = true;

            if (ImGui.IsItemActive())
            {
                // A stroke or gizmo drag consumes the drag; the camera must not move under it.
                if (Painting || gizmoDrag)
                {
                    dragFrom = now;
                }
                else if (dragMoved)
                {
                    // Shift drags the model around the frame instead of turning it.
                    if (ImGui.GetIO().KeyShift)
                        pan += moved / size * new Vector2(2f, -2f);
                    else
                    {
                        yaw -= moved.X * 0.01f;
                        pitch = Math.Clamp(pitch + moved.Y * 0.01f, -1.5f, 1.5f);
                    }
                    dragFrom = now;
                    geometryDirty = true;
                }
            }
            else
            {
                // A click still picks a part, but only when the drag was not a stroke.
                if (!dragMoved && !Painting && !gizmoDrag) clicked = Hovered;
                if (Painting) { Painting = false; StrokeEnded = true; }
                dragging = false;
                gizmoDrag = false;
            }
        }
        return clicked;
    }

    // ── rendering ───────────────────────────────────────────────────────────

    /// <summary>
    /// Project every vertex once, then fill triangles into the id/shade/depth buffers. No backface culling: a mod's
    /// winding cannot be trusted, and the depth buffer gives the right answer.
    /// </summary>
    private void Rasterize(ModelParts model)
    {
        Array.Fill(id, Empty);
        Array.Fill(depth, float.MaxValue);
        Array.Clear(shade);

        var centre = (model.Min + model.Max) * 0.5f;
        float radius = MathF.Max((model.Max - model.Min).Length() * 0.5f, 1e-3f);

        float dist = radius * 2.6f * zoom;
        var eye = centre + new Vector3(
            MathF.Cos(pitch) * MathF.Sin(yaw), MathF.Sin(pitch), MathF.Cos(pitch) * MathF.Cos(yaw)) * dist;

        var view = Matrix4x4.CreateLookAt(eye, centre, Vector3.UnitY);
        var proj = Matrix4x4.CreatePerspectiveFieldOfView(
            0.7f, (float)bufW / bufH, MathF.Max(radius * 0.01f, 1e-4f), dist + radius * 4f);
        var vp = view * proj;
        viewProj = vp;
        rasterPan = pan;
        haveProjection = true;

        // Screen-space positions, plus a w to reject anything behind the eye, for the whole vertex array in one pass.
        // The override only counts when it describes the same vertices.
        var source = PositionOverride is { } ov && ov.Length == model.Positions.Length ? ov : model.Positions;

        int vertices = source.Length / 3;
        var screen = new Vector3[vertices];
        var valid = new bool[vertices];
        var world = new Vector3[vertices];

        // The wash's values, read once per vertex here rather than per pixel in the fill.
        var scalarOf = VertexScalar;
        float[]? perVertex = null;
        if (scalarOf != null)
        {
            perVertex = new float[vertices];
            for (int i = 0; i < vertices; i++) perVertex[i] = scalarOf(i);
        }
        Array.Clear(scalar);
        for (int i = 0; i < vertices; i++)
        {
            var p = new Vector3(source[i * 3], source[i * 3 + 1], source[i * 3 + 2]);
            world[i] = p;
            var clip = Vector4.Transform(new Vector4(p, 1f), vp);
            if (clip.W <= 1e-6f) continue;
            var ndc = new Vector3(clip.X, clip.Y, clip.Z) / clip.W;
            screen[i] = new Vector3(
                (ndc.X + pan.X + 1f) * 0.5f * bufW,
                (1f - (ndc.Y + pan.Y)) * 0.5f * bufH,
                ndc.Z);
            valid[i] = true;
        }

        // A light over the viewer's shoulder, so the shape reads without any material information.
        var light = Vector3.Normalize(new Vector3(-0.4f, 0.6f, 1f));

        // Horizontal bands, one per worker, each testing every triangle but writing only its own rows: a pixel has
        // exactly one owner, so the depth buffer needs no locking.
        int bands = Math.Clamp(Environment.ProcessorCount, 1, 16);
        int rows = (bufH + bands - 1) / bands;

        System.Threading.Tasks.Parallel.For(0, bands, band =>
        {
            int yLo = band * rows, yHi = Math.Min(yLo + rows, bufH) - 1;
            if (yLo > yHi) return;

            for (int part = 0; part < pickable.Count; part++)
            {
                var tris = pickable[part].Triangles;
                for (int t = 0; t + 2 < tris.Length; t += 3)
                {
                    int ia = tris[t], ib = tris[t + 1], ic = tris[t + 2];
                    if (!valid[ia] || !valid[ib] || !valid[ic]) continue;

                    var normal = Vector3.Cross(world[ib] - world[ia], world[ic] - world[ia]);
                    float len = normal.Length();
                    // Two-sided: winding cannot be trusted, so light both faces the same.
                    float lambert = len > 1e-12f ? MathF.Abs(Vector3.Dot(normal / len, light)) : 0.5f;
                    byte lit = (byte)(60 + 195 * MathF.Min(lambert, 1f));

                    FillTriangle(screen[ia], screen[ib], screen[ic],
                                 world[ia], world[ib], world[ic], part, lit, yLo, yHi,
                                 perVertex?[ia] ?? 0f, perVertex?[ib] ?? 0f, perVertex?[ic] ?? 0f);
                }
            }
        });
    }

    private void FillTriangle(Vector3 a, Vector3 b, Vector3 c,
                              Vector3 wa, Vector3 wb, Vector3 wc, int part, byte lit, int yLo, int yHi,
                              float sa, float sb, float sc)
    {
        float area = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
        if (MathF.Abs(area) < 1e-6f) return;
        float inv = 1f / area;

        int x0 = Math.Max((int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X))), 0);
        int x1 = Math.Min((int)MathF.Ceiling(MathF.Max(a.X, MathF.Max(b.X, c.X))), bufW - 1);
        int y0 = Math.Max((int)MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, c.Y))), yLo);
        int y1 = Math.Min((int)MathF.Ceiling(MathF.Max(a.Y, MathF.Max(b.Y, c.Y))), yHi);
        if (x1 < x0 || y1 < y0) return;

        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
        {
            float px = x + 0.5f, py = y + 0.5f;
            float w0 = ((b.X - a.X) * (py - a.Y) - (b.Y - a.Y) * (px - a.X)) * inv;
            float w1 = ((c.X - b.X) * (py - b.Y) - (c.Y - b.Y) * (px - b.X)) * inv;
            float w2 = ((a.X - c.X) * (py - c.Y) - (a.Y - c.Y) * (px - c.X)) * inv;
            if (w0 < 0 || w1 < 0 || w2 < 0) continue;

            // Barycentrics here are (w1, w2, w0) against (a, b, c): the edge opposite a vertex carries that vertex's weight.
            float z = a.Z * w1 + b.Z * w2 + c.Z * w0;
            int at = y * bufW + x;
            if (z >= depth[at]) continue;

            depth[at] = z;
            id[at] = part;
            shade[at] = lit;

            // The same barycentrics against the object-space corners; not perspective-correct, but well under a pixel off.
            hit[at] = wa * w1 + wb * w2 + wc * w0;
            scalar[at] = sa * w1 + sb * w2 + sc * w0;
        }
    }

    /// <summary>Paint the id/shade buffers into pixels. Geometry is untouched, so this costs no projection.</summary>
    private void Recolourize()
    {
        var accent = ProteusStyle.Accent;
        var (ar, ag, ab) = ((int)(accent.X * 255), (int)(accent.Y * 255), (int)(accent.Z * 255));

        // Precomputed per part so the pixel loop is a lookup.
        var tint = new (int R, int G, int B)[pickable.Count];
        var canBrush = new bool[pickable.Count];
        bool brushMode = Mode == ViewportMode.Brush;
        bool lockMode = Mode != ViewportMode.Navigate;   // a move honours locks as the brushes do
        for (int i = 0; i < pickable.Count; i++)
        {
            // An island answers to its own label AND to its submesh's (see parentOf): the submesh itself draws no pixels.
            var parent = i < parentOf.Count ? parentOf[i] : null;
            bool on = Selected.Contains(pickable[i].Label) || (parent != null && Selected.Contains(parent));
            bool hot = Hovered == pickable[i].Label || (parent != null && Hovered == parent);
            bool locked = lockMode && (Locked.Contains(pickable[i].Label) || (parent != null && Locked.Contains(parent)));
            canBrush[i] = i < brushable.Length && brushable[i] && !locked;
            tint[i] = locked      ? (hot ? (100, 110, 130) : (70, 70, 76))
                    : on && hot   ? (255, 220, 170)
                    : on          ? (ar, ag, ab)
                    : hot         ? (150, 170, 200)
                    :               (128, 128, 132);
        }

        // The brush's reach, as the falloff the stroke will actually apply; squared radius avoids a square root.
        // Mirrored, the stronger of the two discs, as the solve weighs them.
        bool brushing = brushMode && BrushRadius > 0f && Cursor is not null;
        var centre = Cursor ?? Vector3.Zero;
        var mirrorCentre = new Vector3(-centre.X, centre.Y, centre.Z);
        bool mirrored = MirrorBrush && MathF.Abs(centre.X) > 1e-4f;
        float r2 = BrushRadius * BrushRadius;


        for (int i = 0; i < id.Length; i++)
        {
            int at = i * 4;
            int part = id[i];
            if (part < 0 || part >= tint.Length)
            {
                rgba[at] = rgba[at + 1] = rgba[at + 2] = rgba[at + 3] = 0;
                continue;
            }
            var (r, g, b) = tint[part];
            int s = shade[i];

            // The wind wash, under the brush blob: red by how much the painted amount is.
            if (VertexScalar != null && scalar[i] > 0f && canBrush[part])
            {
                float k = MathF.Min(scalar[i], 1f) * 0.75f;
                r = (int)(r + (235 - r) * k);
                g = (int)(g + (40 - g) * k);
                b = (int)(b + (40 - b) * k);
            }

            if (brushing && canBrush[part])
            {
                float d2 = (hit[i] - centre).LengthSquared();
                if (mirrored) d2 = MathF.Min(d2, (hit[i] - mirrorCentre).LengthSquared());
                if (d2 < r2)
                {
                    // Blend toward the hot colour by the same falloff the edit uses.
                    float w = MeshVolumeSolve.Falloff(MathF.Sqrt(d2) / BrushRadius);
                    r = (int)(r + (255 - r) * w);
                    g = (int)(g + (90 - g) * w);
                    b = (int)(b + (70 - b) * w);
                }
            }

            rgba[at] = (byte)(r * s / 255);
            rgba[at + 1] = (byte)(g * s / 255);
            rgba[at + 2] = (byte)(b * s / 255);
            rgba[at + 3] = 255;
        }
    }


    private void Upload()
    {
        try
        {
            wrap?.Dispose();
            wrap = textures.CreateFromRaw(RawImageSpecification.Rgba32(bufW, bufH), rgba);
        }
        catch (Exception ex)
        {
            wrap = null;
            log.Warning(ex, "[Proteus] parts: could not upload the model preview");
        }
    }
}
