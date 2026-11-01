using System.Linq;
using Proteus.TestHarness;
using Xunit;

namespace Proteus.Tests.TestHarness;

public class LogRingTests
{
    [Fact]
    public void Seq_Monotonic_SinceFiltering_MaxCap()
    {
        // [AC-qa-harness-6]
        var ring = new LogRing(new SessionNames());
        for (int i = 1; i <= 10; i++) ring.Add("line " + i);
        var page = ring.Read(since: 3, max: 4);
        Assert.Equal([4L, 5, 6, 7], page.Lines.Select(l => l.Seq));
        Assert.Equal(7, page.Next);
        Assert.False(page.Dropped);
        Assert.Equal(1, page.Oldest);
        Assert.Equal([8L, 9, 10], ring.Read(null, 3).Lines.Select(l => l.Seq));
        Assert.Equal(10, ring.Read(null, 5).Next);
    }

    [Fact]
    public void SinceInFuture_Empty()
    {
        // [EDGE-qa-harness-26] Nothing, and the cursor unchanged.
        var ring = new LogRing(new SessionNames());
        ring.Add("a");
        var page = ring.Read(99, 10);
        Assert.Empty(page.Lines);
        Assert.Equal(99, page.Next);
    }

    [Fact]
    public void SinceBeforeOldest_Dropped()
    {
        // [EDGE-qa-harness-26]
        var ring = new LogRing(new SessionNames(), capacity: 3);
        for (int i = 0; i < 6; i++) ring.Add("x" + i);
        var page = ring.Read(1, 10);
        Assert.True(page.Dropped);
        Assert.Equal(4, page.Oldest);
        Assert.Equal([4L, 5, 6], page.Lines.Select(l => l.Seq));
        Assert.False(ring.Read(3, 10).Dropped);
    }

    [Fact]
    public void Wraps_AtCapacity()
    {
        // [EDGE-qa-harness-26]
        var ring = new LogRing(new SessionNames());
        for (int i = 0; i < LogRing.DefaultCapacity + 5; i++) ring.Add("x");
        var page = ring.Read(0, LogRing.MaxMax + 500);
        Assert.Equal(LogRing.MaxMax, page.Lines.Count);
        Assert.Equal(6, page.Oldest);
    }

    [Fact]
    public void Read_ScrubsUserAndSessionNames_WholeTokens()
    {
        // [AC-qa-harness-6] [EDGE-qa-harness-27] The Windows user name and every session name go.
        var names = new SessionNames();
        names.AddAll(["Jane's Look"], "Jane Doe");
        var ring = new LogRing(names);
        ring.Add(@"read C:\Users\someone\AppData\x.mdl for Jane Doe in collection ""Jane's Look""");
        string text = ring.Read(null, 10).Lines[0].Text;
        Assert.DoesNotContain("someone", text);
        Assert.DoesNotContain("Jane", text);
        Assert.Contains("<CHARACTER>", text);
        Assert.Contains("<COLLECTION>", text);
    }

    [Fact]
    public void DefaultAndRue_DefaultModJsonAndTrueUnchanged_StandaloneRueScrubbed()
    {
        // [EDGE-qa-harness-27] Whole tokens only, and the built-in Default is not a name.
        var names = new SessionNames();
        names.AddAll(["Default", "Rue"], null);
        var ring = new LogRing(names);
        ring.Add("default_mod.json is true; collection Rue; rue_s.mdl; Default");
        string text = ring.Read(null, 10).Lines[0].Text;
        Assert.Equal("default_mod.json is true; collection <COLLECTION>; rue_s.mdl; Default", text);
    }

    [Fact]
    public void NameSeenBeforeRelog_StillScrubbed()
    {
        // [EDGE-qa-harness-27] A name learned later still comes out of an earlier line, and one from before a relog stays.
        var names = new SessionNames();
        var ring = new LogRing(names);
        ring.Add("hello Alice Smith");
        names.Add("Alice Smith", NameKind.Character);
        names.Add("Bob Jones", NameKind.Character);   // relogged as someone else
        Assert.Equal("hello <CHARACTER>", ring.Read(null, 10).Lines[0].Text);
    }

    [Fact]
    public void ShortNames_NotScrubbed()
    {
        // [EDGE-qa-harness-27]
        var names = new SessionNames();
        names.AddAll(["Al"], "Bo");
        Assert.Equal(0, names.Count);
        Assert.Equal("Al Bo", names.Scrub("Al Bo"));
    }
}
