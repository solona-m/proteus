using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace Proteus.Services;

/// <summary>
/// Conversions between the women's layouts and TBSE's, through the bibo↔TBSE maps — by an explicit table, so a pair no
/// file covers is never probed for (a probe of <c>gen3_to_tbse</c> would warn on every call: <see cref="GetMap"/> does
/// not remember a miss).
/// </summary>
public partial class UVRemapService
{
    /// <summary>The bibo↔TBSE maps, downloaded on demand (see <see cref="UVMapDownloadService"/>).</summary>
    internal const string BiboToTbseFile = "bibo_to_tbse_transfer.tif", TbseToBiboFile = "tbse_to_bibo_transfer.tif";

    /// <summary>
    /// The maps a conversion between two DOUBLED spaces goes through, in order; empty when they are one space, null when
    /// no shipped map joins them. gen3 and TBSE meet through bibo, as two maps; nothing else chains.
    /// </summary>
    internal static IReadOnlyList<(string From, string To)>? Hops(string from, string to)
    {
        static bool Is(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        if (Is(from, to)) return [];
        if ((Is(from, "bibo") && (Is(to, "gen3") || Is(to, TbseSpace))) || (Is(to, "bibo") && (Is(from, "gen3") || Is(from, TbseSpace))))
            return [(from.ToLowerInvariant(), to.ToLowerInvariant())];
        if (Is(from, "gen3") && Is(to, TbseSpace)) return [("gen3", "bibo"), ("bibo", TbseSpace)];
        if (Is(from, TbseSpace) && Is(to, "gen3")) return [(TbseSpace, "bibo"), ("bibo", "gen3")];
        return null;
    }

    /// <summary>
    /// <paramref name="rgba"/>, art in layout <paramref name="src"/>, carried into layout <paramref name="dst"/>: a mirrored
    /// source expanded into its doubled sheet, the maps of <see cref="Hops"/> applied at 4096, a mirrored destination
    /// cropped to its right half (2048×4096). Null when a map it needs is not there. A doubled source must be
    /// <paramref name="sheet"/>², the maps' own size.
    /// </summary>
    internal (byte[] Rgba, int W, int H)? ConvertSheet(byte[] rgba, int w, int h, string src, string dst, int sheet = 4096)
    {
        int S = sheet;
        string from = DoubledSpaceOf(src) ?? src, to = DoubledSpaceOf(dst) ?? dst;
        var hops = Hops(from, to);
        if (hops == null) return null;

        byte[] image;
        if (DoubledSpaceOf(src) != null) image = ExpandMirrored(rgba, w, h, S, S);
        else if (w == S && h == S) image = rgba;
        else return null;

        foreach (var (a, b) in hops)
        {
            var next = Remap(image, S, S, a, b);
            // The map was not there, or is not the sheet's size.
            if (ReferenceEquals(next, image) || next.Length != S * S * 4) return null;
            image = next;
        }
        return DoubledSpaceOf(dst) != null ? (CropRightHalf(image, S, S), S / 2, S) : (image, S, S);
    }

    /// <summary>
    /// A per-vertex converter between any two layouts <see cref="Hops"/> joins, chaining two of
    /// <see cref="UvConverter"/>'s through bibo where it must; null where no shipped map joins them.
    /// <see cref="UvConverter"/> itself stays chain-free: refit never takes this.
    /// </summary>
    internal UvConversion? RouteConverter(string from, string to, bool unmirror = false)
    {
        string a = DoubledSpaceOf(from) ?? from, b = DoubledSpaceOf(to) ?? to;
        var hops = Hops(a, b);
        if (hops == null) return null;
        if (hops.Count <= 1) return UvConverter(from, to, unmirror);
        var first = UvConverter(from, "bibo", unmirror);
        var second = UvConverter("bibo", to);
        if (first == null || second == null) return null;
        return (u, v, side) => first(u, v, side) is { } mid ? second(mid.U, mid.V, side) : null;
    }

    private string MapPath(string file) => Path.Combine(mapsDir, file);

    /// <summary>The map files a route goes through, for a log line ("bibo_to_tbse_transfer.tif", or two for a chained route).</summary>
    internal static string MapFilesOf(MensRoute route)
    {
        var hops = Hops(DoubledSpaceOf(route.Src) ?? route.Src, DoubledSpaceOf(route.Dst) ?? route.Dst);
        return hops == null ? "no map" : string.Join(", ", hops.Select(h => $"{h.From}_to_{h.To}_transfer.tif"));
    }

    /// <summary>Whether both bibo↔TBSE maps are on disk. Asked once per composite, which takes the answer for its whole run.</summary>
    internal bool MenMapsPresent()
        => new FileInfo(MapPath(BiboToTbseFile)) is { Exists: true, Length: > 0 }
           && new FileInfo(MapPath(TbseToBiboFile)) is { Exists: true, Length: > 0 };

    private readonly HashSet<MensRoute> missingRoutes = [];

    /// <summary>
    /// Whether every map <paramref name="route"/> needs is on disk and none has failed to load since the maps last
    /// arrived. Looks at files only: a miss costs no load and logs nothing.
    /// </summary>
    internal bool MensRouteAvailable(MensRoute route)
    {
        lock (missingRoutes) if (missingRoutes.Contains(route)) return false;
        string from = DoubledSpaceOf(route.Src) ?? route.Src, to = DoubledSpaceOf(route.Dst) ?? route.Dst;
        var hops = Hops(from, to);
        if (hops == null) return false;
        foreach (var (a, b) in hops)
            if (new FileInfo(MapPath($"{a}_to_{b}_transfer.tif")) is not { Exists: true, Length: > 0 }) return false;
        return true;
    }

    /// <summary>A route whose conversion failed (a map that would not load): not tried again until the maps arrive anew.</summary>
    internal void MarkRouteMissing(MensRoute route)
    {
        lock (missingRoutes) missingRoutes.Add(route);
    }

    /// <summary>Forgets every failed route — called when maps have just been downloaded.</summary>
    internal void ForgetMissingHops()
    {
        lock (missingRoutes) missingRoutes.Clear();
    }

    private int menMapsRequested;

    /// <summary>Raised, once a session, the first time a man's material meets a woman's art and the maps are not here.</summary>
    internal event Action? MenMapsWanted;

    /// <summary>Asks for the bibo↔TBSE maps; only the first call of a session raises <see cref="MenMapsWanted"/>.</summary>
    internal void RequestMenMaps()
    {
        if (Interlocked.Exchange(ref menMapsRequested, 1) != 0) return;
        log.Information("[Proteus] men's maps requested");
        MenMapsWanted?.Invoke();
    }
}
