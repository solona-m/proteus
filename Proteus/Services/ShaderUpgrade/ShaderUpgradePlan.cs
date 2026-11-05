using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Proteus.Services;

/// <summary>The two legacy material layouts the gear shader upgrade recognises.</summary>
internal enum LegacyForm
{
    /// <summary>Form A: normal + mask + index, the gear (default) texture mode.</summary>
    IndexMask,
    /// <summary>Form B: diffuse + normal + specular-in-the-mask-slot + index, the "compatibility" texture mode.</summary>
    Diffuse,
    /// <summary>Any other sampler set or mode: left alone.</summary>
    Unsupported,
}

/// <summary>How the derived Dawntrail mask's green (roughness) channel is filled for Form B. Session-only.</summary>
internal enum MaskGloss
{
    /// <summary>255 − luma(s), at least 1: shiny where the old specular was strong. Fails matte, never mirror-like.</summary>
    InverseSpecular,
    /// <summary>A low constant (26 ≈ 0.1), so the rows' roughness decides.</summary>
    LowConstant,
    /// <summary>A mid constant (128 ≈ 0.5), the native masks' median.</summary>
    MidConstant,
}

/// <summary>Why a worn legacy material is left on the old shader.</summary>
internal enum LeftReason { OldTable, UnsupportedForm, Unreadable, Outranked, Unresolved, Compositor, WriteFailed }

/// <summary>
/// The gear shader upgrade's policy, with no game, Penumbra or file access of its own: every decision the
/// <see cref="ShaderUpgradeWatcher"/> takes lives here so it can be tested.
/// <para/>
/// <b>Research</b> (the game install, the user's mod library and the TexTools framework, read for the plan):
/// <list type="bullet">
/// <item>Samplers (CRC-32 of the names): <c>g_SamplerDiffuse</c> 0x115306BE, Normal 0x0C5EC1F1, Mask 0x8A4E82B6,
/// Index 0x565F8FD8. Neither shader has a specular sampler: legacy diffuse materials bind their <c>_s</c> to the
/// mask sampler.</item>
/// <item>Texture Mode key 0xB616DC5A: 0x5CC605B5 (default, both packages) or 0x600EF9DF (compatibility, the diffuse
/// mode the game's monster and demihuman materials use). Legacy Specular Mode key 0xC8BD1DEF: 0x198D11CD (the mask
/// sampler is a specular map, the default) or 0xA02F4828.</item>
/// <item>The game's own conversions of legacy diffuse materials to <c>character.shpk</c> (m0075, m0885, m0886) keep
/// the compatibility mode and every binding; their <c>_s</c> files keep the name but hold a real Dawntrail mask
/// (independent channels, blue ≈ 1). Mods' <c>_s</c> maps are grey legacy specular maps (R = G = B, means 0.07–0.39),
/// so Form B gets a derived mask rather than its <c>_s</c>.</item>
/// <item>The library: 3,397 Form A, 2,083 Form B, 5 sixteen-row tables, 3 odd layouts, of 5,488 legacy materials.</item>
/// </list>
/// </summary>
internal static class ShaderUpgradePlan
{
    public const uint TextureModeKey = 0xB616DC5A, TextureModeDefault = 0x5CC605B5, TextureModeCompatibility = 0x600EF9DF;
    public const uint SpecularModeKey = 0xC8BD1DEF, SpecularModeDefault = 0x198D11CD, SpecularModeMask = 0xA02F4828;
    public const uint SamplerDiffuse = 0x115306BE, SamplerNormal = 0x0C5EC1F1, SamplerMask = 0x8A4E82B6,
                      SamplerIndex = 0x565F8FD8;

    /// <summary>Root of the invented game paths the derived masks are served at. Under Proteus's own namespace.</summary>
    public const string MaskGameRoot = CompositorService.OwnedTextureRoot + "shaderupgrade/";

    /// <summary>A derived mask is halved until both its sides are at most this.</summary>
    public const int MaxMaskSide = 2048;

    /// <summary>How many replaced masks are kept, since the game may still be reading one.</summary>
    public const int MaskTail = 4;

    public const string Tag = "Proteus shader upgrade";

    /// <summary>Above every regular mod (the managed mod can be raised), below the live brush's <c>int.MaxValue</c>.</summary>
    public const int Priority = int.MaxValue - 1;

    // ── which files ──────────────────────────────────────────────────────────

    private static readonly Regex GearMaterialRe = new(
        @"^chara/(?:equipment/e\d{4}/material/v\d{4}/mt_c\d{4}e\d{4}_(?:met|top|glv|dwn|sho)_[^/]*"
      + @"|accessory/a\d{4}/material/v\d{4}/mt_c\d{4}a\d{4}_(?:ear|nek|wrs|rir|ril)_[^/]*)\.mtrl$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>An equipment or accessory material's game path — nothing else is upgraded.</summary>
    public static bool IsGearMaterial(string gamePath) => GearMaterialRe.IsMatch(gamePath.Replace('\\', '/'));

    /// <summary>
    /// The worn gear materials in a resource map (resolved file → game paths): game path → the file it was loaded from.
    /// Null when there is no map or it has no body-slot model, which is a character mid-rebuild, not a bare one.
    /// </summary>
    public static Dictionary<string, string>? GearMaterials(Dictionary<string, HashSet<string>>? resourceMap)
    {
        if (resourceMap == null) return null;
        var models = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var gamePaths in resourceMap.Values)
            foreach (var g in gamePaths)
                if (g.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase)) models.Add(g);
        if (!DrawnModelPaths.HasBodySlotModel(models)) return null;

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (resolved, gamePaths) in resourceMap)
            foreach (var g in gamePaths)
                if (IsGearMaterial(g)) result[g] = resolved;
        return result;
    }

    // ── where a file comes from ──────────────────────────────────────────────

    internal enum Source { Mod, Game, Own, Copy, Outside }

    /// <summary>
    /// Where a resolved upstream comes from: game data (nothing resolved, an echo of the game path, or not a rooted
    /// path), the compositor's own output, one of our own copies, a mod under the mods root, or somewhere else.
    /// </summary>
    public static Source Classify(string gamePath, string? upstream, string? modsRoot, string? managedRoot)
    {
        if (string.IsNullOrEmpty(upstream) || string.Equals(upstream, gamePath, StringComparison.OrdinalIgnoreCase)
            || !Path.IsPathRooted(StripPenumbraPrefix(upstream)))
            return Source.Game;
        var file = StripPenumbraPrefix(upstream);
        if (ManagedModPaths.IsShaderUpgradeCopy(file, managedRoot)) return Source.Copy;
        if (ManagedModPaths.IsPublishedOutput(file, managedRoot)) return Source.Own;
        if (!string.IsNullOrEmpty(modsRoot) && HatCompatService.InMods(file, modsRoot, out _, out _)) return Source.Mod;
        return Source.Outside;
    }

    /// <summary>Penumbra spells some resolved files <c>|…|path</c>; the file is what follows the second bar.</summary>
    public static string StripPenumbraPrefix(string path)
    {
        if (path.Length > 0 && path[0] == '|')
        {
            int end = path.IndexOf('|', 1);
            if (end > 0) return path[(end + 1)..];
        }
        return path;
    }

    /// <summary>"modDir/rel" for a mod file, for <c>/state</c> and the log; the file itself otherwise.</summary>
    public static string SourceLabel(string upstream, string? modsRoot)
        => !string.IsNullOrEmpty(modsRoot) && HatCompatService.InMods(StripPenumbraPrefix(upstream), modsRoot,
                                                                       out var root, out var rel)
            ? $"{Path.GetFileName(root)}/{rel}"
            : upstream;

    // ── material reading ────────────────────────────────────────────────────

    /// <summary>The texture path strings, by texture index, with a leading <c>--</c> marker stripped. Null when unwalkable.</summary>
    public static string[]? TexturePaths(byte[] b)
    {
        if (b.Length < 16) return null;
        int strSize = BitConverter.ToUInt16(b, 8), texCount = b[12];
        int stringsAt = 16 + 4 * (b[12] + b[13] + b[14]);
        int stringsEnd = stringsAt + strSize;
        if (stringsEnd > b.Length) return null;
        var paths = new string[texCount];
        for (int i = 0; i < texCount; i++)
        {
            int at = stringsAt + BitConverter.ToUInt16(b, 16 + i * 4);
            if (at >= stringsEnd) return null;
            int end = at;
            while (end < stringsEnd && b[end] != 0) end++;
            var p = Encoding.UTF8.GetString(b, at, end - at);
            paths[i] = p.StartsWith("--", StringComparison.Ordinal) ? p[2..] : p;
        }
        return paths;
    }

    private static bool Header(byte[] b, out int matHeader, out int keyCount, out int constCount, out int sampCount)
    {
        matHeader = keyCount = constCount = sampCount = 0;
        if (b.Length < 16) return false;
        int dataSet = BitConverter.ToUInt16(b, 6), strSize = BitConverter.ToUInt16(b, 8);
        matHeader = 16 + 4 * (b[12] + b[13] + b[14]) + strSize + b[15] + dataSet;
        if (matHeader + 12 > b.Length) return false;
        keyCount = BitConverter.ToUInt16(b, matHeader + 2);
        constCount = BitConverter.ToUInt16(b, matHeader + 4);
        sampCount = BitConverter.ToUInt16(b, matHeader + 6);
        return matHeader + 12 + keyCount * 8 + constCount * 8 + sampCount * 12 <= b.Length;
    }

    /// <summary>The shader keys, key → value. Null when unwalkable.</summary>
    public static Dictionary<uint, uint>? Keys(byte[] b)
    {
        if (!Header(b, out int mh, out int kc, out _, out _)) return null;
        var keys = new Dictionary<uint, uint>();
        for (int i = 0; i < kc; i++)
            keys[BitConverter.ToUInt32(b, mh + 12 + i * 8)] = BitConverter.ToUInt32(b, mh + 16 + i * 8);
        return keys;
    }

    /// <summary>The samplers, (sampler id, texture index). Null when unwalkable.</summary>
    public static List<(uint Id, int TextureIndex)>? Samplers(byte[] b)
    {
        if (!Header(b, out int mh, out int kc, out int cc, out int sc)) return null;
        int at = mh + 12 + kc * 8 + cc * 8;
        var list = new List<(uint, int)>(sc);
        for (int i = 0; i < sc; i++) list.Add((BitConverter.ToUInt32(b, at + i * 12), b[at + i * 12 + 8]));
        return list;
    }

    /// <summary>A constant's float values, or null when the material does not declare it.</summary>
    public static float[]? Constant(byte[] b, uint id)
    {
        if (!Header(b, out int mh, out int kc, out int cc, out int sc)) return null;
        int constBase = mh + 12 + kc * 8;
        int svBase = constBase + cc * 8 + sc * 12;
        for (int i = 0; i < cc; i++)
        {
            int e = constBase + i * 8;
            if (BitConverter.ToUInt32(b, e) != id) continue;
            int off = BitConverter.ToUInt16(b, e + 4), size = BitConverter.ToUInt16(b, e + 6);
            if (svBase + off + size > b.Length) return null;
            var v = new float[size / 4];
            for (int k = 0; k < v.Length; k++) v[k] = BitConverter.ToSingle(b, svBase + off + k * 4);
            return v;
        }
        return null;
    }

    /// <summary>Which layout a legacy material has. Unsupported when it can't be walked.</summary>
    public static LegacyForm FormOf(byte[] mtrl)
    {
        var samplers = Samplers(mtrl);
        var keys = Keys(mtrl);
        if (samplers == null || keys == null) return LegacyForm.Unsupported;
        var ids = samplers.Select(s => s.Id).ToHashSet();
        keys.TryGetValue(TextureModeKey, out var mode);
        bool hasMode = keys.ContainsKey(TextureModeKey);

        if (ids.SetEquals([SamplerIndex, SamplerMask, SamplerNormal]) && samplers.Count == 3
            && (!hasMode || mode == TextureModeDefault))
            return LegacyForm.IndexMask;

        if (ids.SetEquals([SamplerDiffuse, SamplerIndex, SamplerMask, SamplerNormal]) && samplers.Count == 4
            && hasMode && mode == TextureModeCompatibility
            && (!keys.TryGetValue(SpecularModeKey, out var spec) || spec == SpecularModeDefault))
            return LegacyForm.Diffuse;

        return LegacyForm.Unsupported;
    }

    /// <summary>The texture path the mask sampler binds, or null.</summary>
    public static string? MaskSlotPath(byte[] mtrl)
    {
        var samplers = Samplers(mtrl);
        var paths = TexturePaths(mtrl);
        if (samplers == null || paths == null) return null;
        foreach (var (id, index) in samplers)
            if (id == SamplerMask && index < paths.Length) return paths[index];
        return null;
    }

    /// <summary>
    /// The colour table is the pre-Dawntrail 16-row layout (a table flagged, but not 32×32). Only a table the existing
    /// recipe refuses for its shape is called old; anything else it refuses is unreadable.
    /// </summary>
    public static bool IsOldTable(byte[] mtrl)
    {
        if (mtrl.Length < 16) return false;
        int tableAt = GearMaterialWriter.ColorTableStart(mtrl);
        int additional = mtrl[15];
        if (tableAt < 0 || additional < 4 || tableAt - additional < 0) return false;
        uint flags = BitConverter.ToUInt32(mtrl, tableAt - additional);
        return (flags & 0x4) != 0 && (((flags >> 4) & 0xF) != 3 || ((flags >> 8) & 0xF) != 5);
    }

    // ── Form B's derived mask ───────────────────────────────────────────────

    /// <summary>The source's size, halved (keeping the aspect) until both sides are at most <see cref="MaxMaskSide"/>.</summary>
    public static (int W, int H) CapSize(int w, int h)
    {
        while ((w > MaxMaskSide || h > MaxMaskSide) && w > 1 && h > 1) { w /= 2; h /= 2; }
        return (Math.Max(1, w), Math.Max(1, h));
    }

    /// <summary>Rec. 709 luma of an RGBA8 pixel, rounded.</summary>
    public static byte Luma(byte r, byte g, byte b) => (byte)Math.Clamp((int)MathF.Round(0.2126f * r + 0.7152f * g + 0.0722f * b), 0, 255);

    /// <summary>The green a strategy gives for a specular value.</summary>
    public static byte GlossFor(MaskGloss gloss, byte s) => gloss switch
    {
        MaskGloss.LowConstant => 26,
        MaskGloss.MidConstant => 128,
        _ => (byte)Math.Max(1, 255 - s),
    };

    /// <summary>
    /// A Dawntrail mask from a legacy specular map (RGBA8, already at its capped size): R = luma(s) (specular, as the
    /// legacy shader multiplied it), G by <paramref name="gloss"/> (roughness), B = 255 (no occlusion), A = 255.
    /// </summary>
    public static byte[] DeriveMask(byte[] rgba, MaskGloss gloss)
    {
        var o = new byte[rgba.Length];
        for (int i = 0; i + 3 < rgba.Length; i += 4)
        {
            byte s = Luma(rgba[i], rgba[i + 1], rgba[i + 2]);
            o[i] = s;
            o[i + 1] = GlossFor(gloss, s);
            o[i + 2] = 255;
            o[i + 3] = 255;
        }
        return o;
    }

    /// <summary>Decode-capped and derived: the mask a native-size specular map gives.</summary>
    public static (byte[] Rgba, int W, int H) MaskFrom(byte[] rgba, int w, int h, MaskGloss gloss)
    {
        var (cw, ch) = CapSize(w, h);
        var src = cw == w && ch == h ? rgba : UVRemapService.ResizeBilinear(rgba, w, h, cw, ch);
        return (DeriveMask(src, gloss), cw, ch);
    }

    /// <summary>The first 16 hex digits of SHA-256, lowercase.</summary>
    public static string Hash16(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes))[..16];

    /// <summary>The content address of a derived mask: its pixels, size, strategy and encoding.</summary>
    public static string MaskHash(byte[] rgba, int w, int h, MaskGloss gloss, TexEncoding encoding)
    {
        var tail = Encoding.ASCII.GetBytes($"|{w}x{h}|{gloss}|{encoding}");
        var all = new byte[rgba.Length + tail.Length];
        rgba.CopyTo(all, 0);
        tail.CopyTo(all, rgba.Length);
        return Hash16(all);
    }

    /// <summary>The invented game path a mask is served at: lowercase ASCII only.</summary>
    public static string MaskGamePath(string hash16) => MaskGameRoot + hash16.ToLowerInvariant() + "_mask.tex";

    public static string MaskName(int pid, string hash16) => $"{pid}-{hash16.ToLowerInvariant()}_mask.tex";

    /// <summary>A material copy's file name: the writing process and the content's hash.</summary>
    public static string CopyName(int pid, byte[] bytes) => $"{pid}-{Hash16(bytes)}.mtrl";

    // ── the memo ────────────────────────────────────────────────────────────

    /// <summary>
    /// Derived masks by what they came from, for the session: an unchanged walk decodes nothing. The key is the source
    /// file's identity (path, length, last write; the game path for a vanilla <c>_s</c>), the strategy, the encoding and
    /// the cap. Thread-safe.
    /// </summary>
    internal sealed class MaskMemo
    {
        public readonly record struct Key(string Source, long Length, long Ticks, MaskGloss Gloss, TexEncoding Encoding,
                                          int Cap);

        public readonly record struct Entry(string MaskName, string MaskGamePath, int Width, int Height);

        private readonly Dictionary<Key, Entry> map = [];
        private readonly object sync = new();

        public static Key KeyFor(string? sDisk, string sGamePath, Func<string, (long Length, long Ticks)?> stat,
                                 MaskGloss gloss, TexEncoding encoding)
        {
            if (sDisk != null && stat(sDisk) is { } s)
                return new Key(sDisk.ToLowerInvariant(), s.Length, s.Ticks, gloss, encoding, MaxMaskSide);
            return new Key("game:" + sGamePath.ToLowerInvariant(), 0, 0, gloss, encoding, MaxMaskSide);
        }

        public bool TryGet(Key key, out Entry entry)
        {
            lock (sync) return map.TryGetValue(key, out entry);
        }

        public void Put(Key key, Entry entry)
        {
            lock (sync) map[key] = entry;
        }

        public void Evict(Key key)
        {
            lock (sync) map.Remove(key);
        }

        /// <summary>Drop every entry naming this mask file.</summary>
        public void EvictMask(string maskName)
        {
            lock (sync)
                foreach (var k in map.Where(kv => string.Equals(kv.Value.MaskName, maskName,
                                                                StringComparison.OrdinalIgnoreCase))
                                     .Select(kv => kv.Key).ToList())
                    map.Remove(k);
        }

        /// <summary>Drop every entry whose source lies under <paramref name="modRoot"/> (a mod evicted or edited).</summary>
        public void EvictUnder(string modRoot)
        {
            var root = modRoot.TrimEnd('/', '\\').ToLowerInvariant();
            lock (sync)
                foreach (var k in map.Keys.Where(k => k.Source.StartsWith(root + "\\", StringComparison.Ordinal)
                                                   || k.Source.StartsWith(root + "/", StringComparison.Ordinal))
                                          .ToList())
                    map.Remove(k);
        }

        public void Clear()
        {
            lock (sync) map.Clear();
        }

        public int Count { get { lock (sync) return map.Count; } }
    }

    // ── building ────────────────────────────────────────────────────────────

    /// <summary>A worn gear material, with what its game path resolves to under the player's mods.</summary>
    public readonly record struct Worn(string GamePath, string? Upstream);

    /// <summary>A derived mask: where it is served and stored, and its pixels when they still have to be written.</summary>
    public sealed record MaskSpec(string GamePath, string CopyName, int Width, int Height, byte[]? Rgba, MaskMemo.Key Key);

    /// <summary>
    /// One worn material's upgrade: its game path, the upstream it was made from, the copy, and for Form B its mask.
    /// Applied, dropped and verified as one.
    /// </summary>
    public sealed record Unit(string MaterialGamePath, string Upstream, string SourceLabel, LegacyForm Form,
                              byte[] MaterialBytes, string MaterialCopyName, MaskSpec? Mask);

    public readonly record struct Left(string GamePath, string Source, LeftReason Reason);

    public sealed record Plan(List<Unit> Units, List<Left> Left, int Decodes, int MemoHits);

    /// <summary>Everything <see cref="Build"/> reads, injected so it needs no game or disk.</summary>
    public sealed class Inputs
    {
        public required string? ModsRoot { get; init; }
        public required string ManagedRoot { get; init; }
        public required int Pid { get; init; }
        public required Func<string, byte[]?> Read { get; init; }
        /// <summary>The resolved file of a specular map's game path (null: game data).</summary>
        public required Func<string, string?> ResolveTexture { get; init; }
        /// <summary>Decode a texture at its native size: (resolved file or null, game path) → RGBA8.</summary>
        public required Func<string?, string, (byte[] Rgba, int W, int H)?> DecodeNative { get; init; }
        public required Func<string, (long Length, long Ticks)?> Stat { get; init; }
        /// <summary>Whether a mask copy of this name exists and is complete.</summary>
        public required Func<string, bool> MaskFileOk { get; init; }
        public required MaskMemo Memo { get; init; }
        public MaskGloss Gloss { get; init; } = MaskGloss.InverseSpecular;
        public TexEncoding Encoding { get; init; } = TexEncoding.Uncompressed;
        /// <summary>Game paths the compositor publishes.</summary>
        public IReadOnlySet<string> Published { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The upgrades for what is worn: one unit per upgradable material game path, a reason for every legacy one left
    /// alone, and nothing for materials that are not legacy, not from a mod, or not gear.
    /// </summary>
    public static Plan Build(IEnumerable<Worn> worn, Inputs inp)
    {
        var units = new List<Unit>();
        var left = new List<Left>();
        int decodes = 0, hits = 0;
        // One read and one upgrade per upstream: several game paths may share a file.
        var made = new Dictionary<string, (Unit? Unit, Left? Left)>(StringComparer.OrdinalIgnoreCase);

        foreach (var w in worn)
        {
            if (!IsGearMaterial(w.GamePath)) continue;
            if (inp.Published.Contains(w.GamePath))
            {
                left.Add(new Left(w.GamePath, w.Upstream ?? w.GamePath, LeftReason.Compositor));
                continue;
            }
            var src = Classify(w.GamePath, w.Upstream, inp.ModsRoot, inp.ManagedRoot);
            if (src == Source.Own)
            {
                left.Add(new Left(w.GamePath, w.Upstream!, LeftReason.Compositor));
                continue;
            }
            if (src != Source.Mod) continue;
            var upstream = StripPenumbraPrefix(w.Upstream!);
            var label = SourceLabel(upstream, inp.ModsRoot);

            if (!made.TryGetValue(upstream, out var result))
            {
                result = Make(upstream, label, inp, ref decodes, ref hits);
                made[upstream] = result;
            }

            if (result.Unit is { } u) units.Add(u with { MaterialGamePath = w.GamePath });
            else if (result.Left is { } l) left.Add(l with { GamePath = w.GamePath });
        }
        return new Plan(units, left, decodes, hits);
    }

    private static byte[]? Read(Inputs inp, string path)
    {
        try { return inp.Read(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static (Unit?, Left?) Make(string upstream, string label, Inputs inp, ref int decodes, ref int hits)
    {
        var bytes = Read(inp, upstream);
        if (bytes == null) return (null, new Left("", label, LeftReason.Unreadable));
        if (!string.Equals(CharacterShaderUpgrade.ShaderOf(bytes), CharacterShaderUpgrade.Legacy,
                           StringComparison.OrdinalIgnoreCase))
            return (null, null);   // not legacy: untouched, not counted

        var form = FormOf(bytes);
        if (form == LegacyForm.Unsupported)
            return (null, new Left("", label, Samplers(bytes) == null ? LeftReason.Unreadable : LeftReason.UnsupportedForm));

        var up = CharacterShaderUpgrade.Upgrade(bytes);
        if (up == null) return (null, new Left("", label, IsOldTable(bytes) ? LeftReason.OldTable : LeftReason.Unreadable));
        up = WindNeutral(up);
        if (up == null) return (null, new Left("", label, LeftReason.Unreadable));

        MaskSpec? mask = null;
        if (form == LegacyForm.Diffuse)
        {
            var slot = MaskSlotPath(bytes);
            if (slot == null) return (null, new Left("", label, LeftReason.Unreadable));
            // The compositor publishes this specular map (a gear overlay): a derived mask would freeze it.
            if (inp.Published.Contains(slot)) return (null, new Left("", label, LeftReason.Compositor));
            mask = MaskFor(slot, inp, ref decodes, ref hits);
            if (mask == null) return (null, new Left("", label, LeftReason.Unreadable));
            up = TextureLoader.RetargetTexturePaths(up, new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [slot] = mask.GamePath,
            });
            if (up == null) return (null, new Left("", label, LeftReason.Unreadable));
        }

        if (!IsSound(bytes, up, form, mask?.GamePath)) return (null, new Left("", label, LeftReason.Unreadable));
        return (new Unit("", upstream, label, form, up, CopyName(inp.Pid, up), mask), null);
    }

    private static MaskSpec? MaskFor(string slot, Inputs inp, ref int decodes, ref int hits)
    {
        var sDisk = inp.ResolveTexture(slot);
        if (sDisk != null && string.Equals(sDisk, slot, StringComparison.OrdinalIgnoreCase)) sDisk = null;
        if (sDisk != null) sDisk = StripPenumbraPrefix(sDisk);
        var key = MaskMemo.KeyFor(sDisk, slot, inp.Stat, inp.Gloss, inp.Encoding);
        if (inp.Memo.TryGet(key, out var hit))
        {
            if (inp.MaskFileOk(hit.MaskName))
            {
                hits++;
                return new MaskSpec(hit.MaskGamePath, hit.MaskName, hit.Width, hit.Height, null, key);
            }
            inp.Memo.Evict(key);
        }

        decodes++;
        var decoded = inp.DecodeNative(sDisk, slot);
        if (decoded is not { } d || d.W <= 0 || d.H <= 0 || d.Rgba.Length < d.W * d.H * 4) return null;
        var (rgba, w, h) = MaskFrom(d.Rgba, d.W, d.H, inp.Gloss);
        if (!IsSoundMaskBuffer(rgba, w, h, d.Rgba, d.W, d.H, inp.Gloss)) return null;
        var hash = MaskHash(rgba, w, h, inp.Gloss, inp.Encoding);
        var spec = new MaskSpec(MaskGamePath(hash), MaskName(inp.Pid, hash), w, h, rgba, key);
        return spec;
    }

    /// <summary>Both movement constants at 0, so upgraded legacy gear never starts to sway. Null when impossible.</summary>
    public static byte[]? WindNeutral(byte[] mtrl)
    {
        var (a, okA) = TextureLoader.EnsureConstantValues(mtrl, MeshVolumeService.VertexMovementScaleId, 0f);
        if (!okA) return null;
        var (b, okB) = TextureLoader.EnsureConstantValues(a, MeshVolumeService.VertexMovementMaxLengthId, 0f);
        return okB ? b : null;
    }

    // ── gates ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The <c>.mtrl</c> gate: everything a copy must be before it is written. <c>character.shpk</c>; the header size
    /// equals the length; it parses; the texture strings equal the source's, except Form B's mask entry, which is the
    /// invented path exactly; the sampler bindings and the Texture Mode are unchanged; both movement constants are 0.
    /// </summary>
    public static bool IsSound(byte[] source, byte[] upgraded, LegacyForm form, string? maskGamePath)
    {
        try
        {
            if (!string.Equals(CharacterShaderUpgrade.ShaderOf(upgraded), CharacterShaderUpgrade.Character,
                               StringComparison.OrdinalIgnoreCase))
                return false;
            if (BitConverter.ToUInt16(upgraded, 4) != upgraded.Length) return false;
            if (TextureLoader.GetMtrlInfo(upgraded).shader != CharacterShaderUpgrade.Character) return false;

            var before = TexturePaths(source);
            var after = TexturePaths(upgraded);
            var sb = Samplers(source);
            var sa = Samplers(upgraded);
            if (before == null || after == null || sb == null || sa == null) return false;
            if (before.Length != after.Length || !sb.SequenceEqual(sa)) return false;

            int maskIndex = sb.FirstOrDefault(s => s.Id == SamplerMask, (Id: 0u, TextureIndex: -1)).TextureIndex;
            for (int i = 0; i < before.Length; i++)
            {
                if (form == LegacyForm.Diffuse && i == maskIndex)
                {
                    if (maskGamePath == null || !string.Equals(after[i], maskGamePath, StringComparison.Ordinal))
                        return false;
                }
                else if (!string.Equals(before[i], after[i], StringComparison.Ordinal)) return false;
            }

            var kb = Keys(source);
            var ka = Keys(upgraded);
            if (kb == null || ka == null) return false;
            kb.TryGetValue(TextureModeKey, out var mb);
            ka.TryGetValue(TextureModeKey, out var ma);
            if (kb.ContainsKey(TextureModeKey) != ka.ContainsKey(TextureModeKey) || mb != ma) return false;

            return Constant(upgraded, MeshVolumeService.VertexMovementScaleId) is [0f, ..]
                && Constant(upgraded, MeshVolumeService.VertexMovementMaxLengthId) is [0f, ..];
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException) { return false; }
    }

    /// <summary>
    /// The mask gate before encoding: the planned size, B and A at 255, R the source's luma within 1 (at the source
    /// size; a resized mask is checked only against the strategy), G the strategy's within 1.
    /// </summary>
    public static bool IsSoundMaskBuffer(byte[] mask, int w, int h, byte[] source, int sw, int sh, MaskGloss gloss)
    {
        if ((w, h) != CapSize(sw, sh) || mask.Length != w * h * 4) return false;
        bool sameSize = w == sw && h == sh;
        for (int i = 0; i < mask.Length; i += 4)
        {
            if (mask[i + 2] != 255 || mask[i + 3] != 255) return false;
            if (sameSize && Math.Abs(mask[i] - Luma(source[i], source[i + 1], source[i + 2])) > 1) return false;
            if (Math.Abs(mask[i + 1] - GlossFor(gloss, mask[i])) > 1) return false;
        }
        return true;
    }

    /// <summary>
    /// The mask gate after writing: B8G8R8A8 (0x1450) or BC7 (0x6432), one mip, the planned size or 16×16 (a solid
    /// colour is shrunk), and complete.
    /// </summary>
    public static bool IsSoundMaskHeader(byte[] header, long fileLength, int w, int h)
    {
        if (header.Length < 16) return false;
        uint format = BitConverter.ToUInt32(header, 4);
        int fw = BitConverter.ToUInt16(header, 8), fh = BitConverter.ToUInt16(header, 10);
        int mips = header[14];
        if (format is not (0x1450u or 0x6432u) || mips != 1) return false;
        if (!((fw == w && fh == h) || (fw == 16 && fh == 16))) return false;
        long payload = format == 0x1450u ? (long)fw * fh * 4 : (long)fw * fh;
        return fileLength == 80 + payload;
    }

    // ── files ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Copies nothing can still be drawing: from a game no longer running. This process's are kept (a temporary mod from
    /// an earlier plugin load in this game may still point at them), as are other running games'. Names not in the
    /// <c>pid-hash</c> form are not ours to judge.
    /// </summary>
    public static IEnumerable<string> StaleCopies(IEnumerable<string> files, int ownPid, Func<int, bool> alive)
    {
        foreach (var f in files)
        {
            var name = Path.GetFileName(f);
            int dash = name.IndexOf('-');
            if (dash <= 0 || !int.TryParse(name[..dash], out var pid)) continue;
            if (pid != ownPid && !alive(pid)) yield return f;
        }
    }

    /// <summary>
    /// This process's mask files that no held collection references and that are not among the recently replaced ones.
    /// Other processes' masks and every material copy are left alone.
    /// </summary>
    public static List<string> MasksToPrune(int ownPid, IEnumerable<string> files, IReadOnlySet<string> referenced,
                                            IReadOnlyCollection<string> tail)
    {
        var prefix = ownPid + "-";
        return files.Where(f =>
            {
                var name = Path.GetFileName(f);
                return name.StartsWith(prefix, StringComparison.Ordinal)
                    && name.EndsWith("_mask.tex", StringComparison.OrdinalIgnoreCase)
                    && !referenced.Contains(name)
                    && !tail.Contains(name, StringComparer.OrdinalIgnoreCase);
            })
            .ToList();
    }

    /// <summary>
    /// Units whose copies are on disk (and whose mask is complete) are ready; the rest are missing and must not be
    /// applied — Penumbra would happily redirect to a file that is not there.
    /// </summary>
    public static (List<RedirectUnit> Ready, List<RedirectUnit> Missing) UnitsReadyToApply(
        IEnumerable<RedirectUnit> units, Func<string, bool> fileExists, Func<string, bool> isCompleteTex)
    {
        var ready = new List<RedirectUnit>();
        var missing = new List<RedirectUnit>();
        foreach (var u in units)
        {
            bool ok = fileExists(u.MaterialCopy) && (u.Mask is not { } m || isCompleteTex(m.Copy));
            (ok ? ready : missing).Add(u);
        }
        return (ready, missing);
    }

    /// <summary>
    /// Units Penumbra does not serve from our copies after an apply: either path resolving elsewhere drops the whole unit.
    /// </summary>
    public static List<RedirectUnit> Outranked(IEnumerable<RedirectUnit> units,
                                               IReadOnlyDictionary<string, string?> resolved)
    {
        bool Same(string gamePath, string copy)
            => resolved.TryGetValue(gamePath, out var r) && r != null
            && string.Equals(NormalPath(StripPenumbraPrefix(r)), NormalPath(copy), StringComparison.OrdinalIgnoreCase);
        return units.Where(u => !Same(u.MaterialGamePath, u.MaterialCopy)
                             || (u.Mask is { } m && !Same(m.GamePath, m.Copy)))
                    .ToList();
    }

    public static string NormalPath(string p)
    {
        try { return Path.GetFullPath(p.Replace('/', Path.DirectorySeparatorChar)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return p; }
    }

    // ── reloads ─────────────────────────────────────────────────────────────

    /// <summary>
    /// A reload is needed only when the character still draws a redirected material from something other than its copy
    /// (gear just put on loads its old material before the walk).
    /// </summary>
    public static bool NeedsReload(IEnumerable<RedirectUnit> applied, IReadOnlyDictionary<string, string> loaded)
        => applied.Any(u => loaded.TryGetValue(u.MaterialGamePath, out var file)
                         && !string.Equals(NormalPath(StripPenumbraPrefix(file)), NormalPath(u.MaterialCopy),
                                           StringComparison.OrdinalIgnoreCase));

    internal enum ReloadRoute { None, Deferred, InPlace, Redraw }

    /// <summary>
    /// How to reload. Auto redraw off and not asked for by the user: none (it takes effect at the next redraw). An auto
    /// refit batch holding redraws: deferred into its one redraw. In-place when allowed, available and not learned
    /// broken, and not escalated; otherwise a full redraw.
    /// </summary>
    public static ReloadRoute ChooseReloadRoute(bool autoRedraw, bool userRequested, bool held, bool useInPlace,
                                                bool glamourerAvailable, bool inPlaceBroken, bool escalate)
    {
        if (!autoRedraw && !userRequested) return ReloadRoute.None;
        if (held && !userRequested) return ReloadRoute.Deferred;
        if (useInPlace && glamourerAvailable && !inPlaceBroken && !escalate) return ReloadRoute.InPlace;
        return ReloadRoute.Redraw;
    }

    /// <summary>What the session has learned about in-place reloads picking up materials. Null: not yet known.</summary>
    internal sealed class ReloadLearning
    {
        public bool? InPlaceLoadsMaterials { get; private set; }

        /// <summary>
        /// A verify after an in-place reload. Learned "broken" only when Penumbra resolves the copy but the resource map
        /// still shows the old file — a mid-rebuild character or an outranked path proves nothing.
        /// </summary>
        public void Observe(ReloadRoute route, bool resolvesToCopy, bool loadedCopy)
        {
            if (route != ReloadRoute.InPlace) return;
            if (loadedCopy) InPlaceLoadsMaterials = true;
            else if (resolvesToCopy && InPlaceLoadsMaterials != true) InPlaceLoadsMaterials = false;
        }

        public bool Broken => InPlaceLoadsMaterials == false;
    }

    /// <summary>Per applied-map stamp: in place, then a full redraw, then give up. Never a loop.</summary>
    internal sealed class Escalation
    {
        private string? stamp;
        private int step;

        internal enum Next { InPlace, Redraw, GaveUp }

        /// <summary>The next step for a reload that has not taken yet, for this map.</summary>
        public Next Fail(string mapStamp)
        {
            if (mapStamp != stamp) { stamp = mapStamp; step = 0; }
            var next = step switch { 0 => Next.InPlace, 1 => Next.Redraw, _ => Next.GaveUp };
            step++;
            return next;
        }

        public void Reset() { stamp = null; step = 0; }
    }

    // ── signals ─────────────────────────────────────────────────────────────

    internal enum Signal { Equip, Redraw, ModSetting, Collection, ModMoved, PenumbraReady, Enabled, Verify, Published, BlankRetry }

    /// <summary>How long after a signal to walk, and whether that walk must peek under our own redirect.</summary>
    public static (int DelayMs, bool Peek) SignalFor(Signal s) => s switch
    {
        Signal.Equip => (750, false),
        Signal.Redraw => (250, false),
        Signal.ModSetting => (400, true),
        Signal.Collection => (250, true),
        Signal.ModMoved => (250, true),
        Signal.PenumbraReady => (0, true),
        Signal.Enabled => (0, true),
        Signal.Verify => (1000, false),
        Signal.Published => (250, false),
        Signal.BlankRetry => (500, false),
        _ => (250, false),
    };

    internal enum TransitionKind { None, Evaluate, Teardown }

    public static TransitionKind Transition(bool was, bool now)
        => !was && now ? TransitionKind.Evaluate : was && !now ? TransitionKind.Teardown : TransitionKind.None;

    internal enum Waiting { None, Penumbra, ManagedMod }

    public static Waiting WaitingFor(bool penumbraAvailable, string? managedRoot, bool managedMetaExists)
        => !penumbraAvailable || managedRoot == null ? Waiting.Penumbra
         : !managedMetaExists ? Waiting.ManagedMod
         : Waiting.None;

    /// <summary>
    /// Whether a Penumbra setting change must peek. Only for the player's collection; never the managed mod, never
    /// while nothing is redirected; always when the event names no mod; otherwise only a mod that supplies a redirected
    /// upstream or ships one of the redirected game paths.
    /// </summary>
    public static bool PeekFor(Guid eventCollection, Guid? playerCollection, string? modDirectory, bool anythingRedirected,
                               IReadOnlySet<string> sourceMods, Func<string, bool> shipsRedirectedPath)
    {
        if (!anythingRedirected) return false;
        if (playerCollection is not { } player || eventCollection != player) return false;
        if (string.IsNullOrEmpty(modDirectory)) return true;
        if (string.Equals(modDirectory, SidecarDiscoveryService.ManagedModDir, StringComparison.OrdinalIgnoreCase))
            return false;
        return sourceMods.Contains(modDirectory) || shipsRedirectedPath(modDirectory);
    }

    /// <summary>The newest last-write of a mod's option files: what a "ships this path" cache is keyed on.</summary>
    public static long PeekCacheKey(IEnumerable<(string Name, long Ticks)> modJsonFiles)
        => modJsonFiles.Where(f => f.Name.Equals("meta.json", StringComparison.OrdinalIgnoreCase)
                                || f.Name.Equals("default_mod.json", StringComparison.OrdinalIgnoreCase)
                                || (f.Name.StartsWith("group_", StringComparison.OrdinalIgnoreCase)
                                    && f.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
                       .Select(f => f.Ticks).DefaultIfEmpty(0).Max();

    /// <summary>
    /// The untrusted peek's settle: re-resolve until two rounds agree with no copy among the answers, at most
    /// <see cref="SettleCapMs"/>; then fail open.
    /// </summary>
    internal static class PeekSettle
    {
        public const int IntervalMs = 50, SettleCapMs = 600;

        internal enum Outcome { Accept, Wait, FailOpen }

        public static Outcome Step(IReadOnlyDictionary<string, string?>? previous, IReadOnlyDictionary<string, string?> now,
                                   Func<string?, bool> isCopy, long elapsedMs)
        {
            bool anyCopy = now.Values.Any(isCopy);
            bool agree = previous != null && previous.Count == now.Count
                      && now.All(kv => previous.TryGetValue(kv.Key, out var p)
                                     && string.Equals(p, kv.Value, StringComparison.OrdinalIgnoreCase));
            if (!anyCopy && agree) return Outcome.Accept;
            return elapsedMs >= SettleCapMs ? Outcome.FailOpen : Outcome.Wait;
        }
    }

    /// <summary>
    /// Proteus switched off: what to do, in order — take the upgrade down (and forget the compositor's memo for those
    /// paths) before the compositor's withdraw, and make that withdraw a full redraw when anything was held and in-place
    /// reloads are not known to pick materials up. The compositor's redraw is then the single reload.
    /// </summary>
    public static (bool TeardownFirst, bool ForceFullRedraw) OrderMasterSwitch(bool turningOff, bool anythingHeld,
                                                                                bool? inPlaceLoadsMaterials)
        => turningOff ? (true, anythingHeld && inPlaceLoadsMaterials != true) : (false, false);
}

/// <summary>A unit as it is redirected: material game path → copy, and for Form B the mask game path → mask copy.</summary>
internal sealed record RedirectUnit(string MaterialGamePath, string MaterialCopy, RedirectMask? Mask,
                                    string Upstream = "", string SourceLabel = "", LegacyForm Form = LegacyForm.IndexMask,
                                    int MaskWidth = 0, int MaskHeight = 0);

internal sealed record RedirectMask(string GamePath, string Copy);
