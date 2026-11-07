using System.Collections.Generic;

namespace Proteus.Services.Matcap;

/// <summary>Where Proteus's own matcap set is in its first-use download.</summary>
public enum MatcapPackState { Idle, Downloading, Ready, Failed }

/// <summary>The pack's state, how many of its files are on disk, and why it failed (empty otherwise).</summary>
public sealed record MatcapPackStatus(MatcapPackState State, int Have, int Total, string Detail);

/// <summary>
/// Proteus's own matcaps: downloaded the first time the Matcaps tool is shown, listed from the plugin's config directory.
/// </summary>
public interface IMatcapPack
{
    MatcapPackStatus Status { get; }

    /// <summary>Rises whenever <see cref="Available"/> may have changed: a run starting or ending, and each file landing.</summary>
    long Version { get; }

    /// <summary>The pinned matcaps present at their pinned size, by name.</summary>
    IReadOnlyList<MatcapImage> Available();

    /// <summary>
    /// Starts fetching what is missing, once per session; with <paramref name="retryFailed"/>, again after a failed run;
    /// with <paramref name="evenIfReady"/> (the test harness only), again after a finished one too. Cheap to call every frame.
    /// </summary>
    void EnsureAsync(bool retryFailed = false, bool evenIfReady = false);
}

/// <summary>
/// What the Matcaps pane lists from the pack, re-read only when the pack's <see cref="IMatcapPack.Version"/> moved —
/// never a directory scan every frame.
/// </summary>
public sealed class MatcapPackView
{
    private long seen = -1;   // the first call always reads
    private IReadOnlyList<MatcapImage> images = [];

    /// <summary>
    /// The list to show. The version is read before the list: a file landing in between bumps the version again, so
    /// the next call picks it up and nothing is missed.
    /// </summary>
    public IReadOnlyList<MatcapImage> Images(IMatcapPack? pack)
    {
        if (pack == null) return [];
        long v = pack.Version;
        if (v == seen) return images;
        images = pack.Available();
        seen = v;
        return images;
    }
}
