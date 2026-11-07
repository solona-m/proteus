using System;
using System.IO;
using NSubstitute;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// The face rewrite's upstream after an edit to the face file itself (a matcap on Au Ra horns): forgotten, it is read
/// again from the remembered mod file while our own output wins the path — never from game data.
/// </summary>
public sealed class FaceUvDoublingUpstreamTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "proteus-faceuv-" + Guid.NewGuid().ToString("N"));
    private const string FacePath = "chara/human/c1401/obj/face/f0001/model/c1401f0001_fac.mdl";

    public void Dispose()
    {
        try { Directory.Delete(root, true); } catch { }
    }

    private FaceUvDoublingService Service() =>
        new(Substitute.For<Dalamud.Plugin.Services.IPluginLog>(),
            new TextureLoader(Substitute.For<Dalamud.Plugin.Services.IDataManager>(), Substitute.For<Dalamud.Plugin.Services.IPluginLog>()),
            null!);

    [Fact]
    [Trait("Plan", "EDGE-matcaps-54")]
    [Trait("Plan", "EDGE-matcaps-61")]
    public void Forgotten_upstream_is_reread_from_the_edited_mod_file_while_ours_wins()
    {
        var modFile = Path.Combine(root, "mods", "Face Mod", "face.mdl");
        var ours = Path.Combine(root, "mods", "Proteus", "models", "facelr_x.mdl");
        var models = Path.Combine(root, "mods", "Proteus", "models");
        Directory.CreateDirectory(Path.GetDirectoryName(modFile)!);
        Directory.CreateDirectory(models);
        File.WriteAllBytes(modFile, [1, 2, 3]);
        File.WriteAllBytes(ours, [9, 9, 9]);
        var svc = Service();
        bool IsOurs(string? p) => p != null && p.StartsWith(Path.Combine(root, "mods", "Proteus"), StringComparison.OrdinalIgnoreCase);

        // First composite: the face mod wins; its bytes and path are remembered.
        Assert.Equal([1, 2, 3], svc.LoadUpstream(FacePath, _ => modFile, IsOurs, models));
        // Our rewrite now wins the path; the remembered copy is used.
        Assert.Equal([1, 2, 3], svc.LoadUpstream(FacePath, _ => ours, IsOurs, models));

        // The matcap edits the face mod's file, then forgets the copy: the next load reads the edit, no outside trigger.
        File.WriteAllBytes(modFile, [4, 5, 6]);
        svc.ForgetUpstream(FacePath, Path.Combine(root, "mods", "Proteus"));
        Assert.Equal([4, 5, 6], svc.LoadUpstream(FacePath, _ => ours, IsOurs, models));
    }

    [Fact]
    [Trait("Plan", "EDGE-matcaps-61")]
    public void Nothing_remembered_while_ours_wins_refuses_never_game_data()
    {
        var models = Path.Combine(root, "mods", "Proteus", "models");
        Directory.CreateDirectory(models);
        var ours = Path.Combine(models, "facelr_x.mdl");
        File.WriteAllBytes(ours, [9]);
        var svc = Service();
        svc.ForgetUpstream(FacePath, Path.Combine(root, "mods", "Proteus"));
        Assert.Null(svc.LoadUpstream(FacePath, _ => ours, _ => true, models));
    }

    [Fact]
    [Trait("Plan", "EDGE-matcaps-43")]
    public void A_vanilla_face_copied_into_an_edit_mod_is_seeded_by_the_written_file()
    {
        var models = Path.Combine(root, "mods", "Proteus", "models");
        Directory.CreateDirectory(models);
        var ours = Path.Combine(models, "facelr_x.mdl");
        File.WriteAllBytes(ours, [9]);
        var edit = Path.Combine(root, "mods", "Face — Proteus edit", "face.mdl");
        Directory.CreateDirectory(Path.GetDirectoryName(edit)!);
        File.WriteAllBytes(edit, [7, 7]);
        var svc = Service();
        bool IsOurs(string? p) => p == ours;
        svc.ForgetUpstream(FacePath, Path.Combine(root, "mods", "Proteus"), edit);
        Assert.Equal([7, 7], svc.LoadUpstream(FacePath, _ => ours, IsOurs, models));
    }
}
