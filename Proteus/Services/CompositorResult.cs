using System;

namespace Proteus.Services;

public class CompositorResult
{
    public bool Success { get; init; }
    public int TexturesPatched { get; init; }
    public int OverlayModsUsed { get; init; }
    public string? ErrorMessage { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    /// <summary>The run's recomposite epoch, so a waiter can tell its own run from one already in flight.</summary>
    public long Epoch { get; init; }
}
