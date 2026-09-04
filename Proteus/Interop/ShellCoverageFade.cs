using System;
using System.Collections.Generic;
using System.IO;
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
/// Makes a dark-only region's OPACITY follow its glow, so where the light has taken the glow away there is
/// nothing left but skin.
///
/// <para>Dimming the emissive alone is only half of "invisible in the light". A glowing shell still draws a
/// surface, and on <c>characterscroll.shpk</c> that surface is the colour table's diffuse with no base
/// texture beneath it — usually black, because that is what a coloured glow needs to read against. Fade the
/// glow alone and a dark-only tattoo becomes a black silhouette at noon, which is exactly what Atramentum
/// Luminis did not do.</para>
///
/// <para><b>What moves.</b> A shell layer's per-pixel transparency is its normal map's BLUE channel — the
/// compositor writes the overlay's coverage there — so the light scales that blue, and nothing else. Same
/// mechanism as <see cref="ShellNormalGhost"/>, which has been swapping these textures live since the
/// colorset locator shipped; the same swap-then-DecRef and prune-don't-free rules apply for the same
/// reasons, so read that file's comments before changing this one.</para>
///
/// <para><b>Per row, not per layer.</b> The index texture beside the normal says which texel belongs to
/// which colour-table row, so only the rows that asked to hide are faded. That is the whole reason this
/// works on the coverage rather than on the material's alpha constants, which are material-wide and would
/// have forced every row on a layer to agree.</para>
///
/// <para>Quantised into a few steps with a dead band: rebuilding a 2048² texture is not something to do per
/// frame, and a light level does not change fast. Between steps this costs one dictionary lookup.</para>
/// </summary>
public sealed unsafe class ShellCoverageFade : IDisposable
{
    /// <summary>
    /// How many steps the fade is cut into. Each one is a texture rebuild, so this trades smoothness for
    /// work — and it can afford to be coarse because the colour-table glow fade is continuous and carries
    /// the eye through the transition while the surface steps underneath it.
    /// </summary>
    private const int Steps = 8;

    /// <summary>
    /// How far the light must move past the step it is already showing before the step changes.
    /// <para/>
    /// MUST be greater than 0.5, or it does nothing at all: the step comes from rounding, which already
    /// snaps everything within half a step, so a band narrower than that can only ever agree with the
    /// rounding it was meant to override. It shipped at 0.35 and was dead code — a level parked on a
    /// boundary rebuilt a multi-megabyte texture back and forth for as long as it sat there.
    /// </summary>
    private const float Hysteresis = 0.75f;

    private readonly IFramework framework;
    private readonly IObjectTable objects;
    private readonly SceneLightService light;
    private readonly TextureLoader textures;
    private readonly Configuration config;
    private readonly IPluginLog log;

    /// <summary>Shell material leaf → its light response. Supplied by the compositor's publish.</summary>
    public Func<string, ShellLightProfile?>? LightFor { get; set; }

    /// <summary>Whether ANY live shell asks for a light response at all. Checked before the character is
    /// walked, so a collection with no light-sensitive glow — almost every collection — pays nothing per
    /// frame beyond this call.</summary>
    public Func<bool>? AnyLight { get; set; }

    /// <summary>The locator ghost, which swaps these same slots. See <see cref="ShellNormalGhost.IsBusy"/>:
    /// while it holds any of them, this stands aside completely.</summary>
    public ShellNormalGhost? Ghost { get; set; }

    /// <summary>One shell's faded coverage, built off-thread and reused until the step or the file changes.</summary>
    private sealed class Faded
    {
        public volatile byte[]? Bgra;
        public int W, H;
        public long Stamp;
        public int Step;
    }

    // ONE entry per normal path, not one per (path, step). Each holds a full-resolution BGRA buffer — 16 MB
    // for a 2048² normal — so keeping every step a shell had passed through parked well over a hundred
    // megabytes per shell and never released it. Matching ShellNormalGhost's one-per-path rule costs a
    // rebuild when the light steps back to where it was, which is off-thread and rare.
    private readonly Dictionary<string, Faded> _built = new();

    // Currently-swapped slots: our created texture, the original we displaced, and the step it shows.
    private readonly Dictionary<nint, (nint Ours, nint Original, int Step)> _applied = new();

    public ShellCoverageFade(IFramework framework, IObjectTable objects, SceneLightService light,
                             TextureLoader textures, Configuration config, IPluginLog log)
    {
        this.framework = framework;
        this.objects   = objects;
        this.light     = light;
        this.textures  = textures;
        this.config    = config;
        this.log       = log;
        framework.Update += OnFramework;
    }

    private void OnFramework(IFramework fw) => Apply(objects.LocalPlayer?.Address ?? 0);

    private void Apply(nint addr)
    {
        // The locator ghost swaps these same slots. Two owners of one Texture** is how a texture gets freed
        // while the other still has it published, so while the ghost holds any of them this hands back
        // everything it owns and does nothing else — the locator is a deliberate, momentary act by the user
        // and outranks an ambient effect.
        bool ghostBusy = Ghost?.IsBusy == true;

        // Nothing anywhere asks for a coverage fade: skip the walk itself, which allocates a string per
        // texture on the character. This is the state almost every collection is in.
        bool on = config.LightResponseEnabled && LightFor != null && !ghostBusy
               && (AnyLight?.Invoke() ?? true);

        if (addr == 0) { _applied.Clear(); return; }
        if (!on && _applied.Count == 0) return;

        var seen = new HashSet<nint>();
        bool walked = ForEachShellNormal(addr, (slot, path, leaf) =>
        {
            seen.Add(slot);
            var cur = *(nint*)slot;

            var profile = on ? LightFor!(MaterialLeaf(leaf)) : null;
            int step = profile is { AnyHide: true } ? StepFor(slot, profile) : 0;

            if (step > 0)
            {
                bool haveApplied = _applied.TryGetValue(slot, out var ap);
                // Cheapest exit first: already showing this step, so there is nothing to build and — the
                // reason the order matters — no file to stat. GetOrBuild opens a FileInfo, and running it
                // before this check cost a disk stat per faded shell on every single frame.
                if (haveApplied && cur == ap.Ours && ap.Step == step) return;

                var f = GetOrBuild(path, step, profile!);
                var bgra = f.Bgra;
                if (bgra == null || f.Step != step) return;      // still building, or built for another step

                var tex = CreateTex(bgra, f.W, f.H);
                if (tex == 0) return;

                // Swap first, then release our PREVIOUS texture — and only when the slot still held
                // something we recognise. If it held neither, the model was rebuilt and the game already
                // freed ours; DecRef'ing would double-free. Exactly ShellNormalGhost's rule.
                var old = Interlocked.Exchange(ref *(nint*)slot, tex);
                if (haveApplied && (old == ap.Ours || old == ap.Original))
                    ((Texture*)ap.Ours)->DecRef();
                _applied[slot] = (tex, old == ap.Ours ? ap.Original : old, step);
            }
            else if (_applied.TryGetValue(slot, out var ap))
            {
                if (cur == ap.Ours)
                {
                    Interlocked.Exchange(ref *(nint*)slot, ap.Original);
                    ((Texture*)ap.Ours)->DecRef();
                }
                // Someone else's texture is in the slot — the ghost got there first and is holding ours as
                // the original it will one day write back. Drop the tracking WITHOUT freeing: leaking one
                // texture is recoverable, freeing one another owner still has published is not.
                _applied.Remove(slot);
            }
        });

        // Slots the walk didn't revisit had their model rebuilt: the game freed our texture already, so drop
        // the tracking WITHOUT a DecRef. Only prune when the walk actually ran.
        if (walked && _applied.Count > 0)
            foreach (var k in _applied.Keys.Where(k => !seen.Contains(k)).ToList())
                _applied.Remove(k);
    }

    /// <summary>
    /// Which fade step this shell is in, with a dead band so a light hovering on a boundary doesn't rebuild
    /// a texture back and forth. Step 0 means "leave the shell alone".
    /// </summary>
    private int StepFor(nint slot, ShellLightProfile profile)
    {
        float level = Math.Clamp(light.Sample(profile.ProbeHeight), 0f, 1f);
        float raw = level * Steps;
        int step = (int)MathF.Round(raw);

        if (_applied.TryGetValue(slot, out var ap) && ap.Step > 0
            && MathF.Abs(raw - ap.Step) < Hysteresis)
            step = ap.Step;

        return Math.Clamp(step, 0, Steps);
    }

    private Faded GetOrBuild(string path, int step, ShellLightProfile profile)
    {
        long stamp = FileStamp(path);
        var key = Normalize(path);
        // Reuse only while BOTH the step and the file are unchanged — a recomposite rewrites
        // ss_{letter}_norm.tex in place, and a stamp-only cache would then serve coverage from the previous
        // build. A changed step REPLACES the entry rather than joining it, which is what keeps this to one
        // full-resolution buffer per live shell.
        if (_built.TryGetValue(key, out var f) && f.Stamp == stamp && f.Step == step) return f;

        f = new Faded { Stamp = stamp, Step = step };
        _built[key] = f;
        var hide = (float[])profile.RowHide.Clone();   // snapshot: the publish can swap the profile mid-build
        float fade = 1f - step / (float)Steps;
        Task.Run(() => Build(f, path, hide, fade));
        return f;
    }

    private static long FileStamp(string path)
    {
        try { var fi = new FileInfo(path); return fi.LastWriteTimeUtc.Ticks ^ (fi.Length << 1); }
        catch { return 0; }
    }

    /// <summary>
    /// Decode the shell's normal and its index, then scale the normal's blue — the coverage — by
    /// <paramref name="fade"/> wherever the index selects a row that asked to hide. Off the framework thread.
    /// </summary>
    private void Build(Faded f, string normalPath, float[] rowHide, float fade)
    {
        try
        {
            var decNorm = textures.LoadTexAsRgba(normalPath);
            if (decNorm == null) { log.Warning("[ProteusLight] could not decode shell normal {0}", normalPath); return; }
            var (norm, w, h) = decNorm.Value;

            // The index sits beside the normal, written by the same pass: ss_{letter}_norm.tex → _id.tex.
            var idPath = normalPath[..^"_norm.tex".Length] + "_id.tex";
            var decId = textures.LoadTexAsRgba(idPath);
            byte[]? id = null;
            int idW = 0, idH = 0;
            if (decId is { } di) { (id, idW, idH) = di; }
            else
                // No index means the shell samples the fabricated (255, 255, 0) everywhere — row 16 sub-row
                // A — so the whole surface follows that one row. Common: it is what an overlay with no _id
                // art gets, which includes every Atramentum Luminis import.
                log.Debug("[ProteusLight] {0} has no index; fading against row 16A", Path.GetFileName(normalPath));

            var bgra = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int p = (y * w + x) * 4;

                    // Which colour-table row this texel reads, in the convention ContentIndexTexture decodes:
                    // red picks the pair, green blends A→B. Sampled nearest, since the index can be a
                    // different size from the normal and row regions are areas rather than detail.
                    int row = 30, sub = 0;   // the fabricated index's answer: row pair 16, sub-row A
                    if (id != null)
                    {
                        int q = ((y * idH / h) * idW + x * idW / w) * 4;
                        if (q + 1 < id.Length)
                        {
                            row = Math.Clamp((id[q] + 8) / 17, 0, 15) * 2;
                            sub = id[q + 1] >= 128 ? 0 : 1;
                        }
                    }
                    int cell = Math.Clamp(row + sub, 0, ShellLightProfile.RowCount - 1);
                    float keep = rowHide[cell] > 0f ? 1f - rowHide[cell] * (1f - fade) : 1f;

                    // Normal is RGBA here; blue (p+2) is the coverage gate. Scale it, keep RG (the actual
                    // normal) and A, then swizzle RGBA→BGRA for upload.
                    bgra[p]     = (byte)(norm[p + 2] * keep);
                    bgra[p + 1] = norm[p + 1];
                    bgra[p + 2] = norm[p];
                    bgra[p + 3] = norm[p + 3];
                }
            }

            f.W = w; f.H = h;
            f.Bgra = bgra;   // volatile publish
        }
        catch (Exception ex) { log.Error(ex, "[ProteusLight] coverage fade build failed for {0}", normalPath); }
    }

    /// <summary>The material leaf a shell texture belongs to: <c>ss_0_norm.tex</c> → <c>ss_0.mtrl</c>, which
    /// is the key the compositor publishes light profiles under.</summary>
    private static string MaterialLeaf(string textureLeaf)
        => textureLeaf[..^"_norm.tex".Length] + ".mtrl";

    // Walk the character's materials → normal textures that are OUR shell layers. Same shape as
    // ShellNormalGhost.ForEachShellNormal; returns false if the character isn't drawable this frame.
    private bool ForEachShellNormal(nint addr, Action<nint /*Texture** slot*/, string /*path*/, string /*leaf*/> visit)
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

                    bool underProteus =
                        name.IndexOf("/Proteus/textures/", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        name.IndexOf("\\Proteus\\textures\\", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!underProteus) continue;

                    var path = name.Trim().Trim('"');
                    var leaf = Path.GetFileName(path);
                    if (!leaf.StartsWith("ss_", StringComparison.OrdinalIgnoreCase)
                     || !leaf.EndsWith("_norm.tex", StringComparison.OrdinalIgnoreCase))
                        continue;

                    visit((nint)(&handle->Texture), path, leaf);
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

    private static string Normalize(string s) => s.Trim().Trim('"').Replace('\\', '/').ToLowerInvariant();

    public void Dispose()
    {
        framework.Update -= OnFramework;
        LightFor = null;                                  // makes the pass below a pure restore
        try { Apply(objects.LocalPlayer?.Address ?? 0); } // put every faded normal back
        catch (Exception ex) { log.Error(ex, "[ProteusLight] could not restore shell coverage on shutdown"); }
        _built.Clear();
    }
}
