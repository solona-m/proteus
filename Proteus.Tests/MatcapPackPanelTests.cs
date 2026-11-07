using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using NSubstitute;
using Proteus.Gui;
using Proteus.Localization;
using Proteus.Services;
using Proteus.Services.Matcap;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// Proteus's own matcaps in the Matcaps pane: the third source, its status line, the relist rule, name resolution against
/// the user's images, baking, and that the pack never ships in the plugin's output.
/// </summary>
public sealed class MatcapPackPanelTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "proteus-mcpanel-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(root, true); } catch { }
    }

    private static string Repo => DefaultMatcapsDownloadServiceTests.RepoRoot();

    private static string Source(string rel) => File.ReadAllText(Path.Combine(Repo, rel));

    /// <summary>A pack whose version and list the test sets, logging the order it is read in.</summary>
    private sealed class FakePack : IMatcapPack
    {
        public long V;
        public List<MatcapImage> List = [];
        public int AvailableCalls;
        public readonly List<string> Log = [];
        public Action? DuringAvailable;
        public MatcapPackStatus Status { get; set; } = new(MatcapPackState.Ready, 0, 39, "");
        public long Version { get { Log.Add("version"); return V; } }
        public IReadOnlyList<MatcapImage> Available()
        {
            Log.Add("available");
            AvailableCalls++;
            var snapshot = List.ToList();
            DuringAvailable?.Invoke();
            return snapshot;
        }
        public void EnsureAsync(bool retryFailed = false, bool evenIfReady = false) { }
    }

    private static MatcapPackStatus St(MatcapPackState s, int have = 0) => new(s, have, 39, "");

    private static MatcapPanel.Context Ctx(IReadOnlyList<MatcapImage> images, IReadOnlyList<MatcapImage> pack) => new()
    {
        Penumbra = true, Busy = false, Ticked = [], MaterialRoom = 1, Images = images, ImageFolder = null, Libraries = null,
        Scanning = false, ModsRoot = null, Record = new MatcapRecord(), States = new Dictionary<string, MatcapWriter.State>(),
        Entries = [], GroupFiles = [], LibraryStatus = _ => null!, Apply = (_, _) => { }, Change = (_, _) => { }, Undo = () => { },
        Forget = () => { }, Rescan = () => { }, RefreshImages = () => { }, PackImages = pack,
    };

    [Fact]
    [Trait("Plan", "AC-matcap-pack-1")]
    public void Plugin_never_starts_the_matcap_download_at_load_and_DrawMatcaps_does()
    {
        var plugin = Source("Proteus/Plugin.cs");
        Assert.Contains("new DefaultMatcapsDownloadService(", plugin);
        Assert.Contains("partsPanel.UseMatcapPack(matcapPackDl)", plugin);
        Assert.DoesNotContain("matcapPackDl.EnsureAsync", plugin);
        var panel = Source("Proteus/Gui/PartsPanel.Matcaps.cs");
        var draw = panel[panel.IndexOf("private void DrawMatcaps()", StringComparison.Ordinal)..];
        Assert.Contains("matcapPack?.EnsureAsync(", draw[..draw.IndexOf("matcapPanel.Draw(", StringComparison.Ordinal)]);
    }

    [Fact]
    [Trait("Plan", "AC-matcap-pack-2")]
    public void PackLine_reads_downloading_n_of_total()
    {
        Assert.Equal(string.Format(Strings.Matcap.PackDownloadingFmt, 12, 39),
                     MatcapPanelModel.PackLine(St(MatcapPackState.Downloading), 12));
        Assert.Equal(string.Format(Strings.Matcap.PackDownloadingFmt, 0, 39),
                     MatcapPanelModel.PackLine(St(MatcapPackState.Idle), 0));
    }

    [Fact]
    [Trait("Plan", "AC-matcap-pack-3")]
    public void PackLine_rules()
    {
        foreach (var s in new[] { MatcapPackState.Idle, MatcapPackState.Downloading, MatcapPackState.Ready })
            Assert.Null(MatcapPanelModel.PackLine(St(s), 39));
        Assert.Null(MatcapPanelModel.PackLine(St(MatcapPackState.Ready), 38));
        Assert.Equal(string.Format(Strings.Matcap.PackDownloadingFmt, 38, 39),
                     MatcapPanelModel.PackLine(St(MatcapPackState.Downloading), 38));
        Assert.Equal(Strings.Matcap.PackFailed, MatcapPanelModel.PackLine(St(MatcapPackState.Failed), 39));
        Assert.Equal(Strings.Matcap.PackFailed, MatcapPanelModel.PackLine(St(MatcapPackState.Failed), 10));
    }

    [Fact]
    [Trait("Plan", "AC-matcap-pack-6")]
    public void PackLine_failed_reads_the_failure()
    {
        Assert.Equal(Strings.Matcap.PackFailed, MatcapPanelModel.PackLine(St(MatcapPackState.Failed), 0));
        var src = Source("Proteus/Gui/MatcapPanel.cs");
        var draw = src[src.IndexOf("private void DrawProteus(", StringComparison.Ordinal)..];
        draw = draw[..draw.IndexOf("private void DrawLibraries(", StringComparison.Ordinal)];
        Assert.Contains("st.State == MatcapPackState.Failed && ImGui.Button(s.PackRetry)) ctx.RetryPack", draw);
        Assert.Contains("st.State == MatcapPackState.Failed && st.Detail.Length > 0 && ImGui.IsItemHovered()) ImGui.SetTooltip(st.Detail)", draw);
    }

    [Fact]
    [Trait("Plan", "AC-matcap-pack-2")]
    public async Task View_grows_as_files_land()
    {
        var gate = new SemaphoreSlim(0);
        var landed = new SemaphoreSlim(0);
        using var s = new DefaultMatcapsDownloadService(Substitute.For<IPluginLog>(), root, async (u, n, bytes, sha, dest, _, ct) =>
        {
            await gate.WaitAsync(ct);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.WriteAllBytes(dest, new byte[bytes]);
            landed.Release();
            return FetchResult.Success;
        });
        var view = new MatcapPackView();
        s.EnsureAsync();
        Assert.Empty(view.Images(s));
        for (int i = 1; i <= 3; i++)
        {
            gate.Release();
            Assert.True(await landed.WaitAsync(5000));
            // The version bump follows the file; wait for it rather than racing it.
            var until = DateTime.UtcNow.AddSeconds(5);
            while (view.Images(s).Count < i && DateTime.UtcNow < until) await Task.Delay(5);
            Assert.Equal(i, view.Images(s).Count);
        }
        gate.Release(1000);
    }

    [Fact]
    [Trait("Plan", "AC-matcap-pack-6")]
    public void Try_again_after_Failed_relists_files_found_on_disk()
    {
        using var s = new DefaultMatcapsDownloadService(Substitute.For<IPluginLog>(), root,
            (u, n, b, sha, dest, _, ct) => Task.FromResult(new FetchResult(false, FetchFailure.Transport, 0, 0, "offline")));
        s.EnsureAsync();
        SpinWait.SpinUntil(() => s.Status.State == MatcapPackState.Failed, 5000);
        var view = new MatcapPackView();
        Assert.Empty(view.Images(s));
        long v = s.Version;

        var dir = Path.Combine(root, "DefaultMatcaps");
        foreach (var (name, bytes, _) in DefaultMatcapsDownloadService.Matcaps) File.WriteAllBytes(Path.Combine(dir, name), new byte[bytes]);
        s.EnsureAsync(retryFailed: true);
        SpinWait.SpinUntil(() => s.Status.State == MatcapPackState.Ready, 5000);
        Assert.Equal(MatcapPackState.Ready, s.Status.State);
        Assert.True(s.Version > v);
        Assert.Equal(DefaultMatcapsDownloadService.Matcaps.Length, view.Images(s).Count);
    }

    [Fact]
    [Trait("Plan", "EDGE-matcap-pack-15")]
    public void View_rereads_only_when_Version_changes_and_reads_Version_first()
    {
        var pack = new FakePack();
        var view = new MatcapPackView();
        view.Images(pack);
        Assert.Equal(1, pack.AvailableCalls);                 // first call reads, even at version 0
        view.Images(pack);
        view.Images(pack);
        Assert.Equal(1, pack.AvailableCalls);
        pack.V = 1;
        view.Images(pack);
        Assert.Equal(2, pack.AvailableCalls);
        Assert.Equal(["version", "available", "version", "version", "version", "available"], pack.Log);

        // A file landing between the reads (the version moves inside Available): listed on the next call.
        pack.List = [new MatcapImage("a.png", "a", false, true)];
        pack.DuringAvailable = () => { pack.List.Add(new MatcapImage("b.png", "b", false, true)); pack.V++; };
        pack.V = 2;
        Assert.Single(view.Images(pack));
        pack.DuringAvailable = null;
        Assert.Equal(2, view.Images(pack).Count);
    }

    [Fact]
    [Trait("Plan", "AC-matcap-pack-2")]
    public void Choice_from_the_Proteus_source_is_the_pack_image()
    {
        var user = new MatcapImage("Gold Metal.png", @"C:\user\Gold Metal.png", false);
        var pack = new MatcapImage("Gold Metal.png", @"C:\cfg\DefaultMatcaps\Gold Metal.png", false, true);
        var ctx = Ctx([user], [pack]);
        var panel = new MatcapPanel();
        panel.Pick(MatcapPanel.SourceKind.Proteus, "Gold Metal.png");
        var c = panel.Choice(ctx)!;
        Assert.Equal(pack.Path, c.Image!.Path);
        Assert.True(c.Image.FromPack);
        panel.Pick(MatcapPanel.SourceKind.Images, "Gold Metal.png");
        Assert.Equal(user.Path, panel.Choice(ctx)!.Image!.Path);
    }

    [Fact]
    [Trait("Plan", "EDGE-matcap-pack-1")]
    public void Same_name_in_both_sources_hides_neither()
    {
        var user = new MatcapImage("Gold Metal.png", @"C:\user\Gold Metal.png", false);
        var pack = new MatcapImage("Gold Metal.png", @"C:\cfg\DefaultMatcaps\Gold Metal.png", false, true);
        var ctx = Ctx([user], [pack]);
        var panel = new MatcapPanel();
        panel.Pick(MatcapPanel.SourceKind.Proteus, "Gold Metal.png");
        var a = panel.Choice(ctx)!.Image!.Path;
        panel.Pick(MatcapPanel.SourceKind.Images, "Gold Metal.png");
        Assert.NotEqual(a, panel.Choice(ctx)!.Image!.Path);
    }

    [Fact]
    [Trait("Plan", "AC-matcap-pack-5")]
    [Trait("Plan", "EDGE-matcap-pack-1")]
    public void Find_prefers_mod_then_global_then_pack_unless_packOnly()
    {
        var global = Path.Combine(root, "global");
        var mod = Path.Combine(root, "mod");
        Directory.CreateDirectory(global);
        Directory.CreateDirectory(mod);
        File.WriteAllText(Path.Combine(global, "Gold Metal.png"), "g");
        File.WriteAllText(Path.Combine(mod, "Gold Metal.png"), "m");
        var pack = new List<MatcapImage> { new("Gold Metal.png", Path.Combine(root, "pack", "Gold Metal.png"), false, true) };

        Assert.Equal(Path.Combine(mod, "Gold Metal.png"), MatcapImages.Find("Gold Metal.png", global, mod, pack)!.Path);
        File.Delete(Path.Combine(mod, "Gold Metal.png"));
        Assert.Equal(Path.Combine(global, "Gold Metal.png"), MatcapImages.Find("Gold Metal.png", global, mod, pack)!.Path);
        Assert.True(MatcapImages.Find("Gold Metal.png", global, mod, pack, packOnly: true)!.FromPack);
        File.Delete(Path.Combine(global, "Gold Metal.png"));
        Assert.True(MatcapImages.Find("Gold Metal.png", global, mod, pack)!.FromPack);
        Assert.Null(MatcapImages.Find("Nope.png", global, mod, pack));
    }

    [Fact]
    [Trait("Plan", "AC-matcap-pack-4")]
    public void Pack_image_bakes_like_a_user_image()
    {
        var loader = new TextureLoader(Substitute.For<Dalamud.Plugin.Services.IDataManager>(), Substitute.For<IPluginLog>());
        var src = Path.Combine(DefaultMatcapsDownloadServiceTests.CommittedFolder(), "Gold Metal.png");
        var baked = MatcapImages.Bake(loader, src, Path.Combine(root, "o.tex"), compress: false);
        Assert.Null(baked.Refusal);
        Assert.Equal(512, baked.Side);
        Assert.True(File.Exists(baked.TempTex));
    }

    [Fact]
    [Trait("Plan", "AC-matcap-pack-8")]
    public void DefaultMatcaps_is_not_copied_to_the_output()
    {
        // The configuration the tests were built in (CI builds Debug, local runs Release): the segment after "bin".
        var parts = AppContext.BaseDirectory.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        int bin = Array.FindLastIndex(parts, p => string.Equals(p, "bin", StringComparison.OrdinalIgnoreCase));
        Assert.True(bin >= 0 && bin + 1 < parts.Length, AppContext.BaseDirectory);
        var config = parts[bin + 1];
        var output = Path.Combine(Repo, "Proteus", "bin", config, "win-x64");
        Assert.True(File.Exists(Path.Combine(output, "Proteus.dll")), $"{output} is not a build output");
        Assert.False(Directory.Exists(Path.Combine(output, "DefaultMatcaps")));
        Assert.False(Directory.Exists(Path.Combine(AppContext.BaseDirectory, "DefaultMatcaps")));
        var csproj = Source("Proteus/Proteus.csproj");
        Assert.DoesNotMatch(new Regex(@"Include=""[^""]*DefaultMatcaps", RegexOptions.IgnoreCase), csproj);
    }

    [Fact]
    [Trait("Plan", "EDGE-matcap-pack-13")]
    public void A_pack_file_gone_since_it_was_listed_is_refused_as_unreadable()
    {
        var loader = new TextureLoader(Substitute.For<Dalamud.Plugin.Services.IDataManager>(), Substitute.For<IPluginLog>());
        var baked = MatcapImages.Bake(loader, Path.Combine(root, "DefaultMatcaps", "Gold Metal.png"), Path.Combine(root, "o.tex"), false);
        Assert.Equal("unreadable", baked.Refusal);
    }

    [Fact]
    [Trait("Plan", "AC-matcap-pack-9")]
    [Trait("Plan", "EDGE-matcap-pack-9")]
    public void Winning_call_is_Downloading_before_it_returns()
    {
        var dir = Path.Combine(root, "DefaultMatcaps");
        Directory.CreateDirectory(dir);
        foreach (var (name, bytes, _) in DefaultMatcapsDownloadService.Matcaps) File.WriteAllBytes(Path.Combine(dir, name), new byte[bytes]);
        var gate = new SemaphoreSlim(0);
        int fetches = 0;
        using var s = new DefaultMatcapsDownloadService(Substitute.For<IPluginLog>(), root, async (u, n, bytes, sha, dest, _, ct) =>
        {
            Interlocked.Increment(ref fetches);
            await gate.WaitAsync(ct);
            File.WriteAllBytes(dest, new byte[bytes]);
            return FetchResult.Success;
        });
        s.EnsureAsync();
        Assert.True(SpinWait.SpinUntil(() => s.Status.State == MatcapPackState.Ready, 5000));
        File.Delete(Path.Combine(dir, DefaultMatcapsDownloadService.Matcaps[0].Name));

        s.EnsureAsync(retryFailed: true, evenIfReady: true);
        var st = s.Status;
        Assert.Equal(MatcapPackState.Downloading, st.State);           // never the stale Ready
        Assert.Equal(DefaultMatcapsDownloadService.Matcaps.Length - 1, st.Have);   // nor its stale count
        s.EnsureAsync(retryFailed: true, evenIfReady: true);            // a second call while running: no second run
        gate.Release(10);
        Assert.True(SpinWait.SpinUntil(() => s.Status.State == MatcapPackState.Ready, 5000));
        Assert.Equal(1, fetches);
    }

    [Fact]
    [Trait("Plan", "EDGE-matcap-pack-9")]
    public void Final_state_is_never_seen_with_a_run_active()
    {
        int n = 0;
        using var s = new DefaultMatcapsDownloadService(Substitute.For<IPluginLog>(), root, (u, name, bytes, sha, dest, _, ct) =>
        {
            // Alternate success and a stopping failure, so runs end both Ready and Failed.
            if (Interlocked.Increment(ref n) % 3 == 0) return Task.FromResult(new FetchResult(false, FetchFailure.Transport, 0, 0, "x"));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.WriteAllBytes(dest, new byte[bytes]);
            return Task.FromResult(FetchResult.Success);
        });
        bool stop = false, bad = false;
        var observer = new Thread(() =>
        {
            // Read together: a run is active exactly while the state is Downloading — never a final state with a run on.
            while (!Volatile.Read(ref stop))
            {
                var (state, running) = s.Snapshot();
                if (running != (state == MatcapPackState.Downloading)) bad = true;
            }
        });
        observer.Start();
        var dir = Path.Combine(root, "DefaultMatcaps");
        int readies = 0;
        for (int i = 0; i < 20; i++)
        {
            // Every fifth run finds every file in place and ends Ready with no fetch; the rest end Failed.
            if (i % 5 == 4)
            {
                Directory.CreateDirectory(dir);
                foreach (var (name, bytes, _) in DefaultMatcapsDownloadService.Matcaps) File.WriteAllBytes(Path.Combine(dir, name), new byte[bytes]);
            }
            s.EnsureAsync(retryFailed: true, evenIfReady: true);
            Assert.True(SpinWait.SpinUntil(() => s.Status.State != MatcapPackState.Downloading, 5000));
            // Right after a final state, the next re-arm wins at once.
            Assert.False(s.IsRunActive);
            if (s.Status.State == MatcapPackState.Ready) readies++;
            if (i % 2 == 0) foreach (var f in Directory.GetFiles(dir).Take(2)) File.Delete(f);
        }
        Volatile.Write(ref stop, true);
        observer.Join();
        Assert.False(bad);
        Assert.True(readies >= 3, $"only {readies} runs ended Ready");
    }

    [Fact]
    [Trait("Plan", "EDGE-matcap-pack-9")]
    public void A_cancelled_run_publishes_idle_with_no_run_active()
    {
        var entered = new TaskCompletionSource();
        var s = new DefaultMatcapsDownloadService(Substitute.For<IPluginLog>(), root, async (u, n, b, sha, dest, _, ct) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return FetchResult.Success;
        });
        s.EnsureAsync();
        Assert.True(entered.Task.Wait(5000));
        Assert.Equal((MatcapPackState.Downloading, true), s.Snapshot());
        s.Dispose();
        Assert.True(SpinWait.SpinUntil(() => !s.IsRunActive, 5000));
        Assert.Equal((MatcapPackState.Idle, false), s.Snapshot());
    }

    [Fact]
    [Trait("Plan", "EDGE-matcap-pack-10")]
    public void Pack_images_do_not_need_Penumbra()
    {
        var pack = new FakePack { List = [new MatcapImage("a.png", "a", false, true)] };
        Assert.Single(new MatcapPackView().Images(pack));
        Assert.Equal(Strings.Matcap.NoPenumbra, MatcapPanelModel.ApplyDisabledReason(penumbra: false, 1, true, 1));
    }

    [Fact]
    [Trait("Plan", "EDGE-matcap-pack-14")]
    public void Change_kind_rule_holds_for_pack_images()
    {
        var packChoice = MatcapChoice.OfImage(new MatcapImage("Gold Metal.png", "p", false, true));
        var imageEntry = new MatcapRecord.MaterialEntry { Source = new MatcapRecord.SourceRef { Kind = MatcapRecord.SourceRef.KindImage } };
        var libraryEntry = new MatcapRecord.MaterialEntry { Source = new MatcapRecord.SourceRef { Kind = MatcapRecord.SourceRef.KindLibrary } };
        Assert.True(MatcapPanelModel.CanChange(imageEntry, packChoice));
        Assert.False(MatcapPanelModel.CanChange(libraryEntry, packChoice));
    }
}
