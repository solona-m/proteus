using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Penumbra.Api.Enums;
using Proteus.TestHarness;
using Xunit;

namespace Proteus.Tests.TestHarness;

/// <summary>
/// The install route (plan genital-switches, in-game QA): Penumbra's own installer is asked to install a local .pmp, and
/// the job finds the new folder by the package's own name. Proteus writes and deletes nothing under the mod root.
/// </summary>
[Collection(HarnessTimingCollection.Name)]
public class HarnessInstallModTests : IDisposable
{
    private readonly HarnessFixture f = new(HarnessFixture.FastTimings with
    {
        InstallPoll = TimeSpan.FromMilliseconds(20), InstallWait = TimeSpan.FromMilliseconds(400),
    });

    public void Dispose() => f.Dispose();

    /// <summary>A .pmp in the temp folder whose meta.json names it <paramref name="name"/> (null: no meta.json).</summary>
    private string Pack(string? name = "Test Pack")
    {
        string path = Path.Combine(f.Temp.Sub("packs"), Guid.NewGuid().ToString("N") + ".pmp");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            if (name != null)
                using (var w = new StreamWriter(zip.CreateEntry("meta.json").Open()))
                    w.Write(JsonSerializer.Serialize(new { FileVersion = 3, Name = name }));
            using (var w = new StreamWriter(zip.CreateEntry("default_mod.json").Open())) w.Write("{}");
        }
        return path;
    }

    private static string Body(string pmp) => JsonSerializer.Serialize(new { pmp });

    [Fact]
    [Trait("Plan", "AC-genital-switches-10")]
    public async Task Install_asks_Penumbra_and_reports_the_new_folder()
    {
        string pmp = Pack();
        f.Game.InstallAdds = [("Test Pack", "Test Pack")];
        var before = FolderState.Of(f.ModsRoot);
        var (status, body) = await f.Post("/actions/install-mod", Body(pmp));
        Assert.Equal(202, status);
        var job = await f.WaitJob(body.GetProperty("job").GetString()!);
        Assert.Equal("done", job.GetProperty("state").GetString());
        Assert.Equal("Test Pack", job.GetProperty("result").GetProperty("modDir").GetString());
        Assert.Equal([pmp], f.Game.Installs);
        Assert.Contains(f.Game.Calls, c => c.Member == "InstallMod");
        Assert.Equal(before, FolderState.Of(f.ModsRoot));   // nothing written under the mod root by Proteus
        Assert.True(File.Exists(pmp));
    }

    [Fact]
    [Trait("Plan", "EDGE-genital-switches-42")]
    public async Task An_install_already_listed_is_reused_never_doubled()
    {
        f.AddMod("Already There");
        f.Game.ModFolders["Already There"] = "Test Pack";
        var (status, body) = await f.Post("/actions/install-mod", Body(Pack()));
        Assert.Equal(409, status);
        Assert.Equal("exists", body.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("Already There", body.GetProperty("modDir").GetString());
        Assert.Empty(f.Game.Installs);
    }

    [Fact]
    [Trait("Plan", "EDGE-genital-switches-43")]
    public async Task An_unrelated_new_folder_is_never_taken_for_the_install()
    {
        // Penumbra's import fails quietly; the user installs something else meanwhile: that folder is new, but not ours.
        f.Game.InstallAdds = [("Something Else", "Something Else")];
        var (_, body) = await f.Post("/actions/install-mod", Body(Pack()));
        var job = await f.WaitJob(body.GetProperty("job").GetString()!);
        Assert.Equal("failed", job.GetProperty("state").GetString());
        Assert.Equal("install-ambiguous", job.GetProperty("error").GetProperty("code").GetString());
        Assert.Contains("Something Else", job.GetProperty("result").GetProperty("candidates").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    [Trait("Plan", "EDGE-genital-switches-43")]
    public async Task Ours_is_picked_out_among_other_new_folders()
    {
        f.Game.InstallAdds = [("Something Else", "Something Else"), ("Test Pack (2)", "Test Pack")];
        var (_, body) = await f.Post("/actions/install-mod", Body(Pack()));
        var job = await f.WaitJob(body.GetProperty("job").GetString()!);
        Assert.Equal("done", job.GetProperty("state").GetString());
        Assert.Equal("Test Pack (2)", job.GetProperty("result").GetProperty("modDir").GetString());
    }

    [Fact]
    [Trait("Plan", "EDGE-genital-switches-46")]
    public async Task No_new_mod_within_the_wait_times_out_and_names_it()
    {
        var (_, body) = await f.Post("/actions/install-mod", Body(Pack()));
        var job = await f.WaitJob(body.GetProperty("job").GetString()!);
        Assert.Equal("install-timeout", job.GetProperty("error").GetProperty("code").GetString());
        Assert.Contains("Test Pack", job.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    [Trait("Plan", "EDGE-genital-switches-47")]
    public async Task The_installers_refusal_is_said()
    {
        f.Game.InstallAnswer = PenumbraApiEc.InvalidArgument;
        var (status, body) = await f.Post("/actions/install-mod", Body(Pack()));
        Assert.Equal(500, status);
        Assert.Equal("install-failed", body.GetProperty("error").GetProperty("code").GetString());
        f.Game.InstallAnswer = PenumbraApiEc.Success;
        f.Game.PenumbraUp = false;
        (status, body) = await f.Post("/actions/install-mod", Body(Pack()));
        Assert.Equal(503, status);
        Assert.Equal("penumbra-unavailable", body.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    [Trait("Plan", "EDGE-genital-switches-44")]
    [Trait("Plan", "EDGE-genital-switches-41")]
    public async Task Install_validates_its_input()
    {
        async Task<(int, string?)> Error(string json)
        {
            var (s, b) = await f.Post("/actions/install-mod", json);
            return (s, b.ValueKind == JsonValueKind.Object && b.TryGetProperty("error", out var e) ? e.GetProperty("code").GetString() : null);
        }
        Assert.Equal((400, "invalid-field"), await Error("{}"));
        Assert.Equal((400, "invalid-field"), await Error(Body("relative/path.pmp")));
        Assert.Equal((400, "invalid-field"), await Error(Body(Path.Combine(f.Temp.Path, "not-a-pack.zip"))));
        Assert.Equal((404, "not-found"), await Error(Body(Path.Combine(f.Temp.Path, "missing.pmp"))));
        Assert.Equal((400, "invalid-field"), await Error(JsonSerializer.Serialize(new { pmp = Pack(), extra = 1 })));
        Assert.Equal((400, "invalid-field"), await Error(Body(Pack(name: null))));   // no meta.json Name
        Assert.Empty(f.Game.Installs);
    }
}
