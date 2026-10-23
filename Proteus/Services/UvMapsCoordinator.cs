using System;

namespace Proteus.Services;

/// <summary>
/// Joins the map downloads to the remap and the composite: a composite asking for the men's maps starts their download;
/// any set arriving clears the routes that failed for want of it and recomposites, so what was drawn as before is
/// redrawn with the maps.
/// <para/>
/// The events arrive on worker threads; the handlers only start a download or ask for a recomposite.
/// </summary>
internal sealed class UvMapsCoordinator : IDisposable
{
    private readonly UVRemapService remap;
    private readonly UVMapDownloadService downloads;
    private readonly Func<bool> canRecomposite;
    private readonly Action recomposite;
    private volatile bool disposed;

    /// <param name="canRecomposite">Whether a recomposite may run now (plugin enabled, Penumbra up).</param>
    /// <param name="recomposite">Asks for one, as "uvmaps-downloaded".</param>
    public UvMapsCoordinator(UVRemapService remap, UVMapDownloadService downloads, Func<bool> canRecomposite, Action recomposite)
    {
        this.remap = remap;
        this.downloads = downloads;
        this.canRecomposite = canRecomposite;
        this.recomposite = recomposite;
        remap.MenMapsWanted += OnWanted;
        downloads.MapsArrived += OnArrived;
    }

    private void OnWanted()
    {
        if (!disposed) downloads.EnsureMenMapsAsync();
    }

    private void OnArrived()
    {
        if (disposed) return;
        remap.ForgetMissingHops();
        if (canRecomposite()) recomposite();
    }

    public void Dispose()
    {
        disposed = true;
        remap.MenMapsWanted -= OnWanted;
        downloads.MapsArrived -= OnArrived;
    }
}
