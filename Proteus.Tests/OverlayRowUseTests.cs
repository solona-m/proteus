using System.Collections.Generic;
using Proteus.Gui;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>Which rows the Colors window treats as used — the rows the picker shows (and dims nothing in). Hiding is
/// only safe when this never under-reports, so every overlay counts and an unreadable or empty read means unknown.</summary>
public class OverlayRowUseTests
{
    private static IndexRead Read(string? column, params int[] rows)
        => new(true, new ContentIndexTexture.Scan(new HashSet<int>(rows), column));

    private static readonly IndexRead Unreadable = new(true, null);
    private static readonly IndexRead NoIndex = IndexRead.NoIndex;

    [Fact]
    [Trait("Plan", "AC-row-hover-highlight-9")]
    [Trait("Plan", "EDGE-row-hover-highlight-32")]
    public void Every_overlays_index_counts()
    {
        var (rows, column) = OverlayRowUse.Of([NoIndex, Read("A", 3, 10)], []);
        Assert.Equal(new HashSet<int> { 3, 10, 16 }, rows);
        Assert.Equal("A", column);

        (rows, column) = OverlayRowUse.Of([Read("A", 3), Read("A", 7)], []);
        Assert.Equal(new HashSet<int> { 3, 7 }, rows);
        Assert.Equal("A", column);
    }

    [Fact]
    [Trait("Plan", "AC-row-hover-highlight-9")]
    [Trait("Plan", "EDGE-row-hover-highlight-33")]
    public void An_index_less_overlay_adds_row_16_in_column_A()
    {
        var (rows, column) = OverlayRowUse.Of([Read("B", 3), NoIndex], []);
        Assert.Equal(new HashSet<int> { 3, GlowShell.Row }, rows);
        Assert.Null(column);   // B from the scan, A from the index-less overlay: both columns live
    }

    [Fact]
    [Trait("Plan", "EDGE-row-hover-highlight-34")]
    public void Masks_count_only_with_an_index()
    {
        var (rows, _) = OverlayRowUse.Of([Read("A", 3)], [NoIndex, Read("A", 5)]);
        Assert.Equal(new HashSet<int> { 3, 5 }, rows);

        var unknown = OverlayRowUse.Of([Read("A", 3)], [Read("A")]);
        Assert.Null(unknown.Rows);
        Assert.Null(unknown.Column);
    }

    [Fact]
    [Trait("Plan", "EDGE-row-hover-highlight-23")]
    public void No_index_anywhere_is_row_16_A()
    {
        foreach (var (rows, column) in new[]
                 {
                     OverlayRowUse.Of([NoIndex, NoIndex], []),
                     OverlayRowUse.Of([], []),
                     OverlayRowUse.Of([], [NoIndex]),
                 })
        {
            Assert.Equal(new HashSet<int> { 16 }, rows);
            Assert.Equal("A", column);
        }
    }

    [Fact]
    [Trait("Plan", "AC-row-hover-highlight-9")]
    [Trait("Plan", "EDGE-row-hover-highlight-37")]
    public void Any_unreadable_index_means_unknown()
    {
        Assert.Equal((null, null), OverlayRowUse.Of([Read("A", 3), Unreadable], []));
        Assert.Equal((null, null), OverlayRowUse.Of([Read("A", 3)], [Unreadable]));
        Assert.Equal((null, null), OverlayRowUse.Of([NoIndex, Unreadable], []));
    }

    [Fact]
    [Trait("Plan", "AC-row-hover-highlight-9")]
    [Trait("Plan", "EDGE-row-hover-highlight-21")]
    public void Any_empty_scan_means_unknown_whatever_sits_beside_it()
    {
        Assert.Equal((null, null), OverlayRowUse.Of([Read(null)], []));
        Assert.Equal((null, null), OverlayRowUse.Of([Read(null), NoIndex], []));
        Assert.Equal((null, null), OverlayRowUse.Of([Read("A", 3), Read(null)], []));
    }

    [Fact]
    [Trait("Plan", "AC-row-hover-highlight-9")]
    public void AgreedColumn_keeps_its_rules()
    {
        Assert.Null(OverlayRowUse.AgreedColumn([]));
        Assert.Equal("A", OverlayRowUse.AgreedColumn(["A", "A"]));
        Assert.Null(OverlayRowUse.AgreedColumn(["A", "B"]));
        Assert.Null(OverlayRowUse.AgreedColumn(["A", null]));
        Assert.Equal("B", OverlayRowUse.AgreedColumn(["B"]));
    }

    [Fact]
    [Trait("Plan", "AC-row-hover-highlight-9")]
    [Trait("Plan", "EDGE-row-hover-highlight-36")]
    public void Stamped_cache_rescans_only_when_the_stamp_changes()
    {
        var cache = new StampedScanCache();
        int scans = 0;
        ContentIndexTexture.Scan? Scan() { scans++; return new ContentIndexTexture.Scan([scans], "A"); }

        var s1 = new FileStamp(1, 10);
        Assert.Equal(new HashSet<int> { 1 }, cache.Get("p", s1, Scan)!.Value.Rows);
        Assert.Equal(new HashSet<int> { 1 }, cache.Get("p", s1, Scan)!.Value.Rows);
        Assert.Equal(1, scans);

        Assert.Equal(new HashSet<int> { 2 }, cache.Get("p", new FileStamp(2, 10), Scan)!.Value.Rows);   // edited on disk
        Assert.Equal(2, scans);

        cache.Get("q", s1, () => null);   // unreadable is cached too, until its stamp changes
        Assert.Null(cache.Get("q", s1, Scan));
        Assert.Equal(2, scans);

        cache.RemoveWhere(k => k == "p");
        Assert.Equal(1, cache.Count);
        cache.Clear();
        Assert.Equal(0, cache.Count);
    }
}
