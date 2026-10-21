using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// The game's own gear copied into its "— Proteus edit" mod by its first edit, on the filesystem: what is made, what is
/// written, and what an existing mod is never written over. <see cref="RefitModService.Ensure"/> needs Penumbra, so
/// it is stood in for by <see cref="FakeEnsure"/>, which keeps its contract: find by name, else scaffold, fill, roll
/// back on a throw, and only then register.
/// </summary>
public class VanillaEditCopyTests : IDisposable
{
    private const string Item = "Weathered Dancer's Casaque";
    private const string Coat = "chara/equipment/e0543/model/c0201e0543_top.mdl";
    private const string Trousers = "chara/equipment/e0543/model/c0201e0543_dwn.mdl";

    private readonly string mods = Path.Combine(Path.GetTempPath(), "proteus-vanillacopy-" + Guid.NewGuid().ToString("N"));
    private static readonly byte[] GameBytes = [1, 2, 3, 4, 5];
    private static readonly byte[] Edited = [9, 9, 9];

    public VanillaEditCopyTests() => Directory.CreateDirectory(mods);

    public void Dispose()
    {
        try { Directory.Delete(mods, true); } catch { /* best effort */ }
    }

    /// <summary>What <see cref="RefitModService.Ensure"/> does, without Penumbra; records what happened when.</summary>
    private sealed class FakeEnsure(string mods)
    {
        public bool Called;
        public bool Registered;
        public bool WrittenBeforeRegistered;
        public bool FailToMake;

        public RefitModService.Result Run(Action<string> fill)
        {
            Called = true;
            if (RefitModService.Find(mods, Item, RefitModService.EditQualifier) is { } found)
                return new(true, found, Path.GetFileName(found), "");
            if (FailToMake) return new(false, "", "", "Penumbra's mod directory isn't available.");

            string name = VanillaEditCopy.ModName(Item);
            string dir = RefitModService.DirFor(name)!;
            string root = Path.Combine(mods, dir);
            Directory.CreateDirectory(root);
            RefitModService.WriteScaffold(root, name, "test");
            try { fill(root); }
            catch (Exception ex)
            {
                Directory.Delete(root, true);
                return new(false, "", "", "Failed to write the mod: " + ex.Message);
            }
            WrittenBeforeRegistered = File.Exists(Path.Combine(root, Coat.Replace('/', Path.DirectorySeparatorChar)));
            Registered = true;
            return new(true, root, dir, "", Made: true);
        }
    }

    private string Find() => RefitModService.Find(mods, Item, RefitModService.EditQualifier)!;

    private static string FileIn(string root, string rel) => Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>A first write standing in for the brush: the edited bytes, and the brush's record of them.</summary>
    private static VanillaEditCopy.FirstWrite WriteEdit(string root, string rel)
    {
        PenumbraModMeta.AtomicWrite(FileIn(root, rel), Edited);
        var record = Path.Combine(root, SidecarDiscoveryService.SidecarSubdir, MeshVolumeService.RecordFile);
        Directory.CreateDirectory(Path.GetDirectoryName(record)!);
        File.WriteAllText(record, "{\"Files\":[\"" + rel + "\"]}");
        return new(true, "");
    }

    private VanillaEditCopy.Outcome Make(FakeEnsure ensure, Func<string, string, VanillaEditCopy.FirstWrite>? write = null,
                                         bool? switchedOn = true, string gamePath = Coat)
        => VanillaEditCopy.Make(gamePath, GameBytes,
                                () => RefitModService.Find(mods, Item, RefitModService.EditQualifier),
                                _ => switchedOn, ensure.Run, write ?? WriteEdit);

    /// <summary>An existing edit mod holding <paramref name="rel"/> as <paramref name="bytes"/>.</summary>
    private string ExistingMod(string rel, byte[] bytes)
    {
        string name = VanillaEditCopy.ModName(Item);
        string root = Path.Combine(mods, RefitModService.DirFor(name)!);
        Directory.CreateDirectory(root);
        RefitModService.WriteScaffold(root, name, "earlier");
        RefitModService.AddGameModel(root, rel, bytes);
        return root;
    }

    [Fact]
    [Trait("Plan", "AC-vanilla-copy-on-edit-5")]
    [Trait("Plan", "EDGE-vanilla-copy-on-edit-13")]
    public void A_new_copy_holds_the_edit_written_before_Penumbra_is_told()
    {
        var ensure = new FakeEnsure(mods);
        var outcome = Make(ensure);

        Assert.True(outcome.Ok);
        Assert.True(outcome.Made);
        Assert.True(ensure.WrittenBeforeRegistered);
        Assert.Equal(Edited, File.ReadAllBytes(FileIn(outcome.Root, Coat)));
        var data = PenumbraModMeta.TryReadDefaultData(outcome.Root);
        Assert.NotNull(data);
        Assert.Equal(Coat, data!.Value.Files[Coat]);
    }

    [Fact]
    [Trait("Plan", "AC-vanilla-copy-on-edit-5")]
    [Trait("Plan", "EDGE-vanilla-copy-on-edit-13")]
    public void A_real_brush_save_as_the_first_write_backs_up_the_game_model()
    {
        var (mdl, solve, model) = MeshVolumeServiceTests.Setup();
        Assert.True(solve.BeginMove(model.Parts[0].Triangles, adjacent: false, falloffRadius: 0f) > 0);
        solve.MoveTo(new Vector3(0f, 0.01f, 0f));
        solve.EndMove();
        Assert.True(solve.Dirty);

        var ensure = new FakeEnsure(mods);
        var outcome = VanillaEditCopy.Make(Coat, mdl, () => null, _ => true, ensure.Run, (root, rel) =>
        {
            var r = MeshVolumeService.Apply(root, rel, mdl, solve, writeUntouched: true);
            return new(r.Ok, r.Message);
        });

        Assert.True(outcome.Ok);
        Assert.NotEqual(mdl, File.ReadAllBytes(FileIn(outcome.Root, Coat)));
        var backup = Path.Combine(outcome.Root, SidecarDiscoveryService.SidecarSubdir, MeshVolumeService.BackupSubdir,
                                  Coat.Replace('/', Path.DirectorySeparatorChar));
        Assert.Equal(mdl, File.ReadAllBytes(backup));
        Assert.True(MeshVolumeService.IsPatched(outcome.Root, Coat));
    }

    [Fact]
    [Trait("Plan", "AC-vanilla-copy-on-edit-5")]
    [Trait("Plan", "AC-vanilla-copy-on-edit-10")]
    public void The_copy_is_named_after_the_item_then_Proteus_edit()
    {
        Assert.Equal(Item + " — Proteus edit", VanillaEditCopy.ModName(Item));
        var outcome = Make(new FakeEnsure(mods));
        Assert.Equal(outcome.Root, Find());
    }

    [Fact]
    [Trait("Plan", "AC-vanilla-copy-on-edit-5")]
    [Trait("Plan", "EDGE-vanilla-copy-on-edit-20")]
    public void Switches_as_the_first_write_land_in_the_new_copy()
    {
        byte[]? seen = null;
        var outcome = Make(new FakeEnsure(mods), (root, rel) =>
        {
            // What CommitVanilla's write does: the redirects are already there to be read, before registration.
            Assert.Contains(PenumbraModMeta.ReadAllRedirects(root), r => r.File == rel && r.GamePath == Coat);
            seen = File.ReadAllBytes(FileIn(root, rel));
            return new(true, "");
        });
        Assert.True(outcome.Ok);
        Assert.Equal(GameBytes, seen);
    }

    [Fact]
    [Trait("Plan", "EDGE-vanilla-copy-on-edit-8")]
    public void A_refused_first_write_leaves_no_mod_and_says_its_own_reason()
    {
        var outcome = Make(new FakeEnsure(mods), (_, _) => new(false, "Too many switches."));

        Assert.Equal(VanillaEditCopy.Status.WriteRefused, outcome.Status);
        Assert.Equal("Too many switches.", outcome.Message);
        Assert.Empty(Directory.GetDirectories(mods));
    }

    [Fact]
    [Trait("Plan", "EDGE-vanilla-copy-on-edit-8")]
    [Trait("Plan", "EDGE-vanilla-copy-on-edit-10")]
    public void A_refused_first_write_in_an_existing_mod_leaves_it_as_it_was()
    {
        var root = ExistingMod(Trousers, [7, 7]);
        var before = PenumbraModMeta.TryReadDefaultData(root)!.Value.Files;

        var outcome = Make(new FakeEnsure(mods), (_, _) => new(false, "Too many switches."));

        Assert.Equal(VanillaEditCopy.Status.WriteRefused, outcome.Status);
        Assert.Equal("Too many switches.", outcome.Message);
        Assert.False(File.Exists(FileIn(root, Coat)));
        Assert.Equal(before, PenumbraModMeta.TryReadDefaultData(root)!.Value.Files);
        Assert.Equal([7, 7], File.ReadAllBytes(FileIn(root, Trousers)));
    }

    [Fact]
    [Trait("Plan", "EDGE-vanilla-copy-on-edit-7")]
    public void A_mod_that_cannot_be_made_writes_nothing_and_never_calls_the_write()
    {
        bool wrote = false;
        var outcome = Make(new FakeEnsure(mods) { FailToMake = true }, (_, _) => { wrote = true; return new(true, ""); });

        Assert.Equal(VanillaEditCopy.Status.EnsureFailed, outcome.Status);
        Assert.False(wrote);
        Assert.Empty(Directory.GetDirectories(mods));
    }

    [Fact]
    [Trait("Plan", "EDGE-vanilla-copy-on-edit-9")]
    public void An_untouched_copy_in_an_existing_mod_is_reused()
    {
        var root = ExistingMod(Coat, GameBytes);
        var ensure = new FakeEnsure(mods);

        var outcome = Make(ensure);

        Assert.True(outcome.Ok);
        Assert.False(outcome.Made);
        Assert.Equal(root, outcome.Root);
        Assert.False(ensure.Registered);
        Assert.Equal(Edited, File.ReadAllBytes(FileIn(root, Coat)));
    }

    [Fact]
    [Trait("Plan", "EDGE-vanilla-copy-on-edit-10")]
    public void An_existing_mod_with_another_piece_gains_this_one_and_keeps_its_redirects()
    {
        var root = ExistingMod(Trousers, [7, 7]);

        var outcome = Make(new FakeEnsure(mods));

        Assert.True(outcome.Ok);
        var files = PenumbraModMeta.TryReadDefaultData(root)!.Value.Files;
        Assert.Equal(Trousers, files[Trousers]);
        Assert.Equal(Coat, files[Coat]);
        Assert.Equal([7, 7], File.ReadAllBytes(FileIn(root, Trousers)));
        Assert.Equal(Edited, File.ReadAllBytes(FileIn(root, Coat)));
    }

    [Fact]
    [Trait("Plan", "AC-vanilla-copy-on-edit-8")]
    [Trait("Plan", "EDGE-vanilla-copy-on-edit-11")]
    public void An_edited_copy_there_is_refused_and_left_byte_for_byte()
    {
        byte[] theirs = [4, 4, 4, 4];
        var root = ExistingMod(Coat, theirs);
        var ensure = new FakeEnsure(mods);
        bool wrote = false;

        var outcome = Make(ensure, (_, _) => { wrote = true; return new(true, ""); });

        Assert.Equal(VanillaEditCopy.Status.EditedCopyThere, outcome.Status);
        Assert.False(wrote);
        Assert.False(ensure.Called);
        Assert.Equal(theirs, File.ReadAllBytes(FileIn(root, Coat)));
    }

    [Fact]
    [Trait("Plan", "AC-vanilla-copy-on-edit-8")]
    [Trait("Plan", "EDGE-vanilla-copy-on-edit-11")]
    public void A_copy_the_brush_or_the_switches_edited_is_an_edit_even_with_the_game_bytes()
    {
        var root = ExistingMod(Coat, GameBytes);
        Assert.False(VanillaEditCopy.HoldsEdit(root, Coat, GameBytes));

        var volume = Path.Combine(root, SidecarDiscoveryService.SidecarSubdir, MeshVolumeService.RecordFile);
        Directory.CreateDirectory(Path.GetDirectoryName(volume)!);
        File.WriteAllText(volume, "{\"Files\":[\"" + Coat + "\"]}");
        Assert.True(VanillaEditCopy.HoldsEdit(root, Coat, GameBytes));

        File.Delete(volume);
        File.WriteAllText(Path.Combine(root, SidecarDiscoveryService.SidecarSubdir, MeshToggleService.RecordFile),
                          "{\"Items\":[{\"GroupName\":\"Parts\",\"Files\":[\"" + Coat + "\"]}]}");
        Assert.True(VanillaEditCopy.HoldsEdit(root, Coat, GameBytes));
        Assert.False(VanillaEditCopy.HoldsEdit(root, Trousers, GameBytes));   // a piece it does not have is no edit
    }

    [Fact]
    [Trait("Plan", "EDGE-vanilla-copy-on-edit-12")]
    public void An_unreadable_copy_is_refused_not_overwritten()
    {
        var root = ExistingMod(Coat, GameBytes);
        using (new FileStream(FileIn(root, Coat), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var outcome = Make(new FakeEnsure(mods));
            Assert.Equal(VanillaEditCopy.Status.EditedCopyThere, outcome.Status);
        }
        Assert.Equal(GameBytes, File.ReadAllBytes(FileIn(root, Coat)));
    }

    [Fact]
    [Trait("Plan", "AC-vanilla-copy-on-edit-8")]
    [Trait("Plan", "AC-vanilla-copy-on-edit-6")]
    public void A_switched_off_mod_is_not_written_into()
    {
        var root = ExistingMod(Coat, GameBytes);
        var ensure = new FakeEnsure(mods);

        var outcome = Make(ensure, switchedOn: false);

        Assert.Equal(VanillaEditCopy.Status.ModSwitchedOff, outcome.Status);
        Assert.False(ensure.Called);
        Assert.Equal(GameBytes, File.ReadAllBytes(FileIn(root, Coat)));
    }

    [Fact]
    [Trait("Plan", "AC-vanilla-copy-on-edit-6")]
    public void After_the_mod_is_deleted_the_next_edit_makes_it_afresh()
    {
        var first = Make(new FakeEnsure(mods));
        Directory.Delete(first.Root, true);

        var again = Make(new FakeEnsure(mods));

        Assert.True(again.Ok);
        Assert.True(again.Made);
    }

    [Fact]
    [Trait("Plan", "EDGE-vanilla-copy-on-edit-21")]
    public void Wind_materials_on_a_copy_report_not_in_mod()
    {
        var outcome = Make(new FakeEnsure(mods));
        var redirects = PenumbraModMeta.ReadAllRedirects(outcome.Root);

        var mats = MeshVolumeService.ApplyWindMaterials(outcome.Root, ["/mt_c0201e0543_top_a.mtrl"], redirects);

        Assert.Equal(0, mats.Changed);
        Assert.Equal(1, mats.NotInMod);
    }
}
