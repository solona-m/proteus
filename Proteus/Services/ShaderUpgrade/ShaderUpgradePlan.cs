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
internal enum LeftReason
{
    OldTable, UnsupportedForm, Unreadable, Compositor, WriteFailed, LegacyMod, MixedSpecular, SpecularFromOtherMod,
    UnsupportedPlacement, ProteusPack, Declined,
}

/// <summary>
/// The gear shader upgrade's policy, with no game, Penumbra or disk writes of its own: what to upgrade, how, and why
/// anything is left alone. <see cref="ShaderUpgradeWriter"/> writes; <see cref="ShaderUpgradeWatcher"/> schedules.
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
/// so Form B gets a derived mask rather than its <c>_s</c>. One conversion (m0885) runs a flow mode of its own with
/// an extra flow sampler, and the constant 0xAD94E254 differs between them: those are each material's own choices.</item>
/// <item>The library: 3,397 Form A, 2,083 Form B, 5 sixteen-row tables, 3 odd layouts, of 5,488 legacy materials.</item>
/// </list>
/// </summary>
internal static class ShaderUpgradePlan
{
    public const uint TextureModeKey = 0xB616DC5A, TextureModeDefault = 0x5CC605B5, TextureModeCompatibility = 0x600EF9DF;
    public const uint SpecularModeKey = 0xC8BD1DEF, SpecularModeDefault = 0x198D11CD, SpecularModeMask = 0xA02F4828;
    public const uint SamplerDiffuse = 0x115306BE, SamplerNormal = 0x0C5EC1F1, SamplerMask = 0x8A4E82B6,
                      SamplerIndex = 0x565F8FD8;

    /// <summary>A derived mask is halved until both its sides are at most this.</summary>
    public const int MaxMaskSide = 2048;

    /// <summary>The folder in the mod the derived masks are written to.</summary>
    public const string MaskFolder = "Shader Upgrade";

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

    internal enum Source { Mod, Game, Own, ProteusPack, Outside }

    /// <summary>
    /// Where a resolved file comes from: game data (nothing resolved, an echo of the game path, or not a rooted path),
    /// Proteus's own managed mod, a Proteus sidecar pack, another mod under the mods root, or somewhere else.
    /// </summary>
    /// <param name="isProteusPack">Whether a mod folder carries Proteus's sidecar (<c>Proteus/metadata.json</c>).</param>
    public static Source Classify(string gamePath, string? resolved, string? modsRoot, string? managedRoot,
                                  Func<string, bool> isProteusPack)
    {
        if (string.IsNullOrEmpty(resolved) || string.Equals(resolved, gamePath, StringComparison.OrdinalIgnoreCase)
            || !Path.IsPathRooted(StripPenumbraPrefix(resolved)))
            return Source.Game;
        var file = StripPenumbraPrefix(resolved);
        if (!string.IsNullOrEmpty(managedRoot) && ManagedModPaths.IsPublishedOutput(file, managedRoot)) return Source.Own;
        if (string.IsNullOrEmpty(modsRoot) || !HatCompatService.InMods(file, modsRoot, out var modRoot, out _))
            return Source.Outside;
        return isProteusPack(modRoot) ? Source.ProteusPack : Source.Mod;
    }

    /// <summary>A mod folder is a Proteus sidecar pack: Proteus writes its materials itself.</summary>
    public static bool IsProteusPack(string modRoot)
        => File.Exists(Path.Combine(modRoot, SidecarDiscoveryService.SidecarSubdir, SidecarDiscoveryService.MetadataFile));

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

    public static string NormalPath(string p)
    {
        try { return Path.GetFullPath(p.Replace('/', Path.DirectorySeparatorChar)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return p; }
    }

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
        bool hasMode = keys.TryGetValue(TextureModeKey, out var mode);

        if (samplers.Count == 3 && ids.SetEquals([SamplerIndex, SamplerMask, SamplerNormal])
            && (!hasMode || mode == TextureModeDefault))
            return LegacyForm.IndexMask;

        if (samplers.Count == 4 && ids.SetEquals([SamplerDiffuse, SamplerIndex, SamplerMask, SamplerNormal])
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
    public static byte Luma(byte r, byte g, byte b)
        => (byte)Math.Clamp((int)MathF.Round(0.2126f * r + 0.7152f * g + 0.0722f * b), 0, 255);

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

    /// <summary>
    /// The new game path a mask is published at: beside the specular map it replaces, named <c>dt_&lt;hash&gt;_mask.tex</c>,
    /// lowercase ASCII only (the material's string and the redirect key must match exactly).
    /// </summary>
    public static string MaskGamePath(string sGamePath, string hash16)
    {
        var p = sGamePath.Replace('\\', '/');
        int slash = p.LastIndexOf('/');
        var dir = slash >= 0 ? p[..(slash + 1)] : "";
        return (dir + "dt_" + hash16 + "_mask.tex").ToLowerInvariant();
    }

    /// <summary>The mask file's path inside the mod.</summary>
    public static string MaskRel(string hash16) => MaskFolder + "/" + hash16.ToLowerInvariant() + "_mask.tex";

    // ── the memo ────────────────────────────────────────────────────────────

    /// <summary>
    /// Derived masks by what they came from, for the session: an unchanged specular map is not decoded again. The key is
    /// the source file's identity (path, length, last write; the game path for a vanilla <c>_s</c>), the strategy, the
    /// encoding and the cap. Thread-safe.
    /// </summary>
    internal sealed class MaskMemo
    {
        public readonly record struct Key(string Source, long Length, long Ticks, MaskGloss Gloss, TexEncoding Encoding,
                                          int Cap);

        public readonly record struct Entry(string Hash, int Width, int Height);

        private readonly Dictionary<Key, Entry> map = [];
        private readonly object sync = new();

        public static Key KeyFor(string? sDisk, string sGamePath, Func<string, (long Length, long Ticks)?> stat,
                                 MaskGloss gloss, TexEncoding encoding)
        {
            if (sDisk != null && stat(sDisk) is { } s)
                return new Key(sDisk.ToLowerInvariant(), s.Length, s.Ticks, gloss, encoding, MaxMaskSide);
            return new Key("game:" + sGamePath.ToLowerInvariant(), 0, 0, gloss, encoding, MaxMaskSide);
        }

        public bool TryGet(Key key, out Entry entry) { lock (sync) return map.TryGetValue(key, out entry); }
        public void Put(Key key, Entry entry) { lock (sync) map[key] = entry; }
        public void Evict(Key key) { lock (sync) map.Remove(key); }
        public void Clear() { lock (sync) map.Clear(); }
        public int Count { get { lock (sync) return map.Count; } }
    }

    // ── specular source ─────────────────────────────────────────────────────

    internal enum SpecularKind { File, Game, Mixed, OtherMod }

    /// <summary>
    /// Where a Form B material's specular map comes from, so the derived mask bakes its own mod's art and nothing else.
    /// <list type="bullet">
    /// <item>The resolved <c>_s</c> is a file of the material's own mod, and no owner of the material maps the
    /// <c>_s</c> game path to a different file: <see cref="SpecularKind.File"/> (with that file's mod-relative path).</item>
    /// <item>It is the game's own file (Penumbra echoes the path), and no owner ships one: <see cref="SpecularKind.Game"/>.</item>
    /// <item>It resolves into another mod (a retexture): <see cref="SpecularKind.OtherMod"/>.</item>
    /// <item>The owners disagree: <see cref="SpecularKind.Mixed"/>.</item>
    /// </list>
    /// </summary>
    public static (SpecularKind Kind, string? Rel) SpecularSource(string? resolvedS, string sGamePath, string materialModRoot,
                                                                  string modsRoot, IReadOnlyList<HairPortPlan.Owner> materialOwners)
    {
        var shipped = materialOwners.Select(o => HairPortPlan.FileFor(o, sGamePath))
                                    .Where(r => r != null)
                                    .Select(r => MeshVolumeService.Rel(r!))
                                    .Distinct(StringComparer.OrdinalIgnoreCase)
                                    .ToList();
        if (string.IsNullOrEmpty(resolvedS) || string.Equals(resolvedS, sGamePath, StringComparison.OrdinalIgnoreCase)
            || !Path.IsPathRooted(StripPenumbraPrefix(resolvedS)))
            return shipped.Count == 0 ? (SpecularKind.Game, null) : (SpecularKind.Mixed, null);

        var file = StripPenumbraPrefix(resolvedS);
        if (!HatCompatService.InMods(file, modsRoot, out var root, out var rel)
            || !string.Equals(NormalPath(root), NormalPath(materialModRoot), StringComparison.OrdinalIgnoreCase))
            return (SpecularKind.OtherMod, null);
        rel = MeshVolumeService.Rel(rel);
        if (shipped.Any(s => !string.Equals(s, rel, StringComparison.OrdinalIgnoreCase))) return (SpecularKind.Mixed, null);
        return (SpecularKind.File, rel);
    }

    /// <summary>Every owner (default data, option, container) that ships <paramref name="rel"/>.</summary>
    public static List<HairPortPlan.Owner> OwnersOf(IReadOnlyList<HairPortPlan.Owner> owners, string rel)
        => owners.Where(o => o.Files.Values.Any(f => string.Equals(MeshVolumeService.Rel(f), MeshVolumeService.Rel(rel),
                                                                   StringComparison.OrdinalIgnoreCase)))
                 .ToList();

    // ── building ────────────────────────────────────────────────────────────

    /// <summary>A worn gear material, with the file its game path resolves to under the player's mods.</summary>
    public readonly record struct Worn(string GamePath, string? Resolved);

    /// <summary>One worn mod file that will be upgraded, before anything is read.</summary>
    public sealed record Candidate(string ModRoot, string Rel, List<string> GamePaths, byte[] Bytes, LegacyForm Form,
                                   string? SpecularGamePath);

    /// <summary>A derived mask, ready to write (pixels null when the file is already in the mod).</summary>
    public sealed record MaskWrite(string Rel, string GamePath, int Width, int Height, byte[]? Rgba,
                                   IReadOnlyList<(string Group, string Option)> Owners);

    /// <summary>One material file to rewrite in its mod.</summary>
    public sealed record MaterialWrite(string Rel, IReadOnlyList<string> GamePaths, LegacyForm Form, string SourceSha8,
                                       byte[] Bytes, MaskWrite? Mask);

    /// <summary>A mod's writes.</summary>
    public sealed record ModWrites(string ModRoot, List<MaterialWrite> Writes);

    public readonly record struct Left(string GamePath, string Source, LeftReason Reason);

    public sealed record Plan(List<ModWrites> PerMod, List<Left> Left, int Decodes, int MemoHits);

    /// <summary>The first 16 hex digits of the file's SHA-256: what the record compares files by.</summary>
    public static string Sha8(byte[] bytes) => Hash16(bytes);

    /// <summary>Everything planning reads, injected so it needs no game or disk.</summary>
    public sealed class Inputs
    {
        public required string? ModsRoot { get; init; }
        public required string? ManagedRoot { get; init; }
        public required Func<string, byte[]?> Read { get; init; }
        /// <summary>Decode a texture at its native size: (resolved file or null, game path) → RGBA8.</summary>
        public required Func<string?, string, (byte[] Rgba, int W, int H)?> DecodeNative { get; init; }
        public required Func<string, (long Length, long Ticks)?> Stat { get; init; }
        /// <summary>The mod's owners (default data, options, containers), or null for an unreadable or v3 folder.</summary>
        public required Func<string, List<HairPortPlan.Owner>?> ReadOwners { get; init; }
        /// <summary>Whether a file of a mod is declined (undone by the user).</summary>
        public required Func<string, string, bool> IsDeclined { get; init; }
        /// <summary>Whether a mask file is already complete in the mod.</summary>
        public required Func<string, string, bool> MaskFileOk { get; init; }
        public required MaskMemo Memo { get; init; }
        public Func<string, bool> IsProteusPack { get; init; } = ShaderUpgradePlan.IsProteusPack;
        public MaskGloss Gloss { get; init; } = MaskGloss.InverseSpecular;
        public TexEncoding Encoding { get; init; } = TexEncoding.Uncompressed;
    }

    /// <summary>
    /// The worn mod files to upgrade, read and recognised, and a reason for every legacy one left alone. Nothing is left
    /// for materials that are not legacy, not from a mod, or not gear. The specular maps the Form B candidates name
    /// are returned for the caller to resolve through Penumbra before <see cref="Build"/>.
    /// </summary>
    public static (List<Candidate> Candidates, List<Left> Left) Candidates(IEnumerable<Worn> worn, Inputs inp)
    {
        var byFile = new Dictionary<string, (string ModRoot, string Rel, List<string> GamePaths, string Label)>(
            StringComparer.OrdinalIgnoreCase);
        var left = new List<Left>();
        foreach (var w in worn)
        {
            if (!IsGearMaterial(w.GamePath)) continue;
            switch (Classify(w.GamePath, w.Resolved, inp.ModsRoot, inp.ManagedRoot, inp.IsProteusPack))
            {
                case Source.Own: left.Add(new Left(w.GamePath, w.Resolved!, LeftReason.Compositor)); continue;
                case Source.ProteusPack:
                    if (IsLegacyFile(inp, StripPenumbraPrefix(w.Resolved!)))
                        left.Add(new Left(w.GamePath, w.Resolved!, LeftReason.ProteusPack));
                    continue;
                case Source.Mod: break;
                default: continue;
            }
            var file = StripPenumbraPrefix(w.Resolved!);
            HatCompatService.InMods(file, inp.ModsRoot!, out var modRoot, out var rel);
            rel = MeshVolumeService.Rel(rel);
            var key = NormalPath(file);
            if (!byFile.TryGetValue(key, out var entry))
                byFile[key] = entry = (modRoot, rel, [], $"{Path.GetFileName(modRoot)}/{rel}");
            entry.GamePaths.Add(w.GamePath);
        }

        var candidates = new List<Candidate>();
        foreach (var (key, f) in byFile)
        {
            var bytes = SafeRead(inp, key);
            if (bytes == null) { LeftAll(left, f.GamePaths, f.Label, LeftReason.Unreadable); continue; }
            if (!string.Equals(CharacterShaderUpgrade.ShaderOf(bytes), CharacterShaderUpgrade.Legacy,
                               StringComparison.OrdinalIgnoreCase))
                continue;   // not legacy: untouched, not counted
            if (inp.IsDeclined(f.ModRoot, f.Rel)) { LeftAll(left, f.GamePaths, f.Label, LeftReason.Declined); continue; }
            var form = FormOf(bytes);
            if (form == LegacyForm.Unsupported)
            {
                LeftAll(left, f.GamePaths, f.Label, Samplers(bytes) == null ? LeftReason.Unreadable : LeftReason.UnsupportedForm);
                continue;
            }
            candidates.Add(new Candidate(f.ModRoot, f.Rel, f.GamePaths, bytes, form,
                                         form == LegacyForm.Diffuse ? MaskSlotPath(bytes) : null));
        }
        return (candidates, left);
    }

    private static bool IsLegacyFile(Inputs inp, string path)
        => SafeRead(inp, path) is { } b
        && string.Equals(CharacterShaderUpgrade.ShaderOf(b), CharacterShaderUpgrade.Legacy, StringComparison.OrdinalIgnoreCase);

    private static void LeftAll(List<Left> left, IEnumerable<string> gamePaths, string label, LeftReason reason)
    {
        foreach (var g in gamePaths) left.Add(new Left(g, label, reason));
    }

    private static byte[]? SafeRead(Inputs inp, string path)
    {
        try { return inp.Read(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// The upgrades for the candidates: each material upgraded, made wind-neutral and, for Form B, given a derived mask
    /// published beside its own redirects. <paramref name="resolvedSpecular"/> holds what each specular game path resolves
    /// to for the player.
    /// </summary>
    public static Plan Build(IEnumerable<Candidate> candidates, IReadOnlyDictionary<string, string?> resolvedSpecular,
                             Inputs inp, List<Left>? leftSoFar = null)
    {
        var left = leftSoFar ?? [];
        var perMod = new Dictionary<string, ModWrites>(StringComparer.OrdinalIgnoreCase);
        int decodes = 0, hits = 0;
        var ownersCache = new Dictionary<string, List<HairPortPlan.Owner>?>(StringComparer.OrdinalIgnoreCase);

        foreach (var c in candidates)
        {
            var label = $"{Path.GetFileName(c.ModRoot)}/{c.Rel}";
            if (!ownersCache.TryGetValue(c.ModRoot, out var owners)) ownersCache[c.ModRoot] = owners = inp.ReadOwners(c.ModRoot);
            if (owners == null) { LeftAll(left, c.GamePaths, label, LeftReason.LegacyMod); continue; }

            var up = CharacterShaderUpgrade.Upgrade(c.Bytes);
            if (up == null)
            {
                LeftAll(left, c.GamePaths, label, IsOldTable(c.Bytes) ? LeftReason.OldTable : LeftReason.Unreadable);
                continue;
            }
            up = WindNeutral(up);
            if (up == null) { LeftAll(left, c.GamePaths, label, LeftReason.Unreadable); continue; }

            MaskWrite? mask = null;
            if (c.Form == LegacyForm.Diffuse)
            {
                var slot = c.SpecularGamePath;
                if (slot == null) { LeftAll(left, c.GamePaths, label, LeftReason.Unreadable); continue; }
                var mine = OwnersOf(owners, c.Rel);
                if (mine.Count == 0 || mine.Any(o => o.IsCombining))
                {
                    LeftAll(left, c.GamePaths, label, LeftReason.UnsupportedPlacement);
                    continue;
                }
                resolvedSpecular.TryGetValue(slot, out var resolvedS);
                var (kind, sRel) = SpecularSource(resolvedS, slot, c.ModRoot, inp.ModsRoot!, mine);
                if (kind == SpecularKind.OtherMod) { LeftAll(left, c.GamePaths, label, LeftReason.SpecularFromOtherMod); continue; }
                if (kind == SpecularKind.Mixed) { LeftAll(left, c.GamePaths, label, LeftReason.MixedSpecular); continue; }

                var sDisk = kind == SpecularKind.File ? Path.Combine(c.ModRoot, sRel!.Replace('/', Path.DirectorySeparatorChar)) : null;
                var derived = Derive(c.ModRoot, slot, sDisk, inp, ref decodes, ref hits);
                if (derived == null) { LeftAll(left, c.GamePaths, label, LeftReason.Unreadable); continue; }
                var (hash, w, h, rgba) = derived.Value;
                var gamePath = MaskGamePath(slot, hash);
                mask = new MaskWrite(MaskRel(hash), gamePath, w, h, rgba, mine.Select(o => (o.Group, o.Option)).ToList());
                up = TextureLoader.RetargetTexturePaths(up, new Dictionary<string, string>(StringComparer.Ordinal) { [slot] = gamePath });
                if (up == null) { LeftAll(left, c.GamePaths, label, LeftReason.Unreadable); continue; }
            }

            if (!IsSound(c.Bytes, up, c.Form, mask?.GamePath)) { LeftAll(left, c.GamePaths, label, LeftReason.Unreadable); continue; }
            if (!perMod.TryGetValue(c.ModRoot, out var mw)) perMod[c.ModRoot] = mw = new ModWrites(c.ModRoot, []);
            mw.Writes.Add(new MaterialWrite(c.Rel, c.GamePaths, c.Form, Sha8(c.Bytes), up, mask));
        }
        return new Plan([.. perMod.Values], left, decodes, hits);
    }

    private static (string Hash, int W, int H, byte[]? Rgba)? Derive(string modRoot, string slot, string? sDisk, Inputs inp,
                                                                    ref int decodes, ref int hits)
    {
        var key = MaskMemo.KeyFor(sDisk, slot, inp.Stat, inp.Gloss, inp.Encoding);
        if (inp.Memo.TryGet(key, out var hit))
        {
            if (inp.MaskFileOk(modRoot, MaskRel(hit.Hash)))
            {
                hits++;
                return (hit.Hash, hit.Width, hit.Height, null);
            }
        }

        decodes++;
        var decoded = inp.DecodeNative(sDisk, slot);
        if (decoded is not { } d || d.W <= 0 || d.H <= 0 || d.Rgba.Length < d.W * d.H * 4) return null;
        var (rgba, w, h) = MaskFrom(d.Rgba, d.W, d.H, inp.Gloss);
        if (!IsSoundMaskBuffer(rgba, w, h, d.Rgba, d.W, d.H, inp.Gloss)) return null;
        var hash = MaskHash(rgba, w, h, inp.Gloss, inp.Encoding);
        inp.Memo.Put(key, new MaskMemo.Entry(hash, w, h));
        return (hash, w, h, inp.MaskFileOk(modRoot, MaskRel(hash)) ? null : rgba);
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
    /// The <c>.mtrl</c> gate: everything a rewritten material must be before it is written. <c>character.shpk</c>; the
    /// header size equals the length; it parses; the texture strings equal the source's, except Form B's mask entry,
    /// which is the new mask path exactly; the sampler bindings and the Texture Mode are unchanged; both movement
    /// constants are 0.
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
    /// colour is shrunk), and complete. (WriteTex writes a non-4-aligned size uncompressed, so BC7 is always aligned.)
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

    // ── signals, timing, reloads ────────────────────────────────────────────

    internal enum Signal { Equip, Redraw, ModSetting, Collection, ModMoved, PenumbraReady, Enabled, BlankRetry, AutoRefitBusy }

    /// <summary>How long after a signal to walk.</summary>
    public static int SignalFor(Signal s) => s switch
    {
        Signal.Equip => 750,
        Signal.Redraw => 250,
        Signal.ModSetting => 400,
        Signal.Collection => 400,
        Signal.ModMoved => 0,
        Signal.PenumbraReady => 0,
        Signal.Enabled => 0,
        Signal.BlankRetry => 500,
        Signal.AutoRefitBusy => 500,
        _ => 250,
    };

    public const int MaxWalkWaitMs = 2000;

    /// <summary>
    /// When a walk asked for at <paramref name="now"/> with <paramref name="delayMs"/> is due: a burst of signals
    /// collapses into one walk (the latest signal's settle time decides), but never later than
    /// <see cref="MaxWalkWaitMs"/> after the first signal still pending, so a steady stream of events cannot postpone it
    /// for ever.
    /// </summary>
    public static (long Due, long FirstAt) NextDue(long now, int delayMs, long pendingDue, long firstAt)
    {
        long first = pendingDue == 0 ? now : firstAt;
        return (Math.Min(now + delayMs, first + MaxWalkWaitMs), first);
    }

    internal enum TransitionKind { None, Evaluate, Stop }

    /// <summary>Turning the setting on (or starting with it on) evaluates what is worn; turning it off only stops.</summary>
    public static TransitionKind Transition(bool was, bool now)
        => !was && now ? TransitionKind.Evaluate : was && !now ? TransitionKind.Stop : TransitionKind.None;

    internal enum Waiting { None, Penumbra }

    public static Waiting WaitingFor(bool penumbraAvailable, string? modsRoot)
        => !penumbraAvailable || string.IsNullOrEmpty(modsRoot) ? Waiting.Penumbra : Waiting.None;

    /// <summary>A walk waits while auto refit has anything running, queued, due or undoing: both write into worn mods.</summary>
    public static bool MayWalk(bool running, int queued, bool walkDue, bool undoing)
        => !running && queued == 0 && !walkDue && !undoing;

    internal enum RunKind { Upgrade, UndoWorn, UpgradeNow }

    /// <summary>
    /// Whether to redraw after a run that changed mods: an automatic upgrade follows auto redraw; the user's buttons
    /// always redraw.
    /// </summary>
    public static bool RedrawAfter(RunKind kind, bool autoRedraw, bool changedAny)
        => changedAny && (kind != RunKind.Upgrade || autoRedraw);

    /// <summary>The chat notice is said once per mod per session.</summary>
    public static bool ShouldAnnounce(ISet<string> announced, string modDir) => announced.Add(modDir);

    /// <summary>The buttons start only when nothing else of the upgrade is running.</summary>
    public static bool MayStart(bool busy) => !busy;
}
