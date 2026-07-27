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
/// Makes the second-skin gear shell layers stacked ABOVE a highlighted layer semi-transparent ("ghost")
/// so the colorset Glow locator on the lower layer is visible through them. A shell layer's per-pixel
/// transparency is its normal map's BLUE channel (SecondSkinService writes coverage there); this swaps the
/// live normal texture for a copy with blue scaled down, and restores it when the highlight clears.
///
/// Same live-GPU lifecycle as <see cref="SkinDiffuseGlow"/>: walk the character's materials each framework
/// tick, swap the target textures, and always restore — swap-first-then-DecRef, and prune (never DecRef)
/// slots the model rebuilt out from under us. Driven by the two highlighters (gear + skin), never directly
/// by the UI.
/// </summary>
public sealed unsafe class ShellNormalGhost : IDisposable
{
    // How much of the shell's opacity survives while ghosting (blue = coverage/opacity, 255 = opaque).
    private const float GhostFactor = 0.3f;

    private readonly IFramework framework;
    private readonly IObjectTable objects;
    private readonly TextureLoader textures;
    private readonly IPluginLog log;

    // Ghost every shell layer whose letter is GREATER than this. '`' (just below 'a') means "all layers"
    // (a skin target sits below every shell). Inactive when _active is false.
    private volatile bool _active;
    private volatile char _aboveLetter = (char)('a' - 1);

    // Ghosted normal bytes per shell disk path (decoded + blue-scaled off-thread, reused across frames).
    // Stamp = file write-time+length at build; a recomposite rewrites ss_{letter}_norm.tex in place, so a
    // path-only cache would serve stale transparency — reuse only while the stamp still matches. One entry
    // per path (a changed stamp replaces it), so the cache stays bounded to the live shell layers.
    private sealed class Ghosted { public volatile byte[]? Bgra; public int W, H; public long Stamp; }
    private readonly Dictionary<string, Ghosted> _ghostByPath = new(StringComparer.OrdinalIgnoreCase);

    // Currently-swapped slots: our created texture + the original we displaced. (Texture** address key.)
    private readonly Dictionary<nint, (nint Ours, nint Original)> _applied = new();

    public ShellNormalGhost(IFramework framework, IObjectTable objects, TextureLoader textures, IPluginLog log)
    {
        this.framework = framework;
        this.objects   = objects;
        this.textures  = textures;
        this.log       = log;
        framework.Update += OnFramework;
    }

    /// <summary>Ghost every shell layer stacked above <paramref name="targetLetter"/> (the ss_ letter of the
    /// highlighted gear layer); pass null for a skin target (below all shells → ghost every layer).</summary>
    public void GhostAbove(char? targetLetter)
    {
        // Sentinel must sort BELOW the lowest disk id ('0'), so a skin/no target ghosts every shell.
        _aboveLetter = targetLetter ?? (char)('0' - 1);
        _active = true;
    }

    public void Clear() => _active = false;

    private void OnFramework(IFramework fw) => Apply(objects.LocalPlayer?.Address ?? 0);

    private void Apply(nint addr)
    {
        if (addr == 0) { _applied.Clear(); return; }
        if (!_active && _applied.Count == 0) return;

        var seen = new HashSet<nint>();
        bool walked = ForEachShellNormal(addr, (slot, name, letter) =>
        {
            seen.Add(slot);
            var cur = *(nint*)slot;

            bool want = _active && letter > _aboveLetter;
            if (want)
            {
                bool haveApplied = _applied.TryGetValue(slot, out var ap);
                if (haveApplied && cur == ap.Ours) return;   // already ghosted with ours — nothing to do

                var g = GetOrBuild(name);
                var bgra = g.Bgra;
                if (bgra == null) return;                     // build not ready yet — leave it opaque
                var tex = CreateTex(bgra, g.W, g.H);
                if (tex == 0) return;

                // Swap first, then release our PREVIOUS texture — but only when the slot still held
                // something we recognise: our own last upload (normal reswap) or the true original the game
                // restored over it (a same-slot re-bind). If old is neither, the model rebuilt and the game
                // already freed our texture, so DON'T DecRef — that would double-free (see the prune below).
                var old = Interlocked.Exchange(ref *(nint*)slot, tex);
                if (haveApplied && (old == ap.Ours || old == ap.Original))
                    ((Texture*)ap.Ours)->DecRef();
                _applied[slot] = (tex, old == ap.Ours ? ap.Original : old);
            }
            else if (_applied.TryGetValue(slot, out var ap))
            {
                if (cur == ap.Ours)
                {
                    Interlocked.Exchange(ref *(nint*)slot, ap.Original);
                    ((Texture*)ap.Ours)->DecRef();
                }
                _applied.Remove(slot);
            }
        });

        // Slots the walk didn't revisit had their model rebuilt — the game already freed our texture, so
        // drop the tracking WITHOUT a DecRef (that would free twice). Only prune when the walk actually ran.
        if (walked && _applied.Count > 0)
            foreach (var k in _applied.Keys.Where(k => !seen.Contains(k)).ToList())
                _applied.Remove(k);
    }

    private Ghosted GetOrBuild(string diskName)
    {
        var key  = Normalize(diskName);
        var path = diskName.Trim().Trim('"');   // the live name is the resolved disk path
        long stamp = FileStamp(path);
        // Reuse only while the file is unchanged; a recomposite rewrites it in place. A stale-stamp entry is
        // replaced here (not appended), so the cache holds at most one entry per live shell path.
        if (_ghostByPath.TryGetValue(key, out var g) && g.Stamp == stamp) return g;
        g = new Ghosted { Stamp = stamp };
        _ghostByPath[key] = g;
        Task.Run(() => Build(g, path));
        return g;
    }

    // Write-time + length, so an in-place recomposite of the same path invalidates a cached ghost. 0 when
    // the file can't be stat'd (missing/mid-write) — a distinct value from any real stamp, forcing a rebuild.
    private static long FileStamp(string path)
    {
        try { var fi = new FileInfo(path); return fi.LastWriteTimeUtc.Ticks ^ (fi.Length << 1); }
        catch { return 0; }
    }

    private void Build(Ghosted g, string path)
    {
        try
        {
            var dec = textures.LoadTexAsRgba(path);
            if (dec == null) { log.Warning("[ShellGhost] could not decode normal {0}", path); return; }
            var (rgba, w, h) = dec.Value;
            var bgra = new byte[w * h * 4];
            for (int p = 0; p < bgra.Length; p += 4)
            {
                // Normal is RGBA here; blue (p+2) is the transparency gate. Scale it toward clear, keep RG
                // (the actual normal) and A, then swizzle RGBA→BGRA for upload.
                bgra[p]     = (byte)(rgba[p + 2] * GhostFactor);   // B ← scaled transparency
                bgra[p + 1] = rgba[p + 1];                         // G
                bgra[p + 2] = rgba[p];                             // R
                bgra[p + 3] = rgba[p + 3];                         // A
            }
            g.W = w; g.H = h;
            g.Bgra = bgra;   // volatile publish
        }
        catch (Exception ex) { log.Error(ex, "[ShellGhost] ghost build failed for {0}", path); }
    }

    // Walk the character's materials → normal textures that are OUR shell layers (\Proteus\textures\ +
    // ss_{letter}_norm.tex). Returns false if the character isn't drawable this frame.
    private bool ForEachShellNormal(nint addr, Action<nint /*Texture** slot*/, string /*name*/, char /*letter*/> visit)
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

                    // Shell layer normal: basename "ss_{letter}_norm.tex". Letter encodes stack order.
                    var baseName = Path.GetFileName(name.Trim().Trim('"'));
                    if (baseName.Length < 9 || !baseName.StartsWith("ss_", StringComparison.OrdinalIgnoreCase)
                        || !baseName.EndsWith("_norm.tex", StringComparison.OrdinalIgnoreCase))
                        continue;
                    char letter = char.ToLowerInvariant(baseName[3]);
                    if (!((letter >= '0' && letter <= '9') || (letter >= 'a' && letter <= 'z'))) continue;   // base-36 disk id

                    visit((nint)(&handle->Texture), name, letter);
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

    private static string Normalize(string s)
        => s.Trim().Trim('"').Replace('\\', '/').ToLowerInvariant();

    public void Dispose()
    {
        framework.Update -= OnFramework;
        _active = false;
        Apply(objects.LocalPlayer?.Address ?? 0);   // restore anything still ghosted
    }
}
