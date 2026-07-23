using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using Proteus.Services;

namespace Proteus.Interop;

/// <summary>
/// One composited body-diffuse's "glow recipe": the disk path of the .tex Proteus wrote (also what the
/// live texture resource reports, so we can find it on the character) plus a per-pixel row map at that
/// texture's resolution. Each map byte is <c>0</c> where the overlay doesn't apply, else
/// <c>0x80 | (subRowA ? 0x40 : 0) | pairIndex</c> — the colour-table pair (0-15) and A/B sub-row that
/// pixel resolves to. Built by the compositor from the overlay's index texture.
/// </summary>
public sealed record SkinGlowTarget(string DiffuseDiskPath, byte[] RowMap, int Width, int Height);

/// <summary>
/// Makes a skin colorset row's mesh region "glow" on the local player. Skin has no per-character colour
/// table (unlike gear — see <see cref="ColorTableHighlighter"/>); its look is baked into the body diffuse.
/// So we rebind the live diffuse GPU texture: walk the render material graph to Proteus's own composited
/// body diffuse (sampler slot 0x47, path under \Proteus\textures\ — unique & single-owner, so no other
/// actor is affected) and swap its inner <see cref="Texture"/> for a copy whose target row's pixels are
/// forced bright. The highlight is a static colour (a per-frame 4K rebuild is too heavy to hue-cycle) and
/// is built off the framework thread, then uploaded and pointer-swapped on it.
/// </summary>
public sealed unsafe class SkinDiffuseGlow : IDisposable
{
    // We identify our composited body diffuse by its FILENAME, not by the material's internal sampler
    // slot id. That id is not stable across bodies/shaders — a Bibo body was observed serving diffuse at
    // 0x4B while the code assumed 0x47, so the walk matched nothing and the highlight silently did
    // nothing. The composited diffuse always ends in "_d.tex" under \Proteus\textures\, which is exact.
    private const string DiffuseSuffix = "_d.tex";

    private sealed class Target
    {
        public string DiskPath = "";
        // Every overlay compositing into this one diffuse contributes a row map; the highlight is their
        // union (a pixel glows if ANY of them resolves it to the selected row).
        public readonly List<(byte[] Map, int W, int H)> Maps = new();
        public volatile byte[]? Bgra;   // built off-thread; published last (W/H set first)
        public int W, H;
    }

    private readonly IFramework framework;
    private readonly IObjectTable objects;
    private readonly TextureLoader textures;
    private readonly IPluginLog log;

    // Desired targets keyed by normalized diffuse path; swapped in as one reference (volatile publish).
    private volatile Dictionary<string, Target> _targetByPath = new();
    // Currently-swapped textures keyed by the live texture slot (Texture** address — stable per handle, so
    // two handles sharing one path don't collide): our created texture, the original we displaced, and
    // WHICH target build produced it (so switching rows on the same material re-swaps instead of seeing
    // "already swapped" and keeping the previous row's highlight).
    private readonly Dictionary<nint, (nint Ours, nint Original, Target Applied)> _applied = new();

    // Identity of the active target, for the button's pressed state.
    private int _idRow = -1;
    private bool _idA;

    /// <summary>Ghosts the gear shells above the skin so an occluded skin glow shows through. Set by Plugin.</summary>
    public ShellNormalGhost? Ghost { get; set; }

    public SkinDiffuseGlow(IFramework framework, IObjectTable objects, TextureLoader textures, IPluginLog log)
    {
        this.framework = framework;
        this.objects   = objects;
        this.textures  = textures;
        this.log       = log;
        framework.Update += OnFramework;
    }

    /// <summary>Glow preset row <paramref name="row"/> (1-16) sub-row A/B across every material in <paramref name="targets"/>.</summary>
    public void SetTarget(IReadOnlyList<SkinGlowTarget> targets, int row, bool isA)
    {
        _idRow = row;
        _idA   = isA;
        var map = new Dictionary<string, Target>();
        foreach (var t in targets)
        {
            var norm = Normalize(t.DiffuseDiskPath);
            if (!map.TryGetValue(norm, out var target))
                map[norm] = target = new Target { DiskPath = t.DiffuseDiskPath };
            target.Maps.Add((t.RowMap, t.Width, t.Height));   // several overlays may share one diffuse
        }
        foreach (var target in map.Values)
        {
            var tt = target;
            Task.Run(() => Build(tt, row, isA));
        }
        _targetByPath = map;
        // Skin sits below every gear shell, so ghost them all while the skin glow is active.
        Ghost?.GhostAbove(null);
    }

    public void Clear()
    {
        _idRow = -1;
        _targetByPath = new();
        Ghost?.Clear();
    }

    public bool IsTarget(IReadOnlyList<SkinGlowTarget> targets, int row, bool isA)
    {
        var map = _targetByPath;
        if (map.Count == 0 || row != _idRow || isA != _idA)
            return false;
        // Compare by DISTINCT normalized paths — a target list may repeat a path (two overlays, one material).
        var wanted = new HashSet<string>();
        foreach (var t in targets) wanted.Add(Normalize(t.DiffuseDiskPath));
        if (wanted.Count != map.Count) return false;
        foreach (var w in wanted)
            if (!map.ContainsKey(w))
                return false;
        return true;
    }

    private void Build(Target t, int row, bool isA)
    {
        try
        {
            var dec = textures.LoadTexAsRgba(t.DiskPath);
            if (dec == null) { log.Warning("[SkinGlow] could not decode diffuse {0}", t.DiskPath); return; }
            var (rgba, w, h) = dec.Value;
            int pair = row - 1;
            var bgra = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int p = (y * w + x) * 4;
                    bool hit = false;
                    foreach (var (rm, mw, mh) in t.Maps)   // union: glow if any overlay resolves this row here
                    {
                        int my = mh == h ? y : (int)((long)y * mh / h);
                        int mx = mw == w ? x : (int)((long)x * mw / w);
                        byte m = rm[my * mw + mx];
                        if ((m & 0x80) != 0 && (m & 0x0F) == pair && ((m & 0x40) != 0) == isA) { hit = true; break; }
                    }
                    if (hit)
                    {
                        bgra[p] = 0x30; bgra[p + 1] = 0xC4; bgra[p + 2] = 0xF4; bgra[p + 3] = 255;   // #F4C430
                    }
                    else
                    {
                        bgra[p] = rgba[p + 2]; bgra[p + 1] = rgba[p + 1]; bgra[p + 2] = rgba[p]; bgra[p + 3] = rgba[p + 3];
                    }
                }
            }
            t.W = w; t.H = h;
            t.Bgra = bgra;   // volatile release — publishes W/H too
        }
        catch (Exception ex)
        {
            log.Error(ex, "[SkinGlow] highlight build failed for {0}", t.DiskPath);
        }
    }

    private void OnFramework(IFramework fw) => Apply(objects.LocalPlayer?.Address ?? 0);

    private void Apply(nint addr)
    {
        if (addr == 0) { _applied.Clear(); return; }   // character gone; textures went with it
        var targets = _targetByPath;
        if (targets.Count == 0 && _applied.Count == 0) return;

        var seen = new HashSet<nint>();
        bool walked = ForEachProteusDiffuse(addr, (slot, name) =>
        {
            seen.Add(slot);
            var cur = *(nint*)slot;

            if (targets.TryGetValue(Normalize(name), out var t))
            {
                bool haveApplied = _applied.TryGetValue(slot, out var ap);

                // Already showing exactly this target's texture? Nothing to do.
                if (haveApplied && ap.Applied == t && cur == ap.Ours) return;

                var bgra = t.Bgra;
                if (bgra == null) return;          // desired build not ready — leave what's showing
                var tex = CreateTex(bgra, t.W, t.H);
                if (tex == 0) return;

                // Swap FIRST, then release the old highlight (only if the slot really still held ours) —
                // never DecRef a texture that is still the live slot value, or the render thread can sample
                // freed memory. Keep the true game original across a row change; else the displaced value is it.
                var old = Interlocked.Exchange(ref *(nint*)slot, tex);
                bool swappedOurs = haveApplied && old == ap.Ours;
                if (swappedOurs) ((Texture*)ap.Ours)->DecRef();
                _applied[slot] = (tex, swappedOurs ? ap.Original : old, t);
            }
            else if (_applied.TryGetValue(slot, out var ap))
            {
                if (cur == ap.Ours)                // still ours → restore original, release ours
                {
                    Interlocked.Exchange(ref *(nint*)slot, ap.Original);
                    ((Texture*)ap.Ours)->DecRef();
                }
                _applied.Remove(slot);
            }
        });

        // Prune only when the traversal actually ran: an applied slot it visited-but-didn't-match had its
        // model rebuilt (the game already freed our texture with the old handle, so no DecRef — that would
        // crash). If it couldn't walk this frame (mid-redraw / not a CharacterBase), keep tracking for next.
        if (walked && _applied.Count > 0)
            foreach (var k in _applied.Keys.Where(k => !seen.Contains(k)).ToList())
                _applied.Remove(k);
    }

    // Returns false if the character couldn't be walked this frame (no CharacterBase draw object yet).
    private bool ForEachProteusDiffuse(nint addr, Action<nint /*Texture** slot*/, string /*name*/> visit)
    {
        var chara = (Character*)addr;
        var draw  = chara->GameObject.DrawObject;
        if (draw == null || draw->GetObjectType() != ObjectType.CharacterBase)
            return false;

        var cb = (CharacterBase*)draw;
        foreach (var modelPtr in cb->ModelsSpan)
        {
            var model = modelPtr.Value;
            if (model == null) continue;
            foreach (var matPtr in model->MaterialsSpan)
            {
                var mat = matPtr.Value;
                if (mat == null) continue;
                foreach (ref var entry in mat->TexturesSpan)
                {
                    var handle = entry.Texture;
                    if (handle == null) continue;
                    var name = handle->FileName.ToString();
                    if (string.IsNullOrEmpty(name)) continue;

                    // Our composited body diffuse: under \Proteus\textures\ and ending in _d.tex. The
                    // normal (_n.tex) sits under the same folder but must NOT be swapped. Matching the
                    // name is slot-independent (see DiffuseSuffix).
                    bool underProteus =
                        name.IndexOf("/Proteus/textures/", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        name.IndexOf("\\Proteus\\textures\\", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!underProteus || !name.EndsWith(DiffuseSuffix, StringComparison.OrdinalIgnoreCase))
                        continue;
                    visit((nint)(&handle->Texture), name);
                }
            }
        }
        return true;
    }

    private nint CreateTex(byte[] bgra, int w, int h)
    {
        if (w <= 0 || h <= 0 || bgra.Length < w * h * 4) return 0;
        var size = stackalloc int[2];
        size[0] = w;
        size[1] = h;
        var tex = Device.Instance()->CreateTexture2D(size, 1, TextureFormat.B8G8R8A8_UNORM,
            TextureFlags.TextureType2D | TextureFlags.Managed | TextureFlags.Immutable, 7);
        if (tex == null) return 0;

        bool ok;
        fixed (byte* p = bgra)
            ok = tex->InitializeContents(p);
        if (!ok) { tex->DecRef(); return 0; }
        return (nint)tex;
    }

    // The live resource reports forward-slashed, sometimes quote-wrapped paths; the compositor stores a
    // native (back-slashed) disk path. Normalize both to compare.
    private static string Normalize(string s)
        => s.Trim().Trim('"').Replace('\\', '/').ToLowerInvariant();

    public void Dispose()
    {
        framework.Update -= OnFramework;
        Clear();
        Apply(objects.LocalPlayer?.Address ?? 0);   // restore anything still swapped
    }
}
