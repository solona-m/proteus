using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using Proteus.Services.Matcap;

namespace Proteus.Services;

/// <summary>
/// Proteus's own matcap set (pinned release assets, like the starter effects), fetched into the config directory the
/// first time the Studio's Matcaps pane is shown — never at plugin load — and listed from there. Nothing is seeded into
/// the user's own matcap folder.
/// </summary>
public sealed class DefaultMatcapsDownloadService : IMatcapPack, IDisposable
{
    /// <summary>
    /// Immutable by contract, like the effects tag: bump it whenever <see cref="Matcaps"/> changes, and never re-upload
    /// assets under an existing tag, or a mirror keeps serving the old bytes. upload-matcaps.yml publishes it.
    /// </summary>
    internal const string MatcapsTag = "matcaps-v1";

    /// <summary>
    /// Shipped loose (zip bytes are not reproducible, so a pinned checksum would break). Must equal the committed
    /// <c>Proteus/DefaultMatcaps</c> folder byte for byte (<c>DefaultMatcapsDownloadServiceTests</c>).
    /// </summary>
    internal static readonly (string Name, long Bytes, string Sha256)[] Matcaps =
    [
        ("Amber Orange.png", 325396L, "87eeff2ab056bd163fda699a4e51db38b31129f172507792508c0ad33ca1fb6e"),
        ("apple.png", 362170L, "cec04620a42b381568ce8b669ec5517782360deb4a665ccb9e545e29ad054c7e"),
        ("Beige Travertine.png", 260873L, "f393a14a98b9581fc526313a32abba0961bae15a35d6d2029277c9fa0d0a0996"),
        ("Brushed Steel.png", 136747L, "65e0bea2d87396fd345b5f8c177e1973d04dde7e5ba1013c337b1709c56900ab"),
        ("Copper Metal.png", 286071L, "7409c8d65a1e1a1dd5aa2afd41ef80b3bc772b88d1c26aaaa6f53dd6d553b682"),
        ("copper.png", 39467L, "bd6bb3b9d7e6462d0ba9579e1b5791c53c1d169f5e4231f3b6dbb5ad6be662c6"),
        ("Coral Gloss.png", 148296L, "1e0505f23c01986b8a122c712ac426280bb0214ca766d7c5310fe4891f550a3c"),
        ("Cornflower Blue.png", 198144L, "b6213f0eb8eaac9ed457dcf5d2effc9459560100fbb09423fcc84f41070dde39"),
        ("Cream Speckled Stone.png", 287652L, "59e7d20b1725576cf8c60637e52c033cacefde3ca44f4f760b478d9cea7ae64b"),
        ("embossed.png", 489673L, "51b278e1f17923508a43d82fd8473f16f7ad814adbe9245112310472373a1ede"),
        ("Gold Car Paint.png", 264461L, "acb9ff6e0ce49d20c4549b09c2eef948c5da0646401edb75dc72e84659b927cc"),
        ("Gold Metal.png", 206616L, "d8bf4a3bdf237b97522daf5da1a872417f26bc793534a2643098e38c169f88ef"),
        ("Grey Glass Car Paint.png", 107547L, "f678aa9f40283600d76500f9dd3cedbdb57d611cc4e446c9b8f3ce06a526cf5e"),
        ("Ice Blue Marble.png", 185749L, "35152f02ae2d7c8c3ca4af2ec4779a4a25de9a95009af43a55d56d0b19836510"),
        ("Light Leak 17.png", 214550L, "99069232641639d172569bc045b498c7eae3403add278a083997ca3a5660cf8b"),
        ("Light Leak 3.png", 230788L, "b55dfc2d85cff04b3b4a62a4ae31af1e814a7cf9644235094273ad01494c3574"),
        ("Light Leak 30.png", 200043L, "280dc3bd9deb7bc30439966116c425d01ac7d93b7da6f066a5277b7f3a20f1fd"),
        ("Light Leak 36.png", 218318L, "589662caec03142c17ed015781472b5486725b207ad88f6e2ab3bcf707f8c343"),
        ("Light Leak 39.png", 207765L, "129a83a9bceab13289fafe467ab385bcf5c87b5bdf0b0a0a023d51ff3b62d2c6"),
        ("Light Leak 44.png", 221685L, "80893dfafd26be8414a25faa74358649c639dbff33674eabd6b2d5a88bb3022a"),
        ("Light Leak 5.png", 216361L, "cabfbf36cb2db39d3c113955eac12e9a2dbe8f58ca22f4c6a9bd53f74073048a"),
        ("Light Leak 8.png", 202217L, "4bb800e07894039f16365329bb2c2b3e7b024084de75b1595f0a6750985cf8e6"),
        ("Lime Green Satin.png", 267167L, "41cf61382fcc144104f1e839f7b4be7573824024ef363a7eca095e7ab0ac44dc"),
        ("matte red.png", 225400L, "769973cb8239f951252a11cfcc8773118c9d6d9b1646982d3eb1a40ec7d9e4cb"),
        ("motherofpearl.png", 303724L, "ac7df66db155e07ddf92dfe01589d94bde6fa18d243d2448e829470a65bc02ab"),
        ("Peach Satin.png", 336647L, "4f3120f333f83cb2b4607673f142c387596de3efc9b8094637b720c180f4577f"),
        ("Pearl White.png", 125388L, "7f229a849aa2f2da5038a5b13492e65f03386177540e681614b8b7549a43aaf3"),
        ("Pink Pearl.png", 126282L, "5450b2325fa1ce7356bccab11470dfa047970dc2fecf143aaf9cca80eb17e8e4"),
        ("rainbow.png", 273721L, "45fbbe3f0ea4b2ff1733b2a6076fb22a498f52bee38c92e37d3cd101bdf41bdd"),
        ("redgold.png", 496717L, "5dd6bd118d20e7a2de107a29e7792291a952575ca73f4191b93d95ca1e1dae9b"),
        ("Saffron Yellow.png", 284055L, "7211bc3d8e35ae5dd9db56a62bd1dca0d38df1904bf94acaad0d2ac6b623a54f"),
        ("Sage Grey Car Paint.png", 95895L, "e85f3c6b6e089d76718b7faa3905f6b78f3ea3dc09afbea1107091a3e41ec621"),
        ("Sand Satin.png", 269564L, "680868d96b209f2fa7e33d37f5912a348c2ae0efcab78505057c5aa96e19eb9a"),
        ("Silver Satin.png", 201668L, "941b6c669a4e1726c7ac4e0ffb036d47b3b06362a9e4a692af407d5fd461f395"),
        ("Slate Blue Grey.png", 240744L, "c2444bf12c4b6391dac32e78bb5310241d93ab73a9dda07b8321c54cafe4196d"),
        ("Teal Blue Gloss.png", 414255L, "813a1be5535856334b5b661f9ff8f536e91eb572ebb68d78095d5295821230f8"),
        ("Tomato Red.png", 304945L, "ca500efdb536181a54a0f0848c8bf3d8ff6d8896ac3dfdbb2954d04ccc061447"),
        ("Turquoise Satin.png", 205224L, "1172473ea62842d6bbf7a98f3518dce5d86e4d17f7c13d990b21e036101217fb"),
        ("White Marble.png", 99579L, "b2d143ed054136df7207aa642a91672f2d53d782a00169c5fe0578e774f7d7bf"),
    ];

    /// <summary>The asset name to ask for: GitHub rewrites spaces, and upload-matcaps.yml uploads with this substitution.</summary>
    internal static string RemoteName(string localName) => localName.Replace(' ', '.');

    /// <summary>Fetches one file: the seam tests replace; <see cref="ResilientDownloader.FetchAsync"/> otherwise.</summary>
    internal delegate Task<FetchResult> Fetch(string[] baseUrls, string fileName, long expectedBytes, string expectedSha,
                                              string destPath, Action<long>? onProgress, CancellationToken ct);

    private readonly IPluginLog log;
    private readonly ResilientDownloader? downloader;
    private readonly Fetch fetch;
    private readonly string dir;
    private readonly CancellationTokenSource cts = new();
    private readonly object gate = new();
    private MatcapPackStatus status;
    private bool running;
    private bool started;
    private long version;

    /// <summary>Where the matcaps are kept: <c>&lt;config&gt;\DefaultMatcaps</c>.</summary>
    public string Directory => dir;

    public DefaultMatcapsDownloadService(IPluginLog log, string dataDir) : this(log, dataDir, null) { }

    internal DefaultMatcapsDownloadService(IPluginLog log, string dataDir, Fetch? fetch)
    {
        this.log = log;
        if (fetch == null) downloader = new ResilientDownloader(log);
        this.fetch = fetch ?? downloader!.FetchAsync;
        dir = Path.Combine(dataDir, "DefaultMatcaps");
        status = new MatcapPackStatus(MatcapPackState.Idle, 0, Matcaps.Length, "");
    }

    public MatcapPackStatus Status { get { lock (gate) return status; } }

    public long Version => Interlocked.Read(ref version);

    /// <summary>A run is under way (tests: the final state is never published while one still is).</summary>
    internal bool IsRunActive { get { lock (gate) return running; } }

    /// <summary>The state and whether a run is active, read together (tests: a run is active exactly while Downloading).</summary>
    internal (MatcapPackState State, bool Running) Snapshot() { lock (gate) return (status.State, running); }

    public IReadOnlyList<MatcapImage> Available()
    {
        var list = new List<MatcapImage>();
        foreach (var (name, bytes, _) in Matcaps)
        {
            var p = Path.Combine(dir, name);
            if (AtPinnedSize(p, bytes)) list.Add(new MatcapImage(name, p, false, FromPack: true));
        }
        return list.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public void EnsureAsync(bool retryFailed = false, bool evenIfReady = false)
    {
        lock (gate)
        {
            if (running) return;
            bool rearm = (retryFailed && status.State == MatcapPackState.Failed)
                         || (evenIfReady && status.State is MatcapPackState.Ready or MatcapPackState.Failed);
            if (started && !rearm) return;
            started = true;
            running = true;
            status = status with { State = MatcapPackState.Downloading, Detail = "", Have = CountHave() };
            Interlocked.Increment(ref version);
        }
        Task.Run(Run);
    }

    private async Task Run()
    {
        var end = MatcapPackState.Ready;
        string detail = "";
        try
        {
            System.IO.Directory.CreateDirectory(dir);
            SetHave();
            foreach (var (name, bytes, sha) in Matcaps)
            {
                cts.Token.ThrowIfCancellationRequested();
                var dest = Path.Combine(dir, name);
                if (AtPinnedSize(dest, bytes)) continue;

                var r = await fetch(ProteusAssets.BaseUrls(MatcapsTag), RemoteName(name), bytes, sha, dest, null, cts.Token)
                    .ConfigureAwait(false);
                if (r.Ok)
                {
                    SetHave();
                    Interlocked.Increment(ref version);
                    continue;
                }
                end = MatcapPackState.Failed;
                detail = $"{name}: {r.Detail}";
                log.Warning("[Proteus] Matcap {0} unavailable ({1})", name, r.Detail);
                // Only an asset that is not there (404/410) is skipped; anything else — the network, a throttled or
                // failing origin, a checksum that never matched — stops the run, which Try again resumes.
                bool absent = r.Failure == FetchFailure.Http && r.StatusCode is 404 or 410;
                if (!absent) break;
            }
            if (end == MatcapPackState.Ready) log.Information("[Proteus] Proteus's matcaps are ready.");
        }
        catch (Exception ex) when (cts.IsCancellationRequested)
        {
            _ = ex;   // plugin unloading: not a failure
            end = MatcapPackState.Idle;
        }
        catch (Exception ex)
        {
            end = MatcapPackState.Failed;
            detail = ex.Message;
            log.Warning(ex, "[Proteus] Downloading Proteus's matcaps failed");
        }
        finally
        {
            int have = CountHave();
            lock (gate)
            {
                running = false;
                status = status with { State = end, Detail = detail, Have = have };
                Interlocked.Increment(ref version);
            }
        }
    }

    private void SetHave()
    {
        int have = CountHave();
        lock (gate) status = status with { Have = have };
    }

    private int CountHave() => Matcaps.Count(m => AtPinnedSize(Path.Combine(dir, m.Name), m.Bytes));

    /// <summary>Present means at the pinned size: a truncated or other-revision file is fetched again (and not listed).</summary>
    private static bool AtPinnedSize(string path, long bytes)
    {
        try { return File.Exists(path) && new FileInfo(path).Length == bytes; }
        catch { return false; }
    }

    /// <summary>Cancels any in-flight fetch and releases the HTTP client, which load-context unloadability requires.</summary>
    public void Dispose()
    {
        cts.Cancel();
        downloader?.Dispose();
    }
}
