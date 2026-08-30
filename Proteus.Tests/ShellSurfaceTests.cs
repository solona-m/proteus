using Xunit;

namespace Proteus.Tests;

/// <summary>
/// Path classification for shell surfaces. Pure and table-driven on purpose: this decides which model a
/// shell is cut from, and getting it wrong is the "face art pasted across the whole body" failure that
/// started this work — a failure that is expensive to see in game and free to see here.
/// </summary>
public class ShellSurfaceTests
{
    // ── the human parts ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("chara/human/c1401/obj/face/f0001/material/mt_c1401f0001_fac_a.mtrl", ShellSurfaceKind.Face, "f0001")]
    [InlineData("chara/human/c1401/obj/face/f0101/material/mt_c1401f0101_fac_a.mtrl", ShellSurfaceKind.Face, "f0101")]
    [InlineData("chara/human/c1401/obj/hair/h0133/material/v0001/mt_c1401h0133_hir_a.mtrl", ShellSurfaceKind.Hair, "h0133")]
    [InlineData("chara/human/c1401/obj/tail/t0001/material/v0001/mt_c1401t0001_etc_a.mtrl", ShellSurfaceKind.Tail, "t0001")]
    [InlineData("chara/human/c1801/obj/zear/z0001/material/mt_c1801z0001_zer_a.mtrl", ShellSurfaceKind.Ear, "z0001")]
    // The eyes sit in the face's folder but are their own surface.
    [InlineData("chara/human/c0201/obj/face/f0001/material/mt_c0201f0001_iri_a.mtrl", ShellSurfaceKind.Iris, "f0001")]
    [InlineData("chara/human/c1401/obj/face/f0101/material/mt_c1401f0101_iri_a.mtrl", ShellSurfaceKind.Iris, "f0101")]
    public void HumanParts_classify_with_their_id(string path, ShellSurfaceKind kind, string id)
        => Assert.Equal(new ShellSurfaceKey(kind, id), ShellSurface.KeyFor(path));

    /// <summary>
    /// The iris must not share a surface with the face it lives in. Surfaces resolve once per key and every
    /// layer on a key shares one source model and one mesh filter, so collapsing these gave a character
    /// wearing both an eye overlay and a face overlay a single shell — one overlay's art on the other's
    /// geometry, silently.
    /// </summary>
    [Fact]
    public void Iris_and_face_are_different_surfaces()
    {
        var iris = ShellSurface.KeyFor("chara/human/c0201/obj/face/f0001/material/mt_c0201f0001_iri_a.mtrl");
        var face = ShellSurface.KeyFor("chara/human/c0201/obj/face/f0001/material/mt_c0201f0001_fac_a.mtrl");
        Assert.NotEqual(iris, face);
        Assert.Equal("f0001", iris!.Value.Id);   // same part id — only the kind separates them
        Assert.Equal("f0001", face!.Value.Id);
    }

    /// <summary>`_etc` (lashes, brows) stays Face — only the eye material moves.</summary>
    [Fact]
    public void Only_the_iris_material_leaves_the_face_surface()
        => Assert.Equal(new ShellSurfaceKey(ShellSurfaceKind.Face, "f0001"),
            ShellSurface.KeyFor("chara/human/c0201/obj/face/f0001/material/mt_c0201f0001_etc_a.mtrl"));

    /// <summary>
    /// An eye is millimetres across and the shell push is a millimetre tuned against a torso, so the iris
    /// asks for a fraction of it. Everything else keeps the tuned value.
    /// </summary>
    [Fact]
    public void Iris_asks_for_a_smaller_push_than_every_other_surface()
    {
        var iris = new ShellSurfaceKey(ShellSurfaceKind.Iris, "f0001");
        Assert.True(iris.PushScale < 1f && iris.PushScale > 0f, $"iris push scale was {iris.PushScale}");
        foreach (var kind in new[] { ShellSurfaceKind.Body, ShellSurfaceKind.Face, ShellSurfaceKind.Hair,
                                     ShellSurfaceKind.Tail, ShellSurfaceKind.Ear, ShellSurfaceKind.Native })
            Assert.Equal(1f, new ShellSurfaceKey(kind, "").PushScale);
    }

    [Fact]
    public void Face_ids_are_different_surfaces()
    {
        // Two faces are never worn at once, so this is really about not cutting a shell for a face the
        // character isn't wearing — the ids must not collapse together.
        Assert.NotEqual(
            ShellSurface.KeyFor("chara/human/c1401/obj/face/f0001/material/mt_c1401f0001_fac_a.mtrl"),
            ShellSurface.KeyFor("chara/human/c1401/obj/face/f0101/material/mt_c1401f0101_fac_a.mtrl"));
    }

    // ── the body, including the routes that don't look like one ───────────────

    [Theory]
    [InlineData("chara/human/c1401/obj/body/b0001/material/v0001/mt_c1401b0001_bibo.mtrl")]
    [InlineData("chara/human/c0201/obj/body/b0001/material/v0001/mt_c0201b0001_a.mtrl")]
    public void Body_materials_are_the_body_surface(string path)
        => Assert.Equal(new ShellSurfaceKey(ShellSurfaceKind.Body, ""), ShellSurface.KeyFor(path));

    [Fact]
    public void Body_uv_routed_through_an_equipment_slot_is_still_the_body()
        // Body mods ship body-UV skin through chara/equipment/ — no /obj/body/ segment anywhere, and shells
        // have always been cut from exactly these models.
        => Assert.Equal(new ShellSurfaceKey(ShellSurfaceKind.Body, ""),
            ShellSurface.KeyFor("chara/equipment/e0000/material/v0001/mt_c0201e0000_top_bibo.mtrl"));

    // ── the things no shell can be cut for ────────────────────────────────────

    [Fact]
    public void Weapons_are_not_a_surface_despite_carrying_obj_body()
    {
        // The ordering trap. This is a real path off a live character, and testing /obj/body/ first files an
        // equipped greatsword as skin — which is how a shell would get cut for a sword.
        Assert.Null(ShellSurface.KeyFor(
            "chara/weapon/w0801/obj/body/b0006/material/v0001/mt_w0801b0006_a.mtrl"));
    }

    [Theory]
    [InlineData("chara/equipment/e6039/material/v0001/mt_c0201e6039_sho_a.mtrl")]
    [InlineData("chara/accessory/a0053/material/v0001/mt_c0101a0053_rir_a.mtrl")]
    [InlineData("chara/monster/m0361/obj/body/b0001/material/v0001/mt_m0361b0001_a.mtrl")]
    [InlineData("")]
    [InlineData(null)]
    public void Unshellable_paths_return_null(string? path)
        => Assert.Null(ShellSurface.KeyFor(path));

    [Fact]
    public void A_face_shaped_path_outside_chara_human_is_rejected()
        // Anchored on chara/human/ so nothing else can claim a surface we'd then find no geometry for.
        => Assert.Null(ShellSurface.KeyFor("chara/demihuman/d1001/obj/face/f0001/material/mt_d1001f0001_fac_a.mtrl"));

    // ── the aggregate helpers the compositor and editor call ──────────────────

    [Fact]
    public void KeysFor_deduplicates_and_keeps_order()
    {
        var keys = ShellSurface.KeysFor(new[]
        {
            "chara/human/c1401/obj/face/f0001/material/mt_c1401f0001_fac_a.mtrl",
            "chara/human/c1401/obj/body/b0001/material/v0001/mt_c1401b0001_bibo.mtrl",
            "chara/human/c1401/obj/face/f0001/material/mt_c1401f0001_fac_a.mtrl",
            "chara/weapon/w0801/obj/body/b0006/material/v0001/mt_w0801b0006_a.mtrl",
        });

        Assert.Equal(2, keys.Count);
        Assert.Equal(new ShellSurfaceKey(ShellSurfaceKind.Face, "f0001"), keys[0]);
        Assert.Equal(new ShellSurfaceKey(ShellSurfaceKind.Body, ""), keys[1]);
    }

    [Fact]
    public void CanShell_is_true_for_any_recognised_surface()
    {
        Assert.True(ShellSurface.CanShell(["chara/human/c1401/obj/face/f0001/material/mt_c1401f0001_fac_a.mtrl"]));
        Assert.True(ShellSurface.CanShell(["chara/human/c1401/obj/body/b0001/material/v0001/mt_c1401b0001_bibo.mtrl"]));
        Assert.False(ShellSurface.CanShell(["chara/weapon/w0801/obj/body/b0006/material/v0001/mt_w0801b0006_a.mtrl"]));
    }

    [Fact]
    public void CanShell_allows_an_overlay_that_names_no_material()
        // It cannot be placed either way; keep the prior behaviour rather than quietly removing a feature.
        => Assert.True(ShellSurface.CanShell([]));

    // ── deform policy, which is what the host assignment turns on ─────────────

    [Fact]
    public void Only_the_body_tolerates_a_deforming_host()
    {
        Assert.False(new ShellSurfaceKey(ShellSurfaceKind.Body, "").RequiresNativeHost);
        Assert.True(new ShellSurfaceKey(ShellSurfaceKind.Face, "f0001").RequiresNativeHost);
        Assert.True(new ShellSurfaceKey(ShellSurfaceKind.Hair, "h0133").RequiresNativeHost);
        Assert.True(new ShellSurfaceKey(ShellSurfaceKind.Tail, "t0001").RequiresNativeHost);
        Assert.True(new ShellSurfaceKey(ShellSurfaceKind.Ear, "z0001").RequiresNativeHost);
    }
}
