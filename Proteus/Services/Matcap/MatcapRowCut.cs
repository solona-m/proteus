using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Proteus.Services;

namespace Proteus.Services.Matcap;

/// <summary>
/// A row matcap's cut, planned: a copy of the part's own material whose normal has blue 0 on the chosen colour-table row,
/// so character.shpk discards there (normal blue × vertex alpha &lt; <c>g_AlphaThreshold</c>) and the matcap copy beneath
/// shows through. characterreflection never discards in its colour passes, so the cut must be the part's own material.
/// <para/>
/// The cut follows how the author's material resolves, per variant folder f (<c>gp_f</c> the material's game path there,
/// <c>cut_f</c> the cut's): (a) every container that redirects <c>gp_f</c> gets <c>cut_f</c> → a cut of its own file;
/// (b) else, when the game ships <c>gp_f</c>, default data gets <c>cut_f</c> → a cut of the game's file; (c) else every
/// container redirecting the worn folder's path gets <c>cut_f</c> → its worn cut (what Gear variants would copy); (d) else
/// nothing. Penumbra then settles both paths the same way, options and variants included.
/// <para/>
/// Each distinct (normal, index, row, sub-row, threshold class) is baked once. A threshold-0 source gets blue 255 off the
/// row, so raising its threshold to 0.5 opens only the row; an alpha-tested one keeps its authored blue off the row.
/// Pure of the mod's state except reading: nothing is written into the mod here (bakes go to the temp folder).
/// </summary>
internal static class MatcapRowCut
{
    /// <summary>The shader whose discard rule the cut relies on (measured from its disassembly).</summary>
    public const string CutShader = "character.shpk";

    /// <summary>The cut material's alpha threshold when its source had none.</summary>
    public const float CutThreshold = 0.5f;

    public const string CodeEmpty = "row-empty", CodeShader = "row-shader", CodeNoNormal = "row-no-normal", CodeBake = "row-bake",
                        CodeTextureVaries = "row-texture-varies", CodeFileSwap = "row-file-swap",
                        CodeAmbiguous = "row-ambiguous-option", CodeUnreadable = "row-unreadable", CodeNoSource = "row-no-source";

    public sealed record Inputs(string ModRoot, MatcapPlan.Target Target, string Source, IReadOnlyCollection<int> Folders,
                                int WornFolder, MatcapStudio.Environment Env, int Row, SubRowPick Sub, string Token, string CutName,
                                bool Preview = false, Func<bool>? Affordable = null);

    /// <summary>One distinct bake: the cut normal (temp file, game path), its row mask, and what the holes count needs.</summary>
    public sealed class Bake
    {
        public required string Key { get; init; }
        public required string Temp { get; init; }
        public required string GamePath { get; init; }
        public required bool[] Mask { get; init; }
        public required int W { get; init; }
        public required int H { get; init; }
        public required bool ThresholdZero { get; init; }
        public required float Threshold { get; init; }
        /// <summary>The source normal's blue per texel (at the mask's size), for the holes count.</summary>
        public required byte[] Blue { get; init; }
        public required int RowTexels { get; init; }
    }

    /// <summary>One cut copy of a source material, and every redirect it is published at.</summary>
    public sealed class CutFile
    {
        public required string Sha8 { get; init; }
        public required byte[] Bytes { get; init; }
        public required bool ThresholdZero { get; init; }
        public required MatcapRecord.RowSource Source { get; init; }
        public required MatcapRecord.RowSource Normal { get; init; }
        public MatcapRecord.RowSource? Index { get; init; }
        public required Bake Bake { get; init; }
        public List<(PenumbraModMeta.ContainerRef Container, string GamePath)> Redirects { get; } = [];
    }

    public sealed record Plan(List<CutFile> Files, List<Bake> Bakes, List<MatcapRecord.RowFolder> Folders, string SwapsSha8,
                              bool AllThresholdZero, bool AnyThresholdZero, List<string> FollowTargets);

    public sealed record Refusal(string Code, string Message);

    /// <summary>The plan, or why the row can't be cut. Writes only the bakes, to the temp folder.</summary>
    public static (Plan? Plan, Refusal? Refused) Make(Inputs i)
    {
        var redirects = PenumbraModMeta.TryReadContainerRedirects(i.ModRoot);
        var swaps = PenumbraModMeta.TryReadContainerFileSwaps(i.ModRoot);
        var groups = ReadGroups(i.ModRoot);
        if (redirects == null || swaps == null || groups == null)
            return Refuse(CodeUnreadable, ProteusLoc.Localize("Matcap.Refused.RowManifestUnreadable",
                "The mod's list of files (meta.json) could not be read, so nothing was changed."));

        string GamePathAt(int f) => MatcapPlan.MaterialGamePaths(i.Target, i.Source, [], [f]).FirstOrDefault() ?? "";
        string CutPathAt(int f) => MatcapPlan.MaterialGamePaths(i.Target, i.CutName, [], [f]).FirstOrDefault() ?? "";
        var folders = i.Folders.Where(f => f > 0).Distinct().OrderBy(f => f == i.WornFolder ? 0 : 1).ThenBy(f => f).ToList();
        if (!folders.Contains(i.WornFolder)) folders.Insert(0, i.WornFolder);
        var materialPaths = folders.Select(GamePathAt).Where(p => p.Length > 0).ToList();

        // ── gates on the mod's layout ──
        if (swaps.FirstOrDefault(s => materialPaths.Any(p => PenumbraModMeta.SameFile(s.GamePath, p))) is { GamePath.Length: > 0 } swap)
            return Refuse(CodeFileSwap, string.Format(ProteusLoc.Localize("Matcap.Refused.RowFileSwap.Fmt",
                "{0} swaps this part's material for another file, so a row matcap can't follow it."), Describe(swap.Container)));

        // A material the gear shader upgrade rewrote is cut like any other (for old gear it is the only way onto
        // character.shpk); the upgrade in turn won't undo a file a row matcap depends on (Dependencies). Its declines
        // only shape the refusal for a part still on the old shader.
        var declined = i.Env.GearShadersOn is true ? ShaderUpgradeWriter.ReadRecord(i.ModRoot)?.Declined ?? [] : [];

        // ── per folder: the case and the sources ──
        var sources = new List<(int Folder, string Case, PenumbraModMeta.ContainerRef? Container, string? File, byte[] Bytes, string GamePath)>();
        var cases = new List<MatcapRecord.RowFolder>();
        var wornHolders = redirects.Where(r => PenumbraModMeta.SameFile(r.GamePath, GamePathAt(i.WornFolder))).ToList();
        foreach (var f in folders)
        {
            var gp = GamePathAt(f);
            if (gp.Length == 0) continue;
            var holders = redirects.Where(r => PenumbraModMeta.SameFile(r.GamePath, gp)).ToList();
            if (holders.Count > 0)
            {
                foreach (var h in holders)
                {
                    if (ReadModFile(i.ModRoot, h.File) is not { } bytes) continue;
                    sources.Add((f, MatcapRecord.RowFingerprint.CaseContainer, h.Container, h.File, bytes, gp));
                }
                cases.Add(new MatcapRecord.RowFolder { Folder = f, Case = MatcapRecord.RowFingerprint.CaseContainer });
            }
            else if (i.Env.ReadGameFile(gp) is { } game)
            {
                sources.Add((f, MatcapRecord.RowFingerprint.CaseGame, null, null, game, gp));
                cases.Add(new MatcapRecord.RowFolder { Folder = f, Case = MatcapRecord.RowFingerprint.CaseGame });
            }
            else if (wornHolders.Count > 0)
            {
                foreach (var h in wornHolders)
                {
                    if (ReadModFile(i.ModRoot, h.File) is not { } bytes) continue;
                    sources.Add((f, MatcapRecord.RowFingerprint.CaseWorn, h.Container, h.File, bytes, gp));
                }
                cases.Add(new MatcapRecord.RowFolder { Folder = f, Case = MatcapRecord.RowFingerprint.CaseWorn });
            }
            else cases.Add(new MatcapRecord.RowFolder { Folder = f, Case = MatcapRecord.RowFingerprint.CaseNone });
        }
        if (sources.Count == 0)
            return Refuse(CodeNoSource, ProteusLoc.Localize("Matcap.Refused.RowNoSource",
                "Proteus couldn't find this part's material in the mod or the game, so there is nothing to cut."));

        // Two options of one group sharing a name: EditContainers addresses options by name, so the cut would land in the wrong one.
        foreach (var s in sources.Where(s => s.Container is { IsDefault: false, Option: not null }))
            if (groups.TryGetValue(s.Container!.Value.Group, out var g)
                && g.Options.Count(o => string.Equals(o, s.Container.Value.Option, StringComparison.OrdinalIgnoreCase)) > 1)
                return Refuse(CodeAmbiguous, string.Format(ProteusLoc.Localize("Matcap.Refused.RowAmbiguousOption.Fmt",
                    "The group \"{0}\" has two options called \"{1}\", so a row matcap can't tell them apart."),
                    s.Container.Value.Group, s.Container.Value.Option));

        // ── per source: shader, normal, ownership, texture variation, bake, cut copy ──
        var bakes = new Dictionary<string, Bake>(StringComparer.Ordinal);
        var files = new Dictionary<string, CutFile>(StringComparer.Ordinal);
        var follow = new List<string>();
        string cutStem = MatcapWriter.StemOf(i.CutName).ToLowerInvariant();
        foreach (var s in sources)
        {
            var where = s.Container is { } c ? Describe(c) : ProteusLoc.Localize("Matcap.Row.GameFile", "the game's own file");
            var h = MatcapMaterial.ReadHeader(s.Bytes);
            if (h == null || !string.Equals(h.Shader, CutShader, StringComparison.OrdinalIgnoreCase))
                return Refuse(CodeShader, ShaderRefusal(where, h?.Shader, s.File, i.Env.GearShadersOn, declined));
            var normalPath = h.TextureOf(MatcapMaterial.SamplerNormal);
            if (string.IsNullOrEmpty(normalPath))
                return Refuse(CodeNoNormal, ProteusLoc.Localize("Matcap.Refused.RowNoNormal",
                    "This part's material has no normal map, which a row matcap cuts by."));
            var indexPath = h.TextureOf(ShaderUpgradePlan.SamplerIndex);

            // The textures as this source's container sees them, and no other container serving them differently.
            var normal = ResolveTexture(i, redirects, s.Container, normalPath);
            (byte[]? Bytes, PenumbraModMeta.ContainerRef? Container, string? File)? index =
                string.IsNullOrEmpty(indexPath) ? null : ResolveTexture(i, redirects, s.Container, indexPath);
            if (normal.Bytes == null)
                return Refuse(CodeBake, string.Format(ProteusLoc.Localize("Matcap.Refused.RowBake.Fmt", "The row couldn't be cut: {0}."),
                    ProteusLoc.Localize("Matcap.Refused.RowBake.Normal", "the normal map couldn't be read")));
            if (index != null && index.Value.Bytes == null)
                return Refuse(CodeBake, string.Format(ProteusLoc.Localize("Matcap.Refused.RowBake.Fmt", "The row couldn't be cut: {0}."),
                    ProteusLoc.Localize("Matcap.Refused.RowBake.Index", "the index map couldn't be read")));
            foreach (var (path, got) in new[] { (normalPath, normal), (indexPath ?? "", index ?? default) })
            {
                if (path.Length == 0) continue;
                if (swaps.FirstOrDefault(sw => PenumbraModMeta.SameFile(sw.GamePath, path)) is { GamePath.Length: > 0 } tsw)
                    return Refuse(CodeFileSwap, string.Format(ProteusLoc.Localize("Matcap.Refused.RowMapSwap.Fmt",
                        "{0} swaps this part's normal or index map for another file, so a row matcap can't follow it."), Describe(tsw.Container)));
                var sha = HairPortPlan.Sha8(got.Bytes!);
                foreach (var x in redirects.Where(r => PenumbraModMeta.SameFile(r.GamePath, path)))
                {
                    if (s.Container is { } own && x.Container.SameAs(own)) continue;
                    if (ReadModFile(i.ModRoot, x.File) is { } xb && HairPortPlan.Sha8(xb) == sha) continue;
                    // Outranked by the source's own redirect, or a rival option of the same single-choice group: never both on.
                    if (x.Container.IsDefault && s.Container is { } own2 && redirects.Any(r => r.Container.SameAs(own2) && PenumbraModMeta.SameFile(r.GamePath, path))) continue;
                    if (s.Container is { IsDefault: false, Option: not null } sc && x.Container.Option != null
                        && string.Equals(sc.Group, x.Container.Group, StringComparison.OrdinalIgnoreCase)
                        && groups.TryGetValue(sc.Group, out var gg) && gg.Single) continue;
                    return Refuse(CodeTextureVaries, string.Format(ProteusLoc.Localize("Matcap.Refused.RowTextureVaries.Fmt",
                        "{0} swaps this part's normal or index map, so one cut can't fit every choice."), Describe(x.Container)));
                }
            }

            float threshold = h.AlphaThreshold ?? 0f;
            bool zero = threshold <= 0f;
            var normalSha = HairPortPlan.Sha8(normal.Bytes!);
            var indexSha = index is { Bytes: { } ib } ? HairPortPlan.Sha8(ib) : "";
            // The threshold class keys the off-row blue (255 for threshold 0); an alpha-tested source's own threshold keys
            // the holes count, so two thresholds never share one bake.
            var key = $"{normalSha}|{indexSha}|{i.Row}|{i.Sub}|{(zero ? "zero" : threshold.ToString("R", System.Globalization.CultureInfo.InvariantCulture))}";
            if (!bakes.TryGetValue(key, out var bake))
            {
                int k = bakes.Count + 1;
                var temp = Path.Combine(i.Env.TempDir, $"rowcut_{Guid.NewGuid():N}.tex");
                var (b, why) = BakeOne(i, normal.Bytes!, index?.Bytes, zero, threshold, temp, normalPath);
                if (b == null)
                    return Refuse(CodeBake, string.Format(ProteusLoc.Localize("Matcap.Refused.RowBake.Fmt", "The row couldn't be cut: {0}."), why));
                bake = new Bake
                {
                    Key = key, Temp = temp, GamePath = $"{MatcapWriter.TextureFolder(i.Token)}/{cutStem}_cut{k}.tex",
                    Mask = b.Value.Mask, W = b.Value.W, H = b.Value.H, ThresholdZero = zero, Threshold = threshold,
                    Blue = b.Value.Blue, RowTexels = b.Value.RowTexels,
                };
                bakes[key] = bake;
            }

            int ni = h.Textures.ToList().FindIndex(t => string.Equals(t, normalPath, StringComparison.OrdinalIgnoreCase));
            var cut = ni < 0 ? null : MatcapMaterial.RetargetByIndex(s.Bytes, new Dictionary<int, string> { [ni] = bake.GamePath });
            if (cut == null)
                return Refuse(CodeBake, string.Format(ProteusLoc.Localize("Matcap.Refused.RowBake.Fmt", "The row couldn't be cut: {0}."),
                    ProteusLoc.Localize("Matcap.Refused.RowBake.Write", "the cut material couldn't be written")));
            if (zero) cut = TextureLoader.EnsureConstantValues(cut, MatcapMaterial.ConstAlphaThreshold, CutThreshold).data;
            var cutSha = HairPortPlan.Sha8(cut);
            if (!files.TryGetValue(cutSha, out var cf))
            {
                cf = new CutFile
                {
                    Sha8 = cutSha, Bytes = cut, ThresholdZero = zero, Bake = bake,
                    Source = SourceRef(i.ModRoot, s.Container, s.File, s.GamePath, s.Bytes),
                    Normal = SourceRef(i.ModRoot, normal.Container, normal.File, normalPath, normal.Bytes!),
                    Index = index is { } ix ? SourceRef(i.ModRoot, ix.Container, ix.File, indexPath!, ix.Bytes!) : null,
                };
                files[cutSha] = cf;
            }
            var at = s.Container ?? new PenumbraModMeta.ContainerRef("");
            var cutPath = CutPathAt(s.Folder);
            if (cutPath.Length > 0 && !cf.Redirects.Any(r => r.Container.SameAs(at) && PenumbraModMeta.SameFile(r.GamePath, cutPath)))
                cf.Redirects.Add((at, cutPath));
            if (s.File != null && !follow.Contains(s.File, StringComparer.OrdinalIgnoreCase)) follow.Add(s.File);
        }

        if (bakes.Values.All(b => b.RowTexels == 0))
            return Refuse(CodeEmpty, string.Format(ProteusLoc.Localize("Matcap.Refused.RowEmpty.Fmt",
                "No pixel of this part uses row {0}."), RowLabel(i.Row, i.Sub)));

        var swapText = string.Join("\n", swaps.Where(sw => materialPaths.Any(p => PenumbraModMeta.SameFile(sw.GamePath, p)))
                                              .Select(sw => $"{sw.Container.Group}|{sw.Container.Option}|{sw.Container.Index}|{sw.GamePath}|{sw.File}")
                                              .Order(StringComparer.OrdinalIgnoreCase));
        var plan = new Plan(files.Values.ToList(), bakes.Values.ToList(), cases,
                            swapText.Length == 0 ? "" : HairPortPlan.Sha8(System.Text.Encoding.UTF8.GetBytes(swapText)),
                            files.Values.All(f => f.ThresholdZero), files.Values.Any(f => f.ThresholdZero), follow);
        return (plan, null);
    }

    /// <summary>
    /// Whether any file the mod serves the material from (by file name, any container) has colours set in Proteus
    /// (<paramref name="hasColours"/>, keyed by the mod-relative file): the cut copy won't follow them. Reads the
    /// manifest: never on the framework thread.
    /// </summary>
    public static bool FollowWarn(string modRoot, string materialName, Func<string, bool> hasColours)
    {
        var stem = Path.GetFileName(materialName.Replace('\\', '/'));
        return (PenumbraModMeta.TryReadContainerRedirects(modRoot) ?? [])
            .Where(r => r.GamePath.Replace('\\', '/').EndsWith("/" + stem, StringComparison.OrdinalIgnoreCase))
            .Any(r => hasColours(r.File.Replace('\\', '/')));
    }

    /// <summary>The author's material a cut copy stands for: <c>/mt_x_mcutabc1231.mtrl</c> → <c>/mt_x.mtrl</c>.</summary>
    public static string SourceNameOf(string cutName) =>
        System.Text.RegularExpressions.Regex.Replace(cutName, @"_mcut[0-9a-f]{6}\d+\.mtrl$", ".mtrl",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// Why a recorded row matcap no longer fits the mod, or null when it does. Consistent means: in every folder recorded
    /// at Apply or now holding the material or the cut, the cut path resolves, container by container, to the cut of the
    /// same source the author's material path resolves to. Sources are matched by CONTENT (Sha8), so byte-identical files
    /// at different paths (one cut copy serving both) count as the same source.
    /// <list type="bullet">
    /// <item>every container redirecting the material also redirects the cut, to a cut whose source matches that
    /// container's current file (a redirect to a missing file draws nothing either way and is skipped);</item>
    /// <item>a container redirecting only the cut is allowed only where nothing serves the material and the recorded case
    /// is the worn fallback (its cut's source then matches its worn file);</item>
    /// <item>where the recorded case is the game's file, default data's cut is the game cut (checked when a game reader is
    /// given);</item>
    /// <item>source normal and index maps, and the file swaps on the material's paths, are unchanged.</item>
    /// </list>
    /// Hashes only files whose size or time differ from the recorded source; never alarms on an unreadable manifest.
    /// </summary>
    public static string? Stale(string modRoot, MatcapRecord.MaterialEntry entry, MatcapPlan.Target target,
                                Func<string, byte[]?>? readGameFile)
    {
        if (entry.Row is not { } row) return null;
        var redirects = PenumbraModMeta.TryReadContainerRedirects(modRoot);
        var swaps = PenumbraModMeta.TryReadContainerFileSwaps(modRoot);
        if (redirects == null || swaps == null) return null;
        var source = SourceNameOf(row.Cut);
        var sourceLeaf = source.Replace('\\', '/')[(source.Replace('\\', '/').LastIndexOf('/') + 1)..];
        var cutLeaf = row.Cut.Replace('\\', '/')[(row.Cut.Replace('\\', '/').LastIndexOf('/') + 1)..];
        string GamePathAt(int f) => MatcapPlan.MaterialGamePaths(target, source, [], [f]).FirstOrDefault() ?? "";
        string CutPathAt(int f) => MatcapPlan.MaterialGamePaths(target, row.Cut, [], [f]).FirstOrDefault() ?? "";

        // Every folder to check: recorded ones, plus any folder of this item that now holds the material or the cut.
        var folders = row.Fingerprint.Folders.Select(x => x.Folder).ToList();
        if (target.Item is { } item)
            foreach (var r in redirects)
            {
                var gp = r.GamePath.Replace('\\', '/');
                var leaf = gp[(gp.LastIndexOf('/') + 1)..];
                if (!string.Equals(leaf, sourceLeaf, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(leaf, cutLeaf, StringComparison.OrdinalIgnoreCase)) continue;
                var m = System.Text.RegularExpressions.Regex.Match(gp, @"/v(\d{4})/[^/]+$");
                if (m.Success && int.TryParse(m.Groups[1].Value, out var f) && f > 0
                    && gp.StartsWith(item.MaterialDir(f), StringComparison.OrdinalIgnoreCase) && !folders.Contains(f))
                    folders.Add(f);
            }

        // File swaps on the material's paths.
        var materialPaths = row.Fingerprint.Folders.Select(x => GamePathAt(x.Folder)).Where(p => p.Length > 0).ToList();
        var swapText = string.Join("\n", swaps.Where(sw => materialPaths.Any(p => PenumbraModMeta.SameFile(sw.GamePath, p)))
                                              .Select(sw => $"{sw.Container.Group}|{sw.Container.Option}|{sw.Container.Index}|{sw.GamePath}|{sw.File}")
                                              .Order(StringComparer.OrdinalIgnoreCase));
        var swapSha = swapText.Length == 0 ? "" : HairPortPlan.Sha8(System.Text.Encoding.UTF8.GetBytes(swapText));
        if (swapSha != row.Fingerprint.SwapsSha8) return "swap";

        // A mod file's current Sha8, read only when its size or time differ from the recorded source of that same path.
        var shaCache = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        string? ShaOf(string rel, MatcapRecord.RowSource? hint = null)
        {
            if (shaCache.TryGetValue(rel, out var known)) return known;
            string? sha = null;
            try
            {
                var fi = new FileInfo(MatcapWriter.Full(modRoot, rel));
                if (fi.Exists)
                    sha = hint is { File: { } hf } && PenumbraModMeta.SameFile(hf, rel) && fi.Length == hint.Size && fi.LastWriteTimeUtc.Ticks == hint.MtimeUtc
                        ? hint.Sha8 : HairPortPlan.Sha8(File.ReadAllBytes(fi.FullName));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { sha = null; }
            shaCache[rel] = sha;
            return sha;
        }
        bool Unchanged(MatcapRecord.RowSource s)
        {
            if (s.Game) return readGameFile == null || (readGameFile(s.GamePath) is { } g && HairPortPlan.Sha8(g) == s.Sha8);
            return s.File == null || ShaOf(s.File, s) == s.Sha8;
        }
        foreach (var f in row.Files)
            if (!Unchanged(f.Normal) || (f.Index is { } ix && !Unchanged(ix))) return "map";

        MatcapRecord.RowFileRef? CutAt(PenumbraModMeta.ContainerRef c, string cutPath) =>
            redirects.Where(r => r.Container.SameAs(c) && PenumbraModMeta.SameFile(r.GamePath, cutPath))
                     .Select(r => row.Files.FirstOrDefault(x => PenumbraModMeta.SameFile(x.File, r.File))).FirstOrDefault(x => x != null);

        int worn = row.Fingerprint.Folders.FirstOrDefault()?.Folder ?? 1;
        foreach (var folder in folders)
        {
            var gp = GamePathAt(folder);
            var cutPath = CutPathAt(folder);
            if (gp.Length == 0 || cutPath.Length == 0) continue;
            var recordedCase = row.Fingerprint.Folders.FirstOrDefault(x => x.Folder == folder)?.Case;
            // A redirect to a file that isn't there draws nothing, cut or not: not a mismatch.
            var holders = redirects.Where(r => PenumbraModMeta.SameFile(r.GamePath, gp) && ShaOf(r.File) != null).ToList();
            var cutHolders = redirects.Where(r => PenumbraModMeta.SameFile(r.GamePath, cutPath)).ToList();

            if (holders.Count > 0)
            {
                // Every container serving the material serves the cut of that very content.
                foreach (var h in holders)
                {
                    var cut = CutAt(h.Container, cutPath);
                    if (cut == null || ShaOf(h.File) != cut.Source.Sha8) return "container";
                }
                // A container serving only the cut, while some container serves the material: it would win where it shouldn't.
                if (cutHolders.Any(c => !c.Container.IsDefault && !holders.Any(h => h.Container.SameAs(c.Container)))) return "container";
                continue;
            }

            if (recordedCase == MatcapRecord.RowFingerprint.CaseGame)
            {
                if (readGameFile == null) continue;   // can't tell without the game's file
                var dcut = CutAt(new PenumbraModMeta.ContainerRef(""), cutPath);
                if (readGameFile(gp) is not { } game || dcut == null || HairPortPlan.Sha8(game) != dcut.Source.Sha8) return "game";
                continue;
            }
            if (recordedCase == MatcapRecord.RowFingerprint.CaseWorn)
            {
                // The worn fallback: each cut holder is a worn container serving the cut of its worn file.
                var wornPath = GamePathAt(worn);
                foreach (var c in cutHolders)
                {
                    var cut = CutAt(c.Container, cutPath);
                    var wornFile = redirects.FirstOrDefault(r => r.Container.SameAs(c.Container) && PenumbraModMeta.SameFile(r.GamePath, wornPath)).File;
                    if (cut == null || wornFile == null || ShaOf(wornFile) != cut.Source.Sha8) return "worn";
                }
                continue;
            }
            // Recorded as served by containers (they're gone now) or never recorded: a cut left alone there is stale.
            if (recordedCase == MatcapRecord.RowFingerprint.CaseContainer && cutHolders.Count > 0) return "container";
        }
        return null;
    }

    // ── what recorded row matcaps depend on (asked by the gear shader upgrade) ──

    /// <summary>
    /// Why the matcap that holds a file can or can't simply be undone, as its own Undo would decide
    /// (<see cref="MatcapWriter.ClassifyForUndo"/>): a later feature changed its model, the model was edited on top, refits
    /// or hair ports still draw it (its Undo already ran and kept it), or none of these (an Undo clears it).
    /// </summary>
    internal enum RowHoldKind { Active, KeptForRegenerated, Edited, LaterFeature }

    /// <summary>A hold, with the model it names and (for <see cref="RowHoldKind.LaterFeature"/>) the later feature.</summary>
    internal sealed record RowHold(RowHoldKind Kind, string Model, string? Later);

    /// <summary>
    /// One recorded row matcap's dependencies: the mod files its cut was made from, the textures its cut files name, and its
    /// own models (classified only when its hold is asked for).
    /// </summary>
    internal sealed record RowEntryDeps(HashSet<string> Files, HashSet<string> CutTextures, IReadOnlyList<string> Models);

    /// <summary>What every recorded row matcap depends on; <see cref="Unreadable"/> holds everything.</summary>
    internal sealed class RowDependencies(bool unreadable, IReadOnlyList<RowEntryDeps> entries, Func<RowEntryDeps, RowHold> hold,
                                          bool manifestUnreadable = false)
    {
        public bool Unreadable { get; } = unreadable;
        /// <summary>Row matcaps are recorded but the mod's meta.json can't be read, so which option files their cuts follow
        /// can't be told: hold everything, as for an unreadable record.</summary>
        public bool ManifestUnreadable { get; } = manifestUnreadable;
        public IReadOnlyList<RowEntryDeps> Entries { get; } = entries;

        /// <summary>
        /// The hold on a gear shader upgrade entry, or null: its material or mask is a file a cut was made from, or its
        /// mask's game path is named by a recorded cut file (a content-addressed mask shared with a sibling). Only the
        /// matching row matcaps are classified; of several, the one that needs the most done first speaks.
        /// </summary>
        public RowHold? HoldOf(string rel, string? maskRel, string? maskGamePath)
        {
            rel = MeshVolumeService.Rel(rel);
            var mr = string.IsNullOrEmpty(maskRel) ? null : MeshVolumeService.Rel(maskRel);
            var mg = string.IsNullOrEmpty(maskGamePath) ? null : NormGamePath(maskGamePath);
            RowHold? best = null;
            foreach (var e in Entries)
            {
                if (!(e.Files.Contains(rel) || (mr != null && e.Files.Contains(mr)) || (mg != null && e.CutTextures.Contains(mg)))) continue;
                var h = hold(e);
                if (best == null || Rank(h.Kind) > Rank(best.Kind)) best = h;
            }
            return best;
        }

        private static int Rank(RowHoldKind k) => k switch
        {
            RowHoldKind.LaterFeature => 3, RowHoldKind.Edited => 2, RowHoldKind.KeptForRegenerated => 1, _ => 0,
        };
    }

    /// <summary>Test seam: called for every model <see cref="Dependencies"/> classifies (only with <c>classify</c>).</summary>
    [ThreadStatic] internal static Action<string>? AfterClassify;

    private static string NormGamePath(string p) => p.Replace('\\', '/').ToLowerInvariant();

    /// <summary>A gear item from a material game path (<c>chara/accessory/a0153/material/v0001/mt_c0201a0153_wrs_…</c>).</summary>
    private static VariantRepointPlan.Item? ItemOfMaterialPath(string gamePath)
    {
        var m = System.Text.RegularExpressions.Regex.Match(gamePath.Replace('\\', '/'),
            @"chara/(?:equipment|accessory)/[ea](\d{4})/material/v\d{4}/mt_c\d{4}[ea]\d{4}_([a-z]{3})",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success && int.TryParse(m.Groups[1].Value, out var id) ? VariantRepointPlan.ItemFor(m.Groups[2].Value.ToLowerInvariant(), id) : null;
    }

    /// <summary>
    /// What recorded row matcaps depend on, per entry (Pending and superseded ones too: only the matcap pane's Undo or
    /// Forget drops them): every mod file a cut was made from (source material, normal, index); every file a container
    /// that holds a cut redirects at the item's own material path of the same name (byte-identical files folded into one
    /// cut); and every texture a cut file names. With <paramref name="classify"/>, a matching entry's own models are
    /// classified when its hold is asked for, as its Undo would (<see cref="MatcapWriter.ClassifyForUndo"/>), to say what
    /// has to happen first; otherwise no model is read. Pure: no locks, no writes; IO failures read as "not there". Record
    /// paths are contained by <see cref="MatcapRecord.Read"/>; it ships in the mod, so null lists are read as empty.
    /// </summary>
    public static RowDependencies Dependencies(string modRoot, bool classify)
    {
        var record = MatcapRecord.Read(modRoot);
        if (record.Unreadable) return new RowDependencies(true, [], _ => new RowHold(RowHoldKind.Active, "", null));
        RowHold Hold(RowEntryDeps e) => classify ? Classify(modRoot, record, e.Models)
                                                 : new RowHold(RowHoldKind.Active, e.Models.FirstOrDefault() ?? "", null);
        var rows = record.Materials.Where(m => m?.Row != null).ToList();
        if (rows.Count == 0) return new RowDependencies(false, [], Hold);
        var redirects = PenumbraModMeta.TryReadContainerRedirects(modRoot);   // null: rule (4) is skipped, the rest stands
        var entries = new List<RowEntryDeps>();
        foreach (var mat in rows)
        {
            var row = mat.Row!;
            var rowFiles = (row.Files ?? []).Where(f => f != null).ToList();
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var textures = new HashSet<string>(StringComparer.Ordinal);
            foreach (var f in rowFiles)
                foreach (var s in new[] { f.Source, f.Normal, f.Index })
                    if (s is { Game: false, File: { Length: > 0 } sf }) files.Add(MeshVolumeService.Rel(sf));

            var models = (mat.Meshes ?? []).Concat(row.CutMeshes ?? []).Select(x => x?.Model ?? "")
                            .Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var cutRedirects = rowFiles.SelectMany(f => f.Redirects ?? []).Where(r => r != null).ToList();
            // Rule (4): what each cut redirect's container serves at the item's own material path of the source's name. The
            // item from a recorded model, else from the cut's own recorded game paths (a kept entry's models are gone).
            var item = models.Select(m => record.Model(m)?.GamePath).OfType<string>()
                             .Select(gp => MatcapPlan.TargetOf(gp, out _)?.Item).FirstOrDefault(x => x != null)
                       ?? cutRedirects.Select(r => ItemOfMaterialPath(r.GamePath ?? "")).FirstOrDefault(x => x != null);
            if (item is { } it && redirects != null && !string.IsNullOrEmpty(row.Cut))
            {
                var src = SourceNameOf(row.Cut).Replace('\\', '/');
                var leaf = src[(src.LastIndexOf('/') + 1)..];
                foreach (var rr in cutRedirects)
                {
                    var c = new PenumbraModMeta.ContainerRef(rr.Group ?? "", rr.Option, rr.Index);
                    foreach (var r in redirects)
                    {
                        if (!r.Container.SameAs(c)) continue;
                        var gp = r.GamePath.Replace('\\', '/');
                        if (!string.Equals(gp[(gp.LastIndexOf('/') + 1)..], leaf, StringComparison.OrdinalIgnoreCase)) continue;
                        var m = System.Text.RegularExpressions.Regex.Match(gp, @"/v(\d{4})/[^/]+$");
                        if (m.Success && int.TryParse(m.Groups[1].Value, out var folder) && folder > 0
                            && gp.StartsWith(it.MaterialDir(folder), StringComparison.OrdinalIgnoreCase))
                            files.Add(MeshVolumeService.Rel(r.File));
                    }
                }
            }

            foreach (var f in rowFiles)
                if (!string.IsNullOrEmpty(f.File) && ReadModFile(modRoot, f.File) is { } bytes && MatcapMaterial.ReadHeader(bytes) is { } h)
                    foreach (var t in h.Textures)
                        if (!string.IsNullOrEmpty(t)) textures.Add(NormGamePath(t));

            entries.Add(new RowEntryDeps(files, textures, models));
        }
        return new RowDependencies(false, entries, Hold, manifestUnreadable: redirects == null);
    }

    /// <summary>The hold's kind from the entry's own models, by priority: a later feature, edited on top, every model
    /// already undone (kept for refits or ports), else active (its Undo would succeed).</summary>
    private static RowHold Classify(string modRoot, MatcapRecord record, IReadOnlyList<string> models)
    {
        var classes = new List<(string Model, MatcapWriter.UndoClass Class, string? Later)>();
        foreach (var m in models)
        {
            AfterClassify?.Invoke(m);
            try
            {
                var c = MatcapWriter.ClassifyForUndo(modRoot, record, m, forgetOnly: false, out var later, out _);
                classes.Add((m, c, later));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { classes.Add((m, MatcapWriter.UndoClass.Restore, null)); }
        }
        if (classes.FirstOrDefault(x => x.Class == MatcapWriter.UndoClass.LaterFeature) is { Model: not null } lf)
            return new RowHold(RowHoldKind.LaterFeature, lf.Model, lf.Later);
        if (classes.FirstOrDefault(x => x.Class == MatcapWriter.UndoClass.Edited) is { Model: not null } ed)
            return new RowHold(RowHoldKind.Edited, ed.Model, null);
        if (classes.Count > 0 && classes.All(x => x.Class == MatcapWriter.UndoClass.AlreadyUndone))
            return new RowHold(RowHoldKind.KeptForRegenerated, classes[0].Model, null);
        return new RowHold(RowHoldKind.Active, models.FirstOrDefault() ?? "", null);
    }

    /// <summary>The shader every legacy gear material uses until the gear shader upgrade moves it to <see cref="CutShader"/>.</summary>
    public const string LegacyShader = "characterlegacy.shpk";

    /// <summary>
    /// Why a source's shader refuses the cut. A part still on <see cref="LegacyShader"/> (a mod file, while the Gear shaders
    /// setting is known) is told how the upgrade gets it onto <see cref="CutShader"/>: switch Gear shaders on, use "Upgrade
    /// what I'm wearing now" for a piece the user undid, or wear it once. Everything else keeps the plain text.
    /// </summary>
    internal static string ShaderRefusal(string where, string? shader, string? file, bool? gearShadersOn, IReadOnlyCollection<string> declined)
    {
        var text = LegacyShaderRefusal(where, shader, file, gearShadersOn, declined);
        // The place leads these sentences and reads "the option …": a sentence starts with a capital.
        return text.Length > 0 && char.IsLower(text[0])
            ? char.ToUpper(text[0], System.Globalization.CultureInfo.CurrentCulture) + text[1..] : text;
    }

    private static string LegacyShaderRefusal(string where, string? shader, string? file, bool? gearShadersOn, IReadOnlyCollection<string> declined)
    {
        if (file != null && gearShadersOn is { } on && string.Equals(shader, LegacyShader, StringComparison.OrdinalIgnoreCase))
        {
            // "Settings > Gear shaders" in the user's language, from the tab and section names themselves.
            var section = $"{ProteusLoc.Localize("Tab.Settings", "Settings")} > {ProteusLoc.Localize("Settings.Section.GearShaders", "Gear shaders")}";
            if (!on)
                return string.Format(ProteusLoc.Localize("Matcap.Refused.RowLegacyOff.Fmt",
                    "{0} is still on the old shader (characterlegacy.shpk), and a row matcap needs character.shpk. Switch on \"{1}\" under {2} and wear it once, then add the row matcap."),
                    where, ProteusLoc.Localize("Settings.GearShaders.AutoUpgrade.Label", "Upgrade old gear to Dawntrail shaders"), section);
            if (declined.Contains(MeshVolumeService.Rel(file), StringComparer.OrdinalIgnoreCase))
                return string.Format(ProteusLoc.Localize("Matcap.Refused.RowLegacyDeclined.Fmt",
                    "{0} is still on the old shader (characterlegacy.shpk) because you undid its upgrade, and a row matcap needs character.shpk. Wear it and press \"{1}\" under {2}, then add the row matcap."),
                    where, ProteusLoc.Localize("Settings.GearShaders.UpgradeNow", "Upgrade what I'm wearing now"), section);
            return string.Format(ProteusLoc.Localize("Matcap.Refused.RowLegacyWear.Fmt",
                "{0} is still on the old shader (characterlegacy.shpk), and a row matcap needs character.shpk. Wear it once so Gear shaders moves it to character.shpk, then add the row matcap. If it stays on the old shader, hover the count under {1} to see why."),
                where, section);
        }
        return string.Format(ProteusLoc.Localize("Matcap.Refused.RowShader.Fmt",
            "A row matcap needs the part on character.shpk, and {0} uses {1}."), where, shader ?? "?");
    }

    /// <summary>"15", "15 A" or "15 B", for messages.</summary>
    public static string RowLabel(int row, SubRowPick sub) => sub switch { SubRowPick.A => $"{row} A", SubRowPick.B => $"{row} B", _ => $"{row}" };

    private static (Plan?, Refusal?) Refuse(string code, string message) => (null, new Refusal(code, message));

    /// <summary>"the option Red of Colour", "default data", "container 2 of Extras".</summary>
    public static string Describe(PenumbraModMeta.ContainerRef c) =>
        c.IsDefault ? ProteusLoc.Localize("Matcap.Row.DefaultData", "the mod's default files")
        : c.Option != null ? string.Format(ProteusLoc.Localize("Matcap.Row.Option.Fmt", "the option \"{1}\" of \"{0}\""), c.Group, c.Option)
        : string.Format(ProteusLoc.Localize("Matcap.Row.Container.Fmt", "entry {1} of \"{0}\""), c.Group, (c.Index ?? 0) + 1);

    private static byte[]? ReadModFile(string modRoot, string rel)
    {
        try
        {
            var full = MatcapWriter.Full(modRoot, rel);
            return File.Exists(full) ? File.ReadAllBytes(full) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>A texture as <paramref name="container"/> sees it: its own redirect, else default data's, else the game's.</summary>
    private static (byte[]? Bytes, PenumbraModMeta.ContainerRef? Container, string? File) ResolveTexture(
        Inputs i, List<PenumbraModMeta.ContainerRedirect> redirects, PenumbraModMeta.ContainerRef? container, string gamePath)
    {
        foreach (var pick in new[] { container, new PenumbraModMeta.ContainerRef("") })
        {
            if (pick is not { } c) continue;
            var r = redirects.FirstOrDefault(x => x.Container.SameAs(c) && PenumbraModMeta.SameFile(x.GamePath, gamePath));
            if (r.File is { Length: > 0 } && ReadModFile(i.ModRoot, r.File) is { } b) return (b, c, r.File);
        }
        return (i.Env.ReadGameFile(gamePath), null, null);
    }

    private static MatcapRecord.RowSource SourceRef(string modRoot, PenumbraModMeta.ContainerRef? container, string? file,
                                                    string gamePath, byte[] bytes)
    {
        var s = new MatcapRecord.RowSource
        {
            GamePath = gamePath, Game = file == null, File = file, Group = container?.Group, Option = container?.Option,
            Index = container?.Index, Sha8 = HairPortPlan.Sha8(bytes), Size = bytes.LongLength,
        };
        if (file != null)
            try { s.MtimeUtc = File.GetLastWriteTimeUtc(MatcapWriter.Full(modRoot, file)).Ticks; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* no time: Stale hashes it */ }
        return s;
    }

    /// <summary>Group name → (single-choice?, option names), from a v4 manifest. Null when unreadable; empty for none.</summary>
    private static Dictionary<string, (bool Single, List<string> Options)>? ReadGroups(string modRoot)
    {
        var map = new Dictionary<string, (bool, List<string>)>(StringComparer.OrdinalIgnoreCase);
        var path = Path.Combine(modRoot, PenumbraModMeta.MetaFile);
        if (!File.Exists(path)) return map;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("Groups", out var gs) || gs.ValueKind != JsonValueKind.Array) return map;
            foreach (var g in gs.EnumerateArray())
            {
                if (g.ValueKind != JsonValueKind.Object || !g.TryGetProperty("Name", out var n) || n.ValueKind != JsonValueKind.String) continue;
                bool single = g.TryGetProperty("Type", out var t) && t.ValueKind == JsonValueKind.String
                              && string.Equals(t.GetString(), "Single", StringComparison.OrdinalIgnoreCase);
                var opts = new List<string>();
                if (g.TryGetProperty("Options", out var os) && os.ValueKind == JsonValueKind.Array)
                    foreach (var o in os.EnumerateArray())
                        if (o.ValueKind == JsonValueKind.Object && o.TryGetProperty("Name", out var on) && on.ValueKind == JsonValueKind.String)
                            opts.Add(on.GetString() ?? "");
                map[n.GetString() ?? ""] = (single, opts);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
        return map;
    }

    // ── the bake ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The cut normal: the source normal at the larger of its and the index map's size, blue 0 on the row's texels; off the
    /// row, blue 255 for a threshold-0 source, else as authored. Written BC7 (uncompressed for a preview, or when the
    /// encoder budget refuses). The row comes from the index map (red = row pair, green = sub-row A at 255) or, without
    /// one, from the normal's alpha.
    /// </summary>
    private static ((bool[] Mask, byte[] Blue, int W, int H, int RowTexels)?, string Why) BakeOne(
        Inputs i, byte[] normalBytes, byte[]? indexBytes, bool thresholdZero, float threshold, string outPath, string what)
    {
        var n = i.Env.Loader.LoadTexBytesAsRgba(normalBytes, what);
        if (n is not { } nt) return (null, ProteusLoc.Localize("Matcap.Refused.RowBake.Normal", "the normal map couldn't be read"));
        (byte[] rgba, int width, int height)? id = null;
        if (indexBytes != null)
        {
            id = i.Env.Loader.LoadTexBytesAsRgba(indexBytes, what + " (index)");
            if (id == null) return (null, ProteusLoc.Localize("Matcap.Refused.RowBake.Index", "the index map couldn't be read"));
        }
        var (rgba, w, h, mask, blue, count) = Cut(nt.rgba, nt.width, nt.height, id?.rgba, id?.width ?? 0, id?.height ?? 0,
                                                  i.Row, i.Sub, thresholdZero, i.Env.Loader);
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        var encoding = i.Preview ? TexEncoding.Uncompressed
            : (i.Affordable ?? i.Env.Loader.CompressionAffordable)() ? TexEncoding.Bc7 : TexEncoding.Uncompressed;
        if (!i.Preview && encoding == TexEncoding.Uncompressed)
            Plugin.Log?.Information("[Proteus] row matcap: BC7 is too slow on this machine, so the cut normal is written uncompressed");
        if (!i.Env.Loader.WriteTex(rgba, w, h, outPath, encoding))
            return (null, ProteusLoc.Localize("Matcap.Refused.RowBake.Write", "the cut material couldn't be written"));
        return ((mask, blue, w, h, count), "");
    }

    /// <summary>The cut normal's pixels, the row mask, the source blue per texel and how many texels the row holds.</summary>
    internal static (byte[] Rgba, int W, int H, bool[] Mask, byte[] Blue, int Count) Cut(
        byte[] normal, int nw, int nh, byte[]? index, int iw, int ih, int row, SubRowPick sub, bool thresholdZero, TextureLoader? loader)
    {
        int w = Math.Max(nw, index != null ? iw : 0), h = Math.Max(nh, index != null ? ih : 0);
        var src = (nw, nh) == (w, h) ? normal
            : loader != null ? loader.ScaleRgba(normal, nw, nh, w, h) : NearestScale(normal, nw, nh, w, h);
        var outp = (byte[])src.Clone();
        var mask = new bool[w * h];
        var blue = new byte[w * h];
        int count = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int p = (y * w + x) * 4;
                blue[y * w + x] = src[p + 2];
                bool on;
                if (index != null)
                {
                    int q = ((y * ih / h) * iw + x * iw / w) * 4;
                    on = ContentIndexTexture.RowOf(index[q]) == row
                         && (sub == SubRowPick.Both || (index[q + 1] > 127) == (sub == SubRowPick.A));
                }
                else on = ContentIndexTexture.RowOf(src[p + 3]) == row;
                if (on) { outp[p + 2] = 0; mask[y * w + x] = true; count++; }
                else if (thresholdZero) outp[p + 2] = 255;
            }
        return (outp, w, h, mask, blue, count);
    }

    private static byte[] NearestScale(byte[] src, int sw, int sh, int dw, int dh)
    {
        var o = new byte[dw * dh * 4];
        for (int y = 0; y < dh; y++)
            for (int x = 0; x < dw; x++)
                Buffer.BlockCopy(src, ((y * sh / dh) * sw + x * sw / dw) * 4, o, (y * dw + x) * 4, 4);
        return o;
    }

    // ── triangles ────────────────────────────────────────────────────────────

    /// <summary>
    /// Whether a triangle touches a row texel in any bake: the bounding box of its RAW UVs, mapped to texel indices that
    /// are then wrapped modulo the mask size (so a triangle straddling u = 1 still covers column 0). Conservative.
    /// </summary>
    public static bool Touches(ModelMaterialWriter.TriangleUv t, IReadOnlyList<Bake> bakes)
    {
        foreach (var b in bakes)
            if (b.RowTexels > 0 && Covered(t, b.W, b.H, (x, y) => b.Mask[y * b.W + x])) return true;
        return false;
    }

    /// <summary>
    /// Whether an alpha-tested bake has an off-row texel already cut (blue below its threshold) inside this kept triangle's
    /// footprint: such a hole will show the matcap beneath.
    /// </summary>
    public static bool HoleInside(ModelMaterialWriter.TriangleUv t, IReadOnlyList<Bake> bakes)
    {
        foreach (var b in bakes)
        {
            if (b.ThresholdZero) continue;
            byte cut = (byte)Math.Clamp(MathF.Ceiling(b.Threshold * 255f), 0f, 255f);
            if (Covered(t, b.W, b.H, (x, y) => !b.Mask[y * b.W + x] && b.Blue[y * b.W + x] < cut)) return true;
        }
        return false;
    }

    private static bool Covered(ModelMaterialWriter.TriangleUv t, int w, int h, Func<int, int, bool> hit)
    {
        if (w <= 0 || h <= 0) return false;
        double minU = Math.Min(t.U0, Math.Min(t.U1, t.U2)), maxU = Math.Max(t.U0, Math.Max(t.U1, t.U2));
        double minV = Math.Min(t.V0, Math.Min(t.V1, t.V2)), maxV = Math.Max(t.V0, Math.Max(t.V1, t.V2));
        // A garbage UV (±3.4e38, which some exports carry on hidden triangles) or a non-finite one samples nothing real:
        // such a triangle touches nothing, so it neither joins the row nor reports a hole.
        if (!Sane(minU) || !Sane(maxU) || !Sane(minV) || !Sane(maxV)) return false;
        // In doubles, so no texel index overflows: a footprint a texture wide or more is the whole width, anything else wraps.
        var (x0, xs) = Footprint(minU, maxU, w);
        var (y0, ys) = Footprint(minV, maxV, h);
        for (int j = 0; j < ys; j++)
        {
            int wy = (y0 + j) % h;
            for (int i = 0; i < xs; i++)
                if (hit((x0 + i) % w, wy)) return true;
        }
        return false;
    }

    /// <summary>UVs beyond this many tiles from the origin are garbage, not a texture repeat.</summary>
    public const double MaxUvTiles = 1e4;

    private static bool Sane(double v) => double.IsFinite(v) && Math.Abs(v) <= MaxUvTiles;

    /// <summary>The first wrapped texel and the texel count (at most <paramref name="size"/>) a UV span covers.</summary>
    private static (int Start, int Count) Footprint(double min, double max, int size)
    {
        double f0 = Math.Floor(min * size), f1 = Math.Floor(max * size);
        if (f1 - f0 >= size - 1) return (0, size);
        int start = (int)(((f0 % size) + size) % size);
        return (start, (int)(f1 - f0) + 1);
    }
}
