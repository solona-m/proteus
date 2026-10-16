using System.Linq;
using Proteus.Gui;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// The Studio row's columns with the part list open as a drawer between the settings and the model, so the list
/// no longer drops down over the model it is picking parts of.
/// </summary>
public class PartsPanelLayoutTests
{
    private const float Spacing = 8f, Settings = 250f, MinDrawer = 240f, MinViewer = 200f;

    private static PartsPanel.StudioColumns Layout(float row, bool view, bool open, float wanted = 380f)
        => PartsPanel.LayoutColumns(row, Spacing, view, open, Settings, wanted, MinDrawer, MinViewer);

    [Fact]
    [Trait("Plan", "AC-part-drawer-1")]
    public void OpenDrawerSitsBetweenSettingsAndViewer()
    {
        var c = Layout(1200f, view: true, open: true);

        Assert.Equal(Settings, c.Settings);
        Assert.Equal(380f, c.Drawer);
        Assert.Equal(1200f - Settings - 380f - 2 * Spacing, c.Viewer);
        Assert.Equal(1200f, c.Settings + c.Drawer + c.Viewer + 2 * Spacing);
    }

    [Fact]
    [Trait("Plan", "AC-part-drawer-3")]
    public void ClosedDrawerLeavesTodaysLayout()
    {
        Assert.Equal(new PartsPanel.StudioColumns(Settings, 0f, 1200f - Settings - Spacing), Layout(1200f, true, false));
        Assert.Equal(new PartsPanel.StudioColumns(1200f, 0f, 0f), Layout(1200f, false, false));
    }

    [Fact]
    [Trait("Plan", "AC-part-drawer-5")]
    public void DrawerTakesTheRestWithoutTheModelView()
        => Assert.Equal(new PartsPanel.StudioColumns(Settings, 1200f - Settings - Spacing, 0f), Layout(1200f, false, true));

    [Fact]
    [Trait("Plan", "AC-part-drawer-6")]
    [Trait("Plan", "EDGE-part-drawer-11")]
    public void DrawerWidthFitsItsRowsWithinBounds()
    {
        Assert.Equal(240f, PartsPanel.DrawerWidth(100f, 24f, 240f, 440f));
        Assert.Equal(324f, PartsPanel.DrawerWidth(300f, 24f, 240f, 440f));
        Assert.Equal(440f, PartsPanel.DrawerWidth(900f, 24f, 240f, 440f));   // the rest scrolls sideways
    }

    /// <summary>However narrow the row, every column that is drawn gets a positive width: ImGui reads 0 as "fill the
    /// rest" and a negative width as "all but", either of which would let one column swallow the next.</summary>
    [Theory]
    [Trait("Plan", "AC-part-drawer-6")]
    [InlineData(1040f, 1f)]   // the 520 floor at scale 2
    [InlineData(520f, 1f)]
    [InlineData(300f, 1f)]
    [InlineData(100f, 1f)]
    [InlineData(1040f, 2f)]
    public void NoDrawnColumnIsEverNonPositive(float row, float scale)
    {
        foreach (bool view in new[] { true, false })
        foreach (bool open in new[] { true, false })
        {
            var c = PartsPanel.LayoutColumns(row, Spacing * scale, view, open, Settings * scale, 440f * scale,
                                             MinDrawer * scale, MinViewer * scale);
            Assert.True(c.Settings > 0f, $"settings {c.Settings} at row {row}, view {view}, open {open}");
            if (open) Assert.True(c.Drawer > 0f, $"drawer {c.Drawer} at row {row}, view {view}");
            else Assert.Equal(0f, c.Drawer);
            if (view) Assert.True(c.Viewer > 0f, $"viewer {c.Viewer} at row {row}, open {open}");
            else Assert.Equal(0f, c.Viewer);
        }
    }

    [Fact]
    [Trait("Plan", "EDGE-part-drawer-2")]
    public void DrawerGivesWayToKeepTheViewerVisible()
    {
        // Room for the drawer's floor and the viewer's minimum, but not the drawer it wants.
        var c = Layout(760f, true, true);
        Assert.Equal(760f - Settings - 2 * Spacing - MinViewer, c.Drawer);
        Assert.Equal(MinViewer, c.Viewer);

        // Not even that: the drawer keeps its floor, the viewer takes what is left, still above zero.
        var tight = Layout(560f, true, true);
        Assert.Equal(MinDrawer, tight.Drawer);
        Assert.Equal(560f - Settings - 2 * Spacing - MinDrawer, tight.Viewer);
        Assert.True(tight.Viewer is > 0f and < MinViewer);
    }

    [Fact]
    [Trait("Plan", "AC-part-drawer-7")]
    public void DrawerStateIsNotPersisted()
        => Assert.DoesNotContain(typeof(Configuration).GetProperties().Concat<System.Reflection.MemberInfo>(typeof(Configuration).GetFields()),
                                 m => m.Name.Contains("Drawer", System.StringComparison.OrdinalIgnoreCase));

    [Fact]
    [Trait("Plan", "AC-part-drawer-8")]
    [Trait("Plan", "EDGE-part-drawer-3")]
    public void ControlsHeightIgnoresTheDrawer()
    {
        // The height the window fits itself to is the tools' and the settings' only: there is no drawer term.
        Assert.Equal(410f, PartsPanel.ControlsHeightFor(320f, 410f));
        Assert.Equal(320f, PartsPanel.ControlsHeightFor(320f, 120f));
    }

    [Fact]
    [Trait("Plan", "EDGE-part-drawer-4")]
    public void AWiderRowWidensOnlyTheDrawer()
    {
        var narrow = Layout(1400f, true, true, wanted: 300f);
        var wide = Layout(1400f, true, true, wanted: 380f);

        Assert.Equal(narrow.Settings, wide.Settings);
        Assert.Equal(80f, narrow.Viewer - wide.Viewer);
    }

    [Fact]
    [Trait("Plan", "EDGE-part-drawer-6")]
    public void ToggleModelViewKeepsTheDrawerOpen()
    {
        Assert.True(Layout(1200f, true, true).Drawer > 0f);
        Assert.Equal(1200f - Settings - Spacing, Layout(1200f, false, true).Drawer);
    }

    /// <summary>Opening beside the model widens the window by what the drawer needs beyond the room already free
    /// beside a viewer its height holds narrow, in unscaled pixels; with the model view off it asks for nothing.</summary>
    [Fact]
    [Trait("Plan", "AC-part-drawer-4")]
    public void NoResizeWithoutTheModelView()
    {
        // Viewer room 700, image 600 wide: 100 already free, so 380 + 8 − 100.
        Assert.Equal(288f, PartsPanel.DrawerOpenGrow(true, 380f, 8f, viewerRoom: 700f, viewerNatural: 600f, scale: 1f));
        // Scaled pixels in, unscaled out.
        Assert.Equal(288f, PartsPanel.DrawerOpenGrow(true, 570f, 12f, viewerRoom: 1050f, viewerNatural: 900f, scale: 1.5f)!.Value, 3);
        // More room free than the drawer needs: nothing to grow.
        Assert.Equal(0f, PartsPanel.DrawerOpenGrow(true, 380f, 8f, viewerRoom: 1200f, viewerNatural: 600f, scale: 1f));
        Assert.Null(PartsPanel.DrawerOpenGrow(false, 380f, 8f, viewerRoom: 700f, viewerNatural: 600f, scale: 1f));
    }
}
