using System.IO;
using System.Runtime.CompilerServices;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// The face-over-body facewear reservation and the glasses carrier's wiring, pinned by reading the source (the
/// compositor and the shell build cannot be built in a test; the pattern of <see cref="StutterFixesWiringTests"/>).
/// Textual and deliberately so: change these with the code.
/// </summary>
public class GlassesCarrierWiringTests
{
    private static string ProteusDir([CallerFilePath] string here = "")
        => Path.Combine(Path.GetDirectoryName(here)!, "..", "Proteus");

    private static string Source(string relative) => File.ReadAllText(Path.Combine(ProteusDir(), relative));

    internal static string Method(string source, string signature)
    {
        int at = source.IndexOf(signature, System.StringComparison.Ordinal);
        Assert.True(at >= 0, $"no {signature}");
        int open = source.IndexOf('{', at), depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source[open..(i + 1)];
        }
        Assert.Fail($"unbalanced {signature}");
        return "";
    }

    internal static void Before(string body, string first, string then)
    {
        int a = body.IndexOf(first, System.StringComparison.Ordinal), b = body.IndexOf(then, System.StringComparison.Ordinal);
        Assert.True(a >= 0, $"no {first}");
        Assert.True(b >= 0, $"no {then}");
        Assert.True(a < b, $"{first} must come before {then}");
    }

    private static string ShellSetBuild => Source("Services/SecondSkinService.ShellSetBuild.cs");
    private static string Allocation => Source("Services/ShellHostAllocation.cs");

    [Fact]
    [Trait("Plan", "EDGE-face-glasses-priority-10")]
    public void DisplacedBodyLineIsDeduplicated() // [EDGE-face-glasses-priority-10]
    {
        var body = Method(ShellSetBuild, "private void LogFacewearReservation(");
        Before(body, "LogLineChanged(ref service.lastFacewearDisplacedLog", "didn't fit because the facewear carrier");
    }

    [Fact]
    [Trait("Plan", "EDGE-face-glasses-priority-11")]
    public void RunRerunsWithoutTheReservationWhenUnused() // [EDGE-face-glasses-priority-11]
    {
        var body = Method(Allocation, "public static State Run(");
        Before(body, "ReservationUnused(s)", "RunWith(null,");
        Before(body, "RunWith(null,", "ReservationDropped = true");
    }

    [Fact]
    [Trait("Plan", "EDGE-face-glasses-priority-12")]
    public void UnbuiltReservedHostIsCheckedAfterTheModelsAreBuilt() // [EDGE-face-glasses-priority-12]
    {
        var body = Method(ShellSetBuild, "private void BuildHostModels(");
        Before(body, "BuildHostModel(pushSweep, h)", "ShellHostAllocation.ReservedButUnbuilt(");
        Before(body, "ShellHostAllocation.ReservedButUnbuilt(", "LogLineChanged(ref service.lastFacewearUnbuiltLog");
    }
}
