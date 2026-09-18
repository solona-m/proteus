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
/// Makes a dark-only region's opacity follow its glow, so where the light has taken the glow away only skin
/// is left.
/// <para>The light scales the shell normal's blue channel (its coverage), per colour-table row via the index
/// texture. Same swap-then-DecRef and prune-don't-free rules as <see cref="ShellNormalGhost"/>.</para>
/// <para>Quantised into a few steps with a dead band, since each step is a full texture rebuild.</para>
/// </summary>
public sealed unsafe class ShellCoverageFade : IDisposable
{
    /// <summary>
    /// How many steps the fade is cut into; each is a texture rebuild. Coarse is fine because the glow fade
    /// itself is continuous.
    /// </summary>
    private const int Steps = 8;

    /// <summary>
    /// How far (in steps) the light must move past the step it is showing before the step changes. Must be
    /// greater than 0.5, or rounding already covers it and the band does nothing.
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

    /// <summary>Whether any live shell asks for a light response at all; checked before the character is walked.</summary>
    public Func<bool>? AnyLight { get; set; }

    /// <summary>The locator ghost, which swaps these same slots; while <see cref="ShellNormalGhost.IsBusy"/>,
    /// this stands aside completely.</summary>
    public ShellNormalGhost? Ghost { get; set; }

    /// <summary>One shell's faded coverage, built off-thread and reused until the step or the file changes.</summary>
    private sealed class Faded
    {
        public volatile byte[]? Bgra;
        public int W, H;
        public long Stamp;
        public int Step;
    }

    // One entry per shell, not per (shell, step): each holds a full-resolution BGRA buffer.
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
        // Never two owners of one Texture**: while the ghost holds any slot, hand back everything we own.
        bool ghostBusy = Ghost?.IsBusy == true;

        // Nothing asks for a coverage fade: skip the walk, which allocates a string per texture.
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
                // Already showing this step: exit before GetOrBuild, which stats the file.
                if (haveApplied && cur == ap.Ours && ap.Step == step) return;

                var f = GetOrBuild(path, step, profile!);
                var bgra = f.Bgra;
                if (bgra == null || f.Step != step) return;      // still building, or built for another step

                var tex = CreateTex(bgra, f.W, f.H);
                if (tex == 0) return;

                // Swap first, then release our previous texture only when the slot still held something we
                // recognise; otherwise the game already freed it and a DecRef would double-free.
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
                // Someone else's texture is in the slot: drop the tracking without freeing, since another
                // owner may still publish ours.
                _applied.Remove(slot);
            }
        });

        // Slots the walk didn't revisit had their model rebuilt: the game freed our texture already, so drop
        // the tracking WITHOUT a DecRef. Only prune when the walk actually ran.
        if (walked && _applied.Count > 0)
            foreach (var k in _applied.Keys.Where(k => !seen.Contains(k)).ToList())
                _applied.Remove(k);
    }

    /// <summary>Which fade step this shell is in, with a dead band. Step 0 means "leave the shell alone".</summary>
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
        // Keyed by the shell, not the file name: content-addressed normals get a new name per revision.
        var key = Normalize(Services.ShellTextureNames.ShellKey(path));
        // Reuse only while both the step and the file stamp are unchanged; a changed step replaces the entry.
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

            // The index sits beside the normal; named through ShellTextureNames because names may be content-addressed.
            var idPath = Services.ShellTextureNames.IndexBeside(normalPath);
            var decId = textures.LoadTexAsRgba(idPath);
            byte[]? id = null;
            int idW = 0, idH = 0;
            if (decId is { } di) { (id, idW, idH) = di; }
            else
                // No index: the shell samples the fabricated (255, 255, 0), so the whole surface follows row 16A.
                log.Debug("[ProteusLight] {0} has no index; fading against row 16A", Path.GetFileName(normalPath));

            var bgra = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int p = (y * w + x) * 4;

                    // The row this texel reads (ContentIndexTexture's convention: red picks the pair, green A→B),
                    // sampled nearest since the index may differ in size.
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

                    // Scale blue (the coverage gate), keep RG and A, and swizzle RGBA→BGRA for upload.
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
        => Services.ShellTextureNames.MaterialLeaf(textureLeaf);

    // Walk the character's materials → normal textures of our shell layers; false if not drawable this frame.
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
                    // Either form of a shell normal's name — see ShellTextureNames.
                    if (!Services.ShellTextureNames.TryNormalStem(leaf, out _))
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
