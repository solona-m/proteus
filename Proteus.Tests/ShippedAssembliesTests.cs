using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// The plugin no longer references or ships Luna (AGPL-3.0), Penumbra.Api (no licence) or Glamourer.Api (plan D24):
/// Proteus talks to both plugins through its own call gates over Dalamud's IPC.
/// </summary>
public class ShippedAssembliesTests
{
    private const string Plan9 = "AC-contrib-clean-room-9";
    private static readonly string[] Gone = ["Luna", "Penumbra.Api", "Glamourer.Api"];

    private static string RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "proteus.slnx")))
            dir = dir.Parent;
        Assert.True(dir != null, "could not find proteus.slnx");
        return Path.Combine([dir!.FullName, .. parts]);
    }

    [Fact]
    [Trait("Plan", Plan9)]
    [Trait("Plan", "EDGE-contrib-clean-room-20")]
    public void Plugin_project_references_none_of_them()   // [AC-contrib-clean-room-9] [EDGE-contrib-clean-room-20]
    {
        var csproj = File.ReadAllText(RepoFile("Proteus", "Proteus.csproj"));
        Assert.DoesNotContain("Luna.csproj", csproj);
        Assert.DoesNotContain("Penumbra.Api.csproj", csproj);
        Assert.DoesNotContain("Glamourer.Api.csproj", csproj);
    }

    [Fact]
    [Trait("Plan", Plan9)]
    public void Lock_file_lists_none_of_them()   // [AC-contrib-clean-room-9]
    {
        var lockFile = File.ReadAllText(RepoFile("Proteus", "packages.lock.json"));
        foreach (var name in Gone)
            Assert.DoesNotContain("\"" + name.ToLowerInvariant() + "\"", lockFile.ToLowerInvariant());
    }

    [Fact]
    [Trait("Plan", Plan9)]
    public void Plugin_assembly_references_none_of_them()   // [AC-contrib-clean-room-9]
    {
        var refs = typeof(Plugin).Assembly.GetReferencedAssemblies().Select(a => a.Name).ToList();
        foreach (var name in Gone)
            Assert.DoesNotContain(name, refs);
    }
}
