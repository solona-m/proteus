using System.Numerics;
using Proteus.Gui;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// The window widening for the part drawer and giving the width back when it closes — only while nobody else has
/// sized it since, and never into the width the window remembers between sessions. Sizes are unscaled.
/// </summary>
public class DrawerResizeTests
{
    private const float Screen = 2560f;

    [Fact]
    [Trait("Plan", "AC-part-drawer-4")]
    public void OpeningWidensByTheGrow()
    {
        var r = new DrawerResize();
        Assert.Equal(new Vector2(1288f, 700f), r.Open(new Vector2(900f, 700f), 388f, fitting: false, Screen));
    }

    [Fact]
    [Trait("Plan", "AC-part-drawer-4")]
    public void ClosingRestoresThePreOpenSize()
    {
        var r = new DrawerResize();
        r.Open(new Vector2(900f, 700f), 388f, false, Screen);
        r.Landed(new Vector2(1288f, 700f));

        Assert.Equal(new Vector2(900f, 700f), r.Close(new Vector2(1288f, 700f), fitting: false));
        // Once given back, a second close has nothing to restore.
        Assert.Null(r.Close(new Vector2(900f, 700f), false));
    }

    [Fact]
    [Trait("Plan", "AC-part-drawer-10")]
    public void SavedWidthExcludesTheDrawer()
    {
        var r = new DrawerResize();
        Assert.Equal(900f, r.SavedWidth(new Vector2(900f, 700f)));

        r.Open(new Vector2(900f, 700f), 388f, false, Screen);
        r.Landed(new Vector2(1288f, 700f));
        Assert.Equal(900f, r.SavedWidth(new Vector2(1288f, 700f)));   // quit now: the drawer's share is not kept

        r.Close(new Vector2(1288f, 700f), false);
        Assert.Equal(900f, r.SavedWidth(new Vector2(900f, 700f)));

        // A width the user dragged to while the drawer was open is theirs, drawer and all.
        r.Open(new Vector2(900f, 700f), 388f, false, Screen);
        r.Landed(new Vector2(1288f, 700f));
        Assert.Equal(1500f, r.SavedWidth(new Vector2(1500f, 700f)));
    }

    [Fact]
    [Trait("Plan", "EDGE-part-drawer-1")]
    public void NoResizeWhileFitting()
    {
        var r = new DrawerResize();
        Assert.Null(r.Open(new Vector2(900f, 700f), 388f, fitting: true, Screen));
        Assert.Null(r.Close(new Vector2(900f, 700f), fitting: false));

        // A fit between open and close: the window was sized afresh, so there is nothing to give back.
        r.Open(new Vector2(900f, 700f), 388f, false, Screen);
        r.Landed(new Vector2(1288f, 700f));
        Assert.Null(r.Close(new Vector2(1288f, 700f), fitting: true));
        Assert.Equal(1288f, r.SavedWidth(new Vector2(1288f, 700f)));
    }

    [Fact]
    [Trait("Plan", "EDGE-part-drawer-5")]
    public void ClosingAfterAUserResizeLeavesTheWindow()
    {
        var r = new DrawerResize();
        r.Open(new Vector2(900f, 700f), 388f, false, Screen);
        r.Landed(new Vector2(1288f, 700f));

        Assert.Null(r.Close(new Vector2(1400f, 760f), false));
    }

    [Fact]
    [Trait("Plan", "EDGE-part-drawer-7")]
    public void ClippedGrowRestoresExactly()
    {
        var r = new DrawerResize();
        Assert.Equal(new Vector2(Screen, 700f), r.Open(new Vector2(2400f, 700f), 388f, false, Screen));
        r.Landed(new Vector2(Screen, 700f));

        // Back to where it was, not 388 narrower than the clipped width.
        Assert.Equal(new Vector2(2400f, 700f), r.Close(new Vector2(Screen, 700f), false));
    }

    [Fact]
    [Trait("Plan", "EDGE-part-drawer-12")]
    public void FractionalScaleRestoresWithinTolerance()
    {
        var r = new DrawerResize();
        r.Open(new Vector2(900f, 700f), 288.4f, false, Screen);
        // At 1.25 the window lands a fraction of a pixel off what was asked; the live size is read back as is.
        r.Landed(new Vector2(1188.8f, 700f));

        Assert.Equal(new Vector2(900f, 700f), r.Close(new Vector2(1188.0f, 700.4f), false));
    }

    [Fact]
    [Trait("Plan", "EDGE-part-drawer-12")]
    public void CloseBeforeTheGrowLandedComparesWithWhatWasAsked()
    {
        var r = new DrawerResize();
        r.Open(new Vector2(900f, 700f), 388f, false, Screen);
        Assert.Equal(new Vector2(900f, 700f), r.Close(new Vector2(1288.4f, 700f), false));
    }

    [Fact]
    [Trait("Plan", "EDGE-part-drawer-13")]
    public void RequestAppliedOnce()
    {
        var q = new DrawerRequest();
        Assert.Null(q.Take());

        q.Opened(288f);
        Assert.Equal(new DrawerRequest.Pending(true, 288f), q.Take());
        Assert.Null(q.Take());   // the next frame sees nothing: the window grows once, not every frame

        q.Closed();
        Assert.Equal(new DrawerRequest.Pending(false, 0f), q.Take());
        Assert.Null(q.Take());
    }

    [Fact]
    [Trait("Plan", "AC-part-drawer-4")]
    public void NothingToGrowRestoresNothing()
    {
        var r = new DrawerResize();
        Assert.Null(r.Open(new Vector2(900f, 700f), 0f, false, Screen));
        Assert.Null(r.Close(new Vector2(900f, 700f), false));
    }
}
