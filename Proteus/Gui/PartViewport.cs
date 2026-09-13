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
/// The model, drawn so the user can click the part they mean.
/// <para/>
/// This replaced a list of numbered rows with thumbnails, which asked someone who wants to remove a bow to
/// work out which of "1.2c" and "1.2d" the bow is. Here they click the bow.
/// <para/>
/// Software-rendered, and deliberately. The obvious alternative is a Direct3D viewport — DragAndDropTexturing
/// has a good one — but it costs three Vortice packages, a pair of HLSL shaders, and borrowing the game's own
/// device context, which means saving and restoring render state the game is also using. None of that buys
/// anything here: the model is a few tens of thousands of triangles drawn once per camera move, at a size
/// smaller than a texture thumbnail.
/// <para/>
/// It also makes picking exact and free. Rasterizing writes a PART ID per pixel, so a click is an array
/// lookup rather than a ray cast against a triangle soup — no bounding hierarchy, no epsilon, and it cannot
/// disagree with what is on screen, because it IS what is on screen.
/// </summary>
public sealed class PartViewport : IDisposable
{
    /// <summary>
    /// The shape the viewport had when it was a fixed size, and still the shape it starts at. Public because
    /// the panel sizes the image from it: it wants the model's own proportions, not whatever the buffer has
    /// been resized to this frame — deriving the width from the CURRENT buffer would feed the last resize
    /// back into the next one.
    /// </summary>
    public const float DefaultAspect = (float)DefaultWidth / DefaultHeight;

    private const int DefaultWidth = 460, DefaultHeight = 560;

    /// <summary>
    /// Ceiling on the render buffer, as an AREA rather than a per-axis limit.
    /// <para/>
    /// Rasterising is O(pixels) and runs on the UI thread, so the cap is a time budget, not a memory one:
    /// 900k pixels is ~3.5x the old 460x560, which on a 60,000-triangle hair model is the difference between
    /// a re-render you do not notice and one you do. It still allows ~850x1050. Above it the viewport
    /// upscales, which is exactly what it did at every size before it could resize at all.
    /// </summary>
    private const int MaxPixels = 900_000;

    /// <summary>Buffer granularity. Snapping UP means the image is always downsampled, never blurred up.</summary>
    private const int Snap = 64;

    /// <summary>
    /// How much larger than the image the buffer is rendered. NOT 1.0, and the number is not arbitrary: the
    /// old fixed 560px buffer was drawn into a 360px box, so the picture has always been downsampled by
    /// about this much, and that downsample is the only antialiasing this rasteriser has. Rendering at
    /// exactly the display size would make an enlarged viewport visibly rougher than the small one it
    /// replaced — a resize that made the model look worse.
    /// </summary>
    private const float Supersample = 1.5f;

    /// <summary>Nothing was drawn at this pixel.</summary>
    private const int Empty = -1;

    private readonly ITextureProvider textures;
    private readonly IPluginLog log;

    /// <summary>
    /// Resolution of the render buffers. A field rather than a constant so a window the user has enlarged
    /// gets a viewport rendered at its real size instead of a 460x560 image stretched over it.
    /// </summary>
    private int bufW = DefaultWidth, bufH = DefaultHeight;

    // Per pixel: which pickable part is in front, and how lit its surface is. Split so a change of selection
    // recolours without touching geometry — the camera is what makes a re-render necessary, not the colours.
    private int[] id = [];
    private byte[] shade = [];
    private float[] depth = [];
    private byte[] rgba = [];

    /// <summary>
    /// The point on the model's surface each pixel is looking at, in object space — the same space
    /// <see cref="ModelParts.Positions"/> is in. Meaningless where <see cref="id"/> is <see cref="Empty"/>.
    /// <para/>
    /// This is what makes a brush possible without a ray cast. The rasteriser already interpolates the
    /// depth across a triangle from barycentrics it has in hand, so interpolating the world position beside
    /// it is nearly free, exact, and needs no unprojection, no bounding hierarchy and no epsilon.
    /// <para/>
    /// Per PIXEL rather than per vertex on purpose: the falloff has to be recomputed every time the cursor
    /// moves, and with the surface point already stored that is one pass over the image with no projection —
    /// which is the whole reason <see cref="Recolourize"/> is separate from <see cref="Rasterize"/>.
    /// </summary>
    private Vector3[] hit = [];

    private IDalamudTextureWrap? wrap;

    // Camera, in the only terms an orbit needs: where it is on the sphere around the model, and how far out.
    private float yaw = MathF.PI, pitch = 0.15f, zoom = 1f;
    private Vector2 pan;

    private string? renderedKey;
    private bool geometryDirty = true, coloursDirty = true;

    /// <summary>
    /// What a click can land on: the finest thing at each place on the model.
    /// <para/>
    /// An island where a submesh has them, the submesh where it does not — never both, because they occupy
    /// the same pixels and the finer one is the useful answer. The list beside the viewport still offers the
    /// whole submesh for someone who wants it.
    /// </summary>
    private List<ModelPart> pickable = [];

    /// <summary>
    /// For each pickable part, the label of the SUBMESH it belongs to — null when it is that submesh itself.
    /// <para/>
    /// Colouring needs it because the list offers a row for the whole submesh while only its islands are
    /// pickable, so a submesh ticked in the list matches no pickable label at all. Without this the model
    /// stayed entirely grey with a 53,000-triangle part ticked, which read as the tick having failed.
    /// </summary>
    private List<string?> parentOf = [];

    /// <summary>
    /// For each pickable part, whether the brush may move it — false for skin, which the brush never moves
    /// (see <c>MeshVolumeSolve</c>) and so is never tinted: a falloff blob across the body would promise an
    /// edit that is not going to happen.
    /// <para/>
    /// Worked out once per model in <see cref="Show"/>. It used to be rebuilt inside the colouring pass, which
    /// runs on every cursor move over the model, re-running a string test on every part's material each time.
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
    }

    /// <summary>Parts the user has ticked, by label — drawn in the accent colour.</summary>
    public IReadOnlySet<string> Selected { get; set; } = new HashSet<string>();

    /// <summary>The part under the cursor, or null. Set by <see cref="Draw"/>, and also settable from the
    /// list beside it so hovering a row lights the model up.</summary>
    public string? Hovered { get; set; }

    /// <summary>
    /// Whether the cursor is over the model image this frame — as opposed to <see cref="Hovered"/> being set
    /// by the list beside it. Lets the panel explain a click the model absorbs without acting on.
    /// </summary>
    public bool PointerOverModel { get; private set; }

    /// <summary>What a drag on the model means.</summary>
    public enum ViewportMode
    {
        /// <summary>Drag turns the model; a click picks a part. The original behaviour.</summary>
        Navigate,

        /// <summary>
        /// A drag that STARTS ON THE MODEL paints; one that starts on the background still turns it.
        /// <para/>
        /// Chosen over a modifier because painting and turning are both continuous activities and holding a
        /// key for one of them is miserable, and over a mode switch per rotation for the same reason. Where
        /// the drag starts is unambiguous, needs no key, and is what sculpting tools already do.
        /// </summary>
        Brush,
    }

    public ViewportMode Mode { get; set; } = ViewportMode.Navigate;

    /// <summary>
    /// Brush radius in object units, which for a character model is metres. Drives the falloff tint only —
    /// the edit itself is the panel's business.
    /// </summary>
    public float BrushRadius { get; set; }

    /// <summary>
    /// Where on the surface the cursor is, in object space, or null when it is off the model. The centre of
    /// the brush, and the one thing a stroke needs from the viewport.
    /// </summary>
    public Vector3? Cursor { get; private set; }

    /// <summary>
    /// Unit direction from the model toward the camera — the side of the model being looked at, and so the
    /// side being painted. The bridge brush falls back on it where the surface cannot say which way is out.
    /// </summary>
    public Vector3 ToViewer => Vector3.Normalize(new Vector3(
        MathF.Cos(pitch) * MathF.Sin(yaw), MathF.Sin(pitch), MathF.Cos(pitch) * MathF.Cos(yaw)));

    /// <summary>The brush is down and being dragged across the model right now.</summary>
    public bool Painting { get; private set; }

    /// <summary>
    /// True for the single frame a stroke is released. The panel runs the passes that are too expensive to
    /// run per frame — the slope limit, the unfold, the normal rebuild — off this edge.
    /// </summary>
    public bool StrokeEnded { get; private set; }

    /// <summary>
    /// The geometry changed underneath us, so the projection has to be redone. Unlike <see cref="Clear"/>
    /// this keeps the pickable set and the camera, because a brush stroke changes where vertices ARE without
    /// changing which parts exist or where the user is looking from.
    /// </summary>
    public void GeometryChanged() => geometryDirty = true;

    /// <summary>
    /// Vertex positions to draw instead of the model's own, in the same layout as
    /// <see cref="ModelParts.Positions"/>. Null draws the model as it is on disk.
    /// <para/>
    /// An override rather than a rebuilt <see cref="ModelParts"/> because the parts, the islands and the
    /// pickable set are all unchanged by a stroke — only where the vertices are has changed, and rebuilding
    /// the rest would re-run the weld and island split on every settle.
    /// <para/>
    /// The camera still frames the model's ORIGINAL bounds, deliberately: an inflate would otherwise nudge
    /// the zoom every time the user let go of the brush.
    /// </summary>
    public float[]? PositionOverride { get; set; }

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
    /// Forget the current model, so the next <see cref="Show"/> rebuilds even under the same key. Needed
    /// after the file on disk is edited: a split renumbers parts, and a viewport still holding the old
    /// pickable set would report labels that no longer mean the same geometry.
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
    /// Match the render buffer to the rectangle the image is about to be drawn into, so an enlarged window
    /// gets a viewport rendered at its real size rather than an upscale.
    /// <para/>
    /// Quantised AND deferred, because a reallocation is not free: it throws away the buffers and forces a
    /// full re-rasterise, which runs on the UI thread and allocates three vertex-sized arrays of its own.
    /// Deferring past any held mouse button means a window-edge drag costs nothing at all — the existing
    /// buffer keeps being stretched into the new rect, which is what <c>AddImage</c> did before this
    /// existed — and the snap then stops the small settling changes (a scrollbar appearing, a pixel of
    /// rounding) from re-rendering at all. Not keyed on the drag itself: the size can also change from a UI
    /// scale change or a restored window size, and those must converge too.
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

        // Over budget: scale both axes by the same factor, computed in one step rather than stepped down in
        // a loop. The aspect is what makes the picture right, and it is also what the projection is built
        // from — shrinking one axis alone would stretch the model.
        float over = w * h / MaxPixels;
        if (over > 1f)
        {
            float s = 1f / MathF.Sqrt(over);
            w *= s; h *= s;
        }

        // Snapping up can put the result a few percent back over MaxPixels. That is fine: it is a budget for
        // how long a re-render may take, not a limit anything depends on.
        return (Up(w), Up(h));

        static int Up(float v)
            => Math.Clamp(((int)MathF.Ceiling(v) + Snap - 1) / Snap * Snap, Snap * 2, 2048);
    }

    /// <summary>
    /// Draw the viewport into <paramref name="box"/>. Returns the part label the user clicked, or null.
    /// <para/>
    /// A click is a press and release without movement: the same button orbits, and treating a drag as a
    /// click would select whatever the camera happened to stop over.
    /// </summary>
    public string? Draw(ModelParts model, Vector2 box)
    {
        if (renderedKey == null) return null;

        // Before the dirty checks, so a reallocation is rasterised and uploaded in this same call rather
        // than leaving a frame with empty buffers behind it.
        RequestResolution(box);

        if (geometryDirty) { Rasterize(model); geometryDirty = false; coloursDirty = true; }
        if (coloursDirty) { Recolourize(); coloursDirty = false; Upload(); }

        var size = box;
        if (wrap == null) { ImGui.Dummy(size); return null; }

        var origin = ImGui.GetCursorScreenPos();

        // An INVISIBLE BUTTON with the image painted into its rect, not a bare Image.
        //
        // ImGui moves a window when a drag begins on it and no item takes the press. An Image submits an
        // item — so hovering and hit-testing work — but it is not clickable, never becomes the ACTIVE item,
        // and so the press falls straight through to the window: turning the model dragged the whole Proteus
        // window instead. A button is clickable, claims the press, and the window stays put.
        ImGui.InvisibleButton("##viewportSurface", size);
        ImGui.GetWindowDrawList().AddImage(wrap.Handle, origin, origin + size);

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

            // Only where something was drawn: hit[] is not cleared between rasterises, so off the model it
            // holds whatever the last frame that DID cover this pixel left behind.
            if (under >= 0) Cursor = hit[py * bufW + px];

            var label = under >= 0 && under < pickable.Count ? pickable[under].Label : null;
            if (label != Hovered) { Hovered = label; coloursDirty = true; }

            // In Brush mode the hand cursor would promise a click that picks a part, which it no longer
            // does. A crosshair says "this acts where it is pointing".
            ImGui.SetMouseCursor(Mode == ViewportMode.Brush
                ? (Cursor != null ? ImGuiMouseCursor.ResizeAll : ImGuiMouseCursor.Arrow)
                : (label != null ? ImGuiMouseCursor.Hand : ImGuiMouseCursor.Arrow));

            if (ImGui.GetIO().MouseWheel != 0)
            {
                zoom = Math.Clamp(zoom * MathF.Pow(0.9f, ImGui.GetIO().MouseWheel), 0.15f, 6f);
                geometryDirty = true;
            }
        }
        else if (Hovered != null) { Hovered = null; coloursDirty = true; }

        // The falloff is painted from the cursor's surface point, so the tint is stale the moment it moves.
        // Only in Brush mode, and only once the radius is real — otherwise every mouse move over the model
        // would repaint the image for nothing.
        if (Mode == ViewportMode.Brush && BrushRadius > 0f && Cursor != wasCursor) coloursDirty = true;

        // Driven by the BUTTON'S own state, not raw mouse buttons: a press that began somewhere else in the
        // window — the part list, the tab bar — must not steer the camera, and IsItemActive is exactly the
        // question "is this button the one being held".
        if (ImGui.IsItemActivated())
        {
            dragging = true; dragMoved = false; dragFrom = ImGui.GetMousePos();

            // WHERE THE PRESS LANDED decides what the drag is for the whole of its life, and it is decided
            // once here rather than re-asked per frame. A stroke that wanders off the silhouette must keep
            // painting when it comes back rather than turning the model out from under itself, and a camera
            // drag that happens to pass over the mesh must not start painting.
            Painting = Mode == ViewportMode.Brush && Cursor != null && !ImGui.GetIO().KeyShift;
        }

        if (dragging)
        {
            var now = ImGui.GetMousePos();
            var moved = now - dragFrom;
            if (moved.LengthSquared() > 9f) dragMoved = true;

            if (ImGui.IsItemActive())
            {
                // A stroke consumes the drag. The panel reads Painting and Cursor each frame and does the
                // work; nothing here moves the camera, so the model holds still under the brush.
                if (Painting)
                {
                    dragFrom = now;
                }
                else if (dragMoved)
                {
                    // Shift drags the model around the frame instead of turning it — the usual pairing, and
                    // the only way to look at something the silhouette pushes off the edge when zoomed in.
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
                // A click still picks a part, but only when the drag was not a stroke — in Brush mode a tap
                // on the model is the smallest possible dab of paint, not a selection.
                if (!dragMoved && !Painting) clicked = Hovered;
                if (Painting) { Painting = false; StrokeEnded = true; }
                dragging = false;
            }
        }
        return clicked;
    }

    // ── rendering ───────────────────────────────────────────────────────────

    /// <summary>
    /// Project every vertex once, then fill triangles into the id/shade/depth buffers.
    /// <para/>
    /// No backface culling, on purpose. Model winding is not something this can assume — a mod's exporter
    /// may have flipped it — and getting it wrong turns a garment inside out. The depth buffer already gives
    /// the right answer; culling would only have saved fill.
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

        // Screen-space positions, plus a w to reject anything behind the eye. Done for the whole vertex
        // array in one pass: a vertex is shared by every triangle that touches it, and by every part.
        // The override only counts when it describes the same vertices; a stale array from a previous model
        // would project the wrong geometry under this one's triangles.
        var source = PositionOverride is { } ov && ov.Length == model.Positions.Length ? ov : model.Positions;

        int vertices = source.Length / 3;
        var screen = new Vector3[vertices];
        var valid = new bool[vertices];
        var world = new Vector3[vertices];
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

        // Split the image into horizontal bands, one per worker, and let each band consider every triangle
        // while writing only its own rows. A pixel therefore has exactly one owner and the depth buffer
        // needs no locking — which the obvious alternative, parallelising over triangles, cannot say.
        //
        // The cost is that each band tests every triangle's bounds. That is a comparison against work that
        // is dominated by fill, and it buys the difference between a viewport that turns smoothly on a
        // 60,000-triangle hair model and one that does not.
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
                    // Two-sided: the sign of the facing tells us nothing reliable (see the remarks), so
                    // light both faces the same.
                    float lambert = len > 1e-12f ? MathF.Abs(Vector3.Dot(normal / len, light)) : 0.5f;
                    byte lit = (byte)(60 + 195 * MathF.Min(lambert, 1f));

                    FillTriangle(screen[ia], screen[ib], screen[ic],
                                 world[ia], world[ib], world[ic], part, lit, yLo, yHi);
                }
            }
        });
    }

    private void FillTriangle(Vector3 a, Vector3 b, Vector3 c,
                              Vector3 wa, Vector3 wb, Vector3 wc, int part, byte lit, int yLo, int yHi)
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

            // Barycentrics here are (w1, w2, w0) against (a, b, c) — the edge opposite a vertex carries that
            // vertex's weight.
            float z = a.Z * w1 + b.Z * w2 + c.Z * w0;
            int at = y * bufW + x;
            if (z >= depth[at]) continue;

            depth[at] = z;
            id[at] = part;
            shade[at] = lit;

            // The same barycentrics, against the object-space corners. Screen-space interpolation is not
            // perspective-correct, so this is a hair off where the true surface point is — by well under a
            // pixel's worth of geometry at these depths, against a brush radius measured in millimetres.
            hit[at] = wa * w1 + wb * w2 + wc * w0;
        }
    }

    /// <summary>
    /// Paint the id/shade buffers into pixels. Geometry is untouched, so ticking a part or moving the mouse
    /// costs one pass over the image and no projection at all.
    /// </summary>
    private void Recolourize()
    {
        var accent = ProteusStyle.Accent;
        var (ar, ag, ab) = ((int)(accent.X * 255), (int)(accent.Y * 255), (int)(accent.Z * 255));

        // Precomputed per part so the pixel loop is a lookup: models run to tens of thousands of triangles
        // but only a few dozen parts.
        var tint = new (int R, int G, int B)[pickable.Count];
        for (int i = 0; i < pickable.Count; i++)
        {
            // An island answers to its own label AND to its submesh's — see parentOf. Ticking the whole
            // part in the list has to light up every island of it, since the part itself draws no pixels.
            var parent = i < parentOf.Count ? parentOf[i] : null;
            bool on = Selected.Contains(pickable[i].Label) || (parent != null && Selected.Contains(parent));
            bool hot = Hovered == pickable[i].Label || (parent != null && Hovered == parent);
            tint[i] = on && hot ? (255, 220, 170)
                    : on        ? (ar, ag, ab)
                    : hot       ? (150, 170, 200)
                    :             (128, 128, 132);
        }

        // The brush's reach, as the falloff the stroke will actually apply — so what the user sees shaded is
        // what will move, and by how much. Squared radius so the pixel loop compares without a square root.
        bool brushing = Mode == ViewportMode.Brush && BrushRadius > 0f && Cursor is not null;
        var centre = Cursor ?? Vector3.Zero;
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

            if (brushing && part < brushable.Length && brushable[part])
            {
                float d2 = (hit[i] - centre).LengthSquared();
                if (d2 < r2)
                {
                    // Blend toward the hot colour by the very falloff the edit uses, so the blob reads as a
                    // gradient rather than a disc and the user can see the soft edge they are relying on.
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
