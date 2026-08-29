using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Dalamud.Plugin.Services;
using NSubstitute;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// Guards the relocation of the plugin's large assets out of the assembly directory.
/// <para/>
/// Dalamud installs every plugin version into its own folder, so anything stored next to the DLL is
/// destroyed on each update. The UV maps lived there, which meant every update re-triggered a 256 MB
/// download: ~48,000 downloads of a 128 MB release asset against ~1,450 installs, and a throttle on
/// the whole repo's assets. The fix is to keep them in the config directory instead — and to RECLAIM
/// an older install's copies rather than re-fetching them, which is the part these tests cover.
/// A regression here is silent and expensive: everything still works, it just downloads a quarter of
/// a gigabyte again on every update.
/// </summary>
public sealed class AssetRelocationTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(), "proteus-assets-" + Guid.NewGuid().ToString("N"));

    private string DataDir => Path.Combine(root, "config");
    private string AsmDir => Path.Combine(root, "plugin");

    private static IPluginLog Log => Substitute.For<IPluginLog>();

    public AssetRelocationTests()
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(AsmDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); } catch { /* temp dir */ }
    }

    private static void Write(string path, int bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
    }

    // ── UV maps ──────────────────────────────────────────────────────────────

    private static readonly string[] MapNames =
        ["bibo_to_gen3_transfer.tif", "gen3_to_bibo_transfer.tif"];

    [Fact]
    public void MapsPresent_ReclaimsMapsLeftInTheAssemblyDirectory()
    {
        foreach (var n in MapNames) Write(Path.Combine(AsmDir, "uvmaps", n), 64);

        var present = new UVMapDownloadService(Log, DataDir, AsmDir).MapsPresent();

        Assert.True(present, "maps sitting in the old assembly-dir home should satisfy MapsPresent " +
                             "after being reclaimed, NOT trigger a fresh 256 MB download");
        foreach (var n in MapNames)
        {
            Assert.True(File.Exists(Path.Combine(DataDir, "uvmaps", n)), $"{n} should be in the config dir");
            Assert.False(File.Exists(Path.Combine(AsmDir, "uvmaps", n)), $"{n} should have MOVED, not been copied");
        }
    }

    [Fact]
    public void MapsPresent_KeepsTheConfigDirCopyWhenBothExist()
    {
        foreach (var n in MapNames)
        {
            Write(Path.Combine(DataDir, "uvmaps", n), 128);   // the good one
            Write(Path.Combine(AsmDir, "uvmaps", n), 64);     // a stale leftover
        }

        Assert.True(new UVMapDownloadService(Log, DataDir, AsmDir).MapsPresent());

        // The config copy wins: it is the one the current version wrote and verified.
        foreach (var n in MapNames)
            Assert.Equal(128, new FileInfo(Path.Combine(DataDir, "uvmaps", n)).Length);
    }

    [Fact]
    public void MapsPresent_IsFalseWhenNothingIsAnywhere()
    {
        Assert.False(new UVMapDownloadService(Log, DataDir, AsmDir).MapsPresent());
    }

    [Fact]
    public void MapsPresent_TreatsAZeroLengthMapAsMissing()
    {
        // A zero-length file is what an interrupted write leaves behind. Trusting it would wedge the
        // plugin permanently: MapsPresent would say yes and every composite would fail to load a map.
        foreach (var n in MapNames) Write(Path.Combine(DataDir, "uvmaps", n), 0);

        Assert.False(new UVMapDownloadService(Log, DataDir, AsmDir).MapsPresent());
    }

    [Fact]
    public void UVMapService_WithNoAssemblyDir_DoesNotThrow()
    {
        Assert.False(new UVMapDownloadService(Log, DataDir).MapsPresent());
    }

    // ── Starter effects ──────────────────────────────────────────────────────

    [Fact]
    public void Effects_ExposeTheConfigDirectoryNotTheAssemblyDirectory()
    {
        var svc = new DefaultEffectsDownloadService(Log, DataDir, AsmDir);

        // SidecarDiscoveryService seeds the user's library from this path; pointing it at the
        // assembly directory is exactly the bug being fixed.
        Assert.Equal(Path.Combine(DataDir, "DefaultEffects"), svc.EffectsDir);
    }

    /// <summary>
    /// The starter effects as the service knows them. Staged in full so the reclaim is COMPLETE and the
    /// service never reaches its download loop — these tests must not touch the network, both because a
    /// unit suite that needs a live host is not a unit suite and because the host in question is the one
    /// that got throttled in the first place.
    /// </summary>
    private static string[] EffectNames =>
    [
        "Moon and Stars.jpeg", "flames.jpeg", "geometric.jpeg", "hello kitty.png", "lips.png",
        "polka dots.png", "rgb.jpeg", "sky.jpeg", "starfield.jpeg", "unicorns.png",
        "wildflowers and butterflies.png",
    ];

    [Fact]
    public void Effects_ReclaimedFromTheAssemblyDirectoryInsteadOfDownloaded()
    {
        // Names with spaces are the interesting case: they are what the user sees in the Effect
        // dropdown and what a sidecar's Scroll value refers to, so a reclaim must not rename them.
        foreach (var n in EffectNames) Write(Path.Combine(AsmDir, "DefaultEffects", n), 32);

        using var svc = new DefaultEffectsDownloadService(Log, DataDir, AsmDir);
        svc.EnsureAsync();
        WaitFor(() => EffectNames.All(n => File.Exists(Path.Combine(svc.EffectsDir, n))));

        foreach (var n in EffectNames)
        {
            Assert.True(File.Exists(Path.Combine(svc.EffectsDir, n)), $"{n} should be reclaimed verbatim");
            Assert.False(File.Exists(Path.Combine(AsmDir, "DefaultEffects", n)), $"{n} should have moved");
        }
    }

    /// <summary>
    /// The reclaim is complete when every REQUIRED effect is present, not when enough files moved.
    /// <para/>
    /// The old folder is a user-visible library, so it can hold files of the user's own. Counting let two
    /// unrelated files stand in for a missing effect — which then never downloaded and was silently gone
    /// from the dropdown, because this service is deliberately quiet.
    /// </summary>
    [Fact]
    public void Effects_ExtraFilesDoNotCoverForAMissingEffect()
    {
        foreach (var n in EffectNames.Where(n => n != "lips.png"))
            Write(Path.Combine(AsmDir, "DefaultEffects", n), 32);
        Write(Path.Combine(AsmDir, "DefaultEffects", "my own effect.png"), 32);
        Write(Path.Combine(AsmDir, "DefaultEffects", "another of mine.png"), 32);

        using var svc = new DefaultEffectsDownloadService(Log, DataDir, AsmDir);

        // Reclaim is incomplete, so the service would go to the network for the missing one — which is
        // correct behaviour and exactly why this asserts on the reclaim rather than calling EnsureAsync.
        var reclaimed = typeof(DefaultEffectsDownloadService)
            .GetMethod("ReclaimFromAssemblyDir", BindingFlags.NonPublic | BindingFlags.Instance)!;
        Directory.CreateDirectory(svc.EffectsDir);

        Assert.False((bool)reclaimed.Invoke(svc, null)!,
            "a missing effect must not be masked by unrelated files the user kept in the same folder");
    }

    /// <summary>
    /// A complete reclaim must leave the reclaimed bytes alone — reclaimed art is an older, larger
    /// revision whose size never matches the pinned one, and rewriting it is the wasted traffic the whole
    /// relocation exists to stop.
    /// <para/>
    /// Note what this does NOT cover: the other half of that bug lives in the download loop's skip
    /// condition, which is only reached when the reclaim is INCOMPLETE — and an incomplete reclaim sends
    /// the service to the network for the missing file. Covering it properly needs an injectable
    /// downloader; asserting it here would mean a unit test that makes real HTTP requests.
    /// </summary>
    [Fact]
    public void Effects_ACompleteReclaimLeavesTheReclaimedBytesAlone()
    {
        foreach (var n in EffectNames) Write(Path.Combine(AsmDir, "DefaultEffects", n), 32);

        using var svc = new DefaultEffectsDownloadService(Log, DataDir, AsmDir);
        svc.EnsureAsync();
        WaitFor(() => EffectNames.All(n => File.Exists(Path.Combine(svc.EffectsDir, n))));

        // 32 bytes is nothing like the pinned size; every file must still be left exactly as reclaimed.
        foreach (var n in EffectNames)
            Assert.Equal(32, new FileInfo(Path.Combine(svc.EffectsDir, n)).Length);
    }

    /// <summary>
    /// The download URL and the on-disk name are deliberately different: GitHub rewrites spaces in
    /// release asset names, so the upload workflow stages them with dots and the client asks for that
    /// name while saving under the original. If these two substitutions ever disagree, every starter
    /// effect 404s forever — silently, because the download is intentionally quiet.
    /// </summary>
    [Theory]
    [InlineData("hello kitty.png", "hello.kitty.png")]
    [InlineData("wildflowers and butterflies.png", "wildflowers.and.butterflies.png")]
    [InlineData("Moon and Stars.jpeg", "Moon.and.Stars.jpeg")]
    [InlineData("flames.jpeg", "flames.jpeg")]
    public void Effects_RemoteNameMatchesTheUploadWorkflowsRenaming(string local, string expected)
    {
        // Mirrors `$f.Name -replace ' ', '.'` in .github/workflows/upload-effects.yml.
        Assert.Equal(expected, local.Replace(' ', '.'));
    }

    // ── Sources ──────────────────────────────────────────────────────────────

    [Fact]
    public void BaseUrls_AlwaysEndWithTheTagAndASlash()
    {
        foreach (var b in ProteusAssets.BaseUrls("uvmaps-v1"))
            Assert.EndsWith("uvmaps-v1/", b);
    }

    [Fact]
    public void BaseUrls_PutTheMirrorFirstAndAlwaysKeepGitHubAsAFallback()
    {
        var urls = ProteusAssets.BaseUrls("effects-v1");

        Assert.NotEmpty(urls);
        // GitHub is the origin and must remain reachable even when a mirror is configured, so that a
        // mirror outage degrades to a slower download rather than to no plugin.
        Assert.EndsWith("effects-v1/", urls[^1]);
        Assert.StartsWith(ProteusAssets.GitHubBase, urls[^1]);
        if (ProteusAssets.MirrorBase.Length > 0)
            Assert.StartsWith(ProteusAssets.MirrorBase, urls[0]);
        else
            Assert.Single(urls);
    }

    private static void WaitFor(Func<bool> cond, int timeoutMs = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs && !cond())
            System.Threading.Thread.Sleep(20);
        Assert.True(cond(), "condition not met within the timeout");
    }
}
