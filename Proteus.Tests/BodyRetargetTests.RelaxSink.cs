using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using Proteus.Services;
using Xunit;
using Vec3 = Proteus.Services.SecondSkinWriter.Vec3;

namespace Proteus.Tests;

/// <summary>
/// The relax guard (see <see cref="BodyRetarget.RelaxSinkTolerance"/>): a relax round never lays a face whose corners stood
/// clear of the skin onto it through its middle, deeper than the face clearing lifts — the coarse game gilet on The Body
/// SE, every corner 3 mm clear, the pec through the face between them. Scenes: one sheet over the bumped cube, its corners
/// at <c>A</c> (−150, −150), <c>B</c> (150, −150) and <c>C</c> (0, 150) mm, the bump under the face's middle (a quarter
/// of A, a quarter of B, half of C); the skin's distance read off the cube's flat face, z = 200 mm, so the corners'
/// standoff is what each scene gives it.
/// </summary>
public partial class BodyRetargetTests
{
    /// <summary>How far a point stands off the cube's +Z face, flat (the bump is the face test's to read).</summary>
    private static float OffFlat(Vector3 p) => p.Z - 0.20f;

    /// <summary>
    /// The sheet with its corners authored at <paramref name="bases"/> (z, mm) and moved by <paramref name="lifts"/> (z,
    /// mm), over the cube whose bump's top stands at <paramref name="bumpTop"/> mm. Worked through in the scenes'
    /// simulation: the relax evens the three moves toward each other half a step a round.
    /// </summary>
    private static (BodyRetarget.FaceCheck Check, Vec3[] Delta, BodyRetarget.Sets Sets) Scene(
        (float A, float B, float C) bases, (float A, float B, float C) lifts, float bumpTop)
    {
        float[] pos =
        [
            -0.15f, -0.15f, bases.A / 1000f, 0.15f, -0.15f, bases.B / 1000f, 0f, 0.15f, bases.C / 1000f,
        ];
        var garment = Build(pos, [0, 0, 1, 0, 0, 1, 0, 0, 1], [0, 1, 2], ClothMaterial);
        var (check, delta, sets) = FaceSetup(garment, Cube(0.20f, SkinMaterial), Bumped(bumpTop / 1000f - 0.20f));
        delta[sets.NodeOf[0]] = new Vec3(0f, 0f, lifts.A / 1000f);
        delta[sets.NodeOf[1]] = new Vec3(0f, 0f, lifts.B / 1000f);
        delta[sets.NodeOf[2]] = new Vec3(0f, 0f, lifts.C / 1000f);
        return (check, delta, sets);
    }

    /// <summary>What the face clearing leaves to the guard: a face ending deeper than it lifts.</summary>
    private const float BeyondReach = BodyRetarget.ClearFaceDeepest - BodyRetarget.Clearance;

    [Fact]
    [Trait("Plan", "AC-relax-sink-guard-1")]
    [Trait("Plan", "EDGE-relax-sink-guard-4")]
    public void Relax_never_lays_a_face_onto_skin_through_its_middle()
    {
        // Corners 6, 6 and 15 mm off; the face clears the bump's top by 0.1 mm. Evened out, the corners come down to 6.8,
        // 6.8 and 7.1 mm — every one clear — and the face's middle sinks 2 mm onto the bump: beyond the face clearing.
        var (check, delta, sets) = Scene((203, 203, 203), (0, 0, 12), 208.9f);
        Assert.Equal(0f, check.Depth(0, delta));
        var plain = (Vec3[])delta.Clone();
        BodyRetarget.Relax(sets, plain, OffFlat);
        Assert.True(check.Depth(0, plain) > BeyondReach,
                    $"the plain relax should sink the face beyond reach, but the bump is {check.Depth(0, plain) * 1000:0.00} mm through");
        Assert.All(Enumerable.Range(0, 3), v => Assert.True(OffFlat(check.At(sets.NodeOf[v], plain)) >= BodyRetarget.RelaxGuardStandoff));

        BodyRetarget.Relax(sets, delta, OffFlat, faces: check);
        Assert.True(check.Depth(0, delta) <= BodyRetarget.RelaxSinkTolerance,
                    $"the bump is {check.Depth(0, delta) * 1000:0.00} mm through the guarded face");
        Assert.False(check.TurnedOver(0, delta));
    }

    [Fact]
    [Trait("Plan", "EDGE-relax-sink-guard-16")]
    public void Relax_guard_leaves_a_sink_the_face_clearing_lifts()
    {
        // The same corners over a lower bump: the face ends 0.3 mm through, which the face clearing lifts — and does.
        var (check, delta, sets) = Scene((203, 203, 203), (0, 0, 12), 207.2f);
        Assert.Single(check.Faces);
        Assert.False(check.Unliftable[0]);
        var plain = (Vec3[])delta.Clone();
        BodyRetarget.Relax(sets, plain, OffFlat);
        Assert.InRange(check.Depth(0, plain), 1e-5f, BeyondReach);
        BodyRetarget.Relax(sets, delta, OffFlat, faces: check);
        Assert.Equal(plain, delta);

        float worst = 0f;
        BodyRetarget.ClearFaces(check, delta, new float[sets.NodeCount], ref worst);
        Assert.Equal(0f, check.Need(check.Faces[0], delta, 1e-4f).Push);
    }

    [Fact]
    [Trait("Plan", "EDGE-relax-sink-guard-21")]
    public void Relax_guard_leaves_a_shallow_sink_on_an_unliftable_face_as_1373_did()
    {
        // As above, the face one the push-out leaves where it is (a corner it does not consider): not taken back, and the
        // face clearing does not lift it either — as build 1373 left it.
        var (_, delta, sets) = Scene((203, 203, 203), (0, 0, 12), 207.2f);
        var garment = Build([-0.15f, -0.15f, 0.203f, 0.15f, -0.15f, 0.203f, 0f, 0.15f, 0.203f], [0, 0, 1, 0, 0, 1, 0, 0, 1],
                            [0, 1, 2], ClothMaterial);
        var source = Cube(0.20f, SkinMaterial);
        var target = Bumped(0.0072f);
        Assert.True(IdentityCorrespondence.TryBuild(source, target, "chest", out var built, out string refusal), refusal);
        BodyRetarget.SlotPair[] pairs = [new("_top", built!, target)];
        var authored = Enumerable.Repeat(float.MaxValue, sets.NodeCount).ToArray();
        var check = new BodyRetarget.FaceCheck(sets, sets.ClothNodes.Where(n => n != sets.NodeOf[2]).ToList(),
                                               BodyRetarget.TargetBody.Build(pairs, null, null, before: true),
                                               BodyRetarget.TargetBody.Build(pairs, null, null, before: false), authored,
                                               clearBody: false);
        Assert.Empty(check.Faces);
        Assert.True(check.Unliftable[0]);
        var plain = (Vec3[])delta.Clone();
        BodyRetarget.Relax(sets, plain, OffFlat);
        BodyRetarget.Relax(sets, delta, OffFlat, faces: check);
        Assert.Equal(plain, delta);
        float depth = check.Depth(0, delta), worst = 0f;
        Assert.True(depth > 0f);
        BodyRetarget.ClearFaces(check, delta, new float[sets.NodeCount], ref worst);
        Assert.Equal(depth, check.Depth(0, delta));
        Assert.NotNull(garment);
    }

    [Fact]
    [Trait("Plan", "EDGE-relax-sink-guard-17")]
    public void Relax_guard_leaves_a_face_lying_on_the_skin()
    {
        // Corner A lying 1.5 mm off the skin as the relax begins: the face sinks 2.3 mm, beyond reach, and is left to the
        // passes built for cloth on the skin.
        var (check, delta, sets) = Scene((201.5f, 203, 203), (0, 0, 12), 208.9f);
        var plain = (Vec3[])delta.Clone();
        BodyRetarget.Relax(sets, plain, OffFlat);
        Assert.True(check.Depth(0, plain) > BeyondReach);
        BodyRetarget.Relax(sets, delta, OffFlat, faces: check);
        Assert.Equal(plain, delta);
    }

    [Fact]
    [Trait("Plan", "EDGE-relax-sink-guard-20")]
    public void Relax_guard_judges_corners_where_the_relax_found_them()
    {
        // Corners 2, 5 and 7 mm off as the relax begins. Round 1 takes B in to 1.75 mm and the face 0.9 mm through — within
        // the face clearing's reach, nothing to take back; rounds 2 and 3 take it 1.2 mm through. B is under 2 mm by then,
        // but it stood clear where the relax found it: the guard still holds the face.
        var (check, delta, sets) = Scene((206, 198, 198), (-4, 5, 9), 204.6f);
        var standsOff = BodyRetarget.StandsOff(sets, delta, OffFlat);
        Assert.All(Enumerable.Range(0, 3), v => Assert.True(standsOff[sets.NodeOf[v]]));
        var plain = (Vec3[])delta.Clone();
        var guarded = (Vec3[])delta.Clone();
        BodyRetarget.Relax(sets, plain, OffFlat, rounds: 1);
        BodyRetarget.Relax(sets, guarded, OffFlat, faces: check, rounds: 1, standsOff: standsOff);
        Assert.Equal(plain, guarded);
        Assert.True(OffFlat(check.At(sets.NodeOf[1], plain)) < BodyRetarget.RelaxGuardStandoff);
        Assert.InRange(check.Depth(0, plain), 1e-5f, BeyondReach);

        for (int round = 1; round < BodyRetarget.RelaxRounds; round++)
        {
            float start = check.Depth(0, guarded);
            BodyRetarget.Relax(sets, plain, OffFlat, rounds: 1);
            BodyRetarget.Relax(sets, guarded, OffFlat, faces: check, rounds: 1, standsOff: standsOff);
            float now = check.Depth(0, guarded);
            Assert.False(now > BeyondReach && now > start + BodyRetarget.RelaxSinkTolerance,
                         $"round {round + 1}: {now * 1000:0.00} mm through, from {start * 1000:0.00}");
        }
        Assert.True(check.Depth(0, plain) > BeyondReach, $"the plain relax ends {check.Depth(0, plain) * 1000:0.00} mm through");
    }

    [Fact]
    [Trait("Plan", "EDGE-relax-sink-guard-18")]
    public void Relax_guard_needs_the_skins_distance()
    {
        var (check, delta, sets) = Scene((203, 203, 203), (0, 0, 12), 208.9f);
        Assert.Throws<ArgumentException>(() => BodyRetarget.Relax(sets, delta, null, faces: check));
    }

    [Fact]
    [Trait("Plan", "EDGE-relax-sink-guard-2")]
    [Trait("Plan", "EDGE-relax-sink-guard-11")]
    public void Relax_guard_changes_nothing_where_no_face_sinks()
    {
        // The same corners over a bump the face never comes near: the guard takes nothing back.
        var (check, delta, sets) = Scene((203, 203, 203), (0, 0, 12), 201f);
        var plain = (Vec3[])delta.Clone();
        Assert.True(BodyRetarget.Relax(sets, plain, OffFlat) > 0);
        BodyRetarget.Relax(sets, delta, OffFlat, faces: check);
        Assert.Equal(plain, delta);

        // And beside a face that does sink beyond reach, a sheet away from it is relaxed exactly as without the guard.
        float[] pos =
        [
            -0.15f, -0.15f, 0.203f, 0.15f, -0.15f, 0.203f, 0f, 0.15f, 0.203f,
            0.16f, 0.16f, 0.203f, 0.19f, 0.16f, 0.203f, 0.175f, 0.19f, 0.203f,
        ];
        float[] nrm = [0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1];
        var garment = Build(pos, nrm, [0, 1, 2, 3, 4, 5], ClothMaterial);
        var (twoCheck, two, twoSets) = FaceSetup(garment, Cube(0.20f, SkinMaterial), Bumped(0.0089f));
        two[twoSets.NodeOf[2]] = new Vec3(0f, 0f, 0.012f);
        two[twoSets.NodeOf[5]] = new Vec3(0f, 0f, 0.004f);
        var plainTwo = (Vec3[])two.Clone();
        BodyRetarget.Relax(twoSets, plainTwo, OffFlat);
        BodyRetarget.Relax(twoSets, two, OffFlat, faces: twoCheck);
        Assert.NotEqual(plainTwo[twoSets.NodeOf[2]], two[twoSets.NodeOf[2]]);
        for (int v = 3; v < 6; v++)
        {
            int n = twoSets.NodeOf[v];
            Assert.NotEqual(default, plainTwo[n]);
            Assert.Equal(plainTwo[n], two[n]);
        }
    }

    [Fact]
    [Trait("Plan", "EDGE-relax-sink-guard-1")]
    public void Relax_may_move_a_face_already_through_so_long_as_not_deeper()
    {
        // Corners 5, 5 and 2 mm off and the bump 1.3 mm through the face; evening the corners out lifts it off.
        var (check, delta, sets) = Scene((203, 203, 203), (2, 2, -1), 204.8f);
        float start = check.Depth(0, delta);
        Assert.True(start > BeyondReach, $"the bump should start beyond reach, but is {start * 1000:0.00} mm through");
        var before = (Vec3[])delta.Clone();

        BodyRetarget.Relax(sets, delta, OffFlat, faces: check);

        Assert.NotEqual(before, delta);
        Assert.True(check.Depth(0, delta) < start, $"{check.Depth(0, delta) * 1000:0.00} mm through, from {start * 1000:0.00}");
    }

    /// <summary>
    /// A coarse sheet 3 mm over the bumped cube, its corners moved out at random (fixed seed); some held, one welded to a
    /// skin triangle the cloth does not own, and the cell over the bump standing out to come down onto it.
    /// </summary>
    private static (BodyRetarget.FaceCheck Check, Vec3[] Delta, BodyRetarget.Sets Sets, bool[] Stay) NoisyScene()
    {
        const int cells = 7;
        const float step = 0.05f, from = -0.175f, z = 0.203f;
        var pos = new List<float>();
        var nrm = new List<float>();
        var tris = new List<int>();
        for (int j = 0; j <= cells; j++)
        for (int i = 0; i <= cells; i++)
        {
            pos.AddRange([from + i * step, from + j * step, z]);
            nrm.AddRange([0f, 0f, 1f]);
        }
        for (int j = 0; j < cells; j++)
        for (int i = 0; i < cells; i++)
        {
            int p00 = j * (cells + 1) + i, p10 = p00 + 1, p01 = p00 + cells + 1, p11 = p01 + 1;
            tris.AddRange([p00, p10, p11, p00, p11, p01]);
        }
        var cloth = Build(pos.ToArray(), nrm.ToArray(), tris.ToArray(), ClothMaterial);
        // A skin triangle sharing the grid's corner vertex 0, the cloth's vertices first.
        int skinBase = pos.Count / 3;
        pos.AddRange([from, from, z, from - 0.02f, from, z, from, from - 0.02f, z]);
        nrm.AddRange([0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f]);
        var garment = new ModelParts
        {
            Positions = pos.ToArray(),
            Normals = nrm.ToArray(),
            MeshSpans = [new MeshSpan(0, 0, pos.Count / 3)],
            Parts =
            [
                cloth.Parts[0],
                new ModelPart
                {
                    Mesh = 0, Submesh = 1, Island = -1, Label = "0.1", Material = SkinMaterial,
                    Triangles = [skinBase, skinBase + 2, skinBase + 1], Ordinals = [0], AttributeMask = 0,
                    Min = new Vector3(from - 0.02f, from - 0.02f, z), Max = new Vector3(from, from, z), Toggleable = true,
                },
            ],
            AttributeNames = [],
            Min = new Vector3(from - 0.02f, from - 0.02f, z),
            Max = new Vector3(-from, -from, z),
            ShatteredSubmeshes = new Dictionary<string, int>(),
        };
        var (check, delta, sets) = FaceSetup(garment, Cube(0.20f, SkinMaterial), Bumped(0.0089f));
        var random = new Random(1374);
        var stay = new bool[sets.NodeCount];
        foreach (int n in sets.ClothNodes)
        {
            delta[n] = new Vec3((float)(random.NextDouble() - 0.5) * 0.002f, (float)(random.NextDouble() - 0.5) * 0.002f,
                                (float)random.NextDouble() * 0.006f);
            stay[n] = random.NextDouble() < 0.1;
        }
        // The cell over the bump (its middle at the origin) stands out, free to come down onto it.
        foreach (int v in new[] { 27, 28, 35, 36 })
        {
            delta[sets.NodeOf[v]] = new Vec3(0f, 0f, 0.008f);
            stay[sets.NodeOf[v]] = false;
        }
        return (check, delta, sets, stay);
    }

    [Theory]
    [InlineData(BodyRetarget.RelaxTakeBackPasses)]
    [InlineData(1)]
    [Trait("Plan", "EDGE-relax-sink-guard-3")]
    [Trait("Plan", "EDGE-relax-sink-guard-4")]
    [Trait("Plan", "EDGE-relax-sink-guard-5")]
    [Trait("Plan", "EDGE-relax-sink-guard-12")]
    public void Relax_guard_holds_on_a_noisy_coarse_sheet(int passCap)
    {
        // Round by round, with the corners' standoff read where the relax found them: no face that stood clear ends a round
        // deeper than it began it beyond the face clearing's reach, nothing turns over, nothing held moves.
        // [EDGE-relax-sink-guard-12] With one pass a take-back's cascade cannot be followed: the round goes back whole.
        var (check, delta, sets, stay) = NoisyScene();
        var start = (Vec3[])delta.Clone();
        var standsOff = BodyRetarget.StandsOff(sets, start, OffFlat);
        var turned = Enumerable.Range(0, check.Tris.Count).Select(t => check.TurnedOver(t, start)).ToArray();
        bool Eligible(int t) => check.IsCloth[t] && standsOff[check.Tris[t].A] && standsOff[check.Tris[t].B] && standsOff[check.Tris[t].C];
        bool Broke(Vec3[] before, Vec3[] after) => Enumerable.Range(0, check.Tris.Count).Any(
            t => Eligible(t) && check.Depth(t, after) > BeyondReach
                 && check.Depth(t, after) > check.Depth(t, before) + BodyRetarget.RelaxSinkTolerance);

        // The scene reproduces: unguarded, some round lays an eligible face beyond reach.
        var plain = (Vec3[])start.Clone();
        bool broke = false;
        for (int round = 0; round < BodyRetarget.RelaxRounds; round++)
        {
            var was = (Vec3[])plain.Clone();
            BodyRetarget.Relax(sets, plain, OffFlat, stay, rounds: 1);
            broke |= Broke(was, plain);
        }
        Assert.True(broke, "the unguarded relax should lay a face that stood clear beyond the face clearing's reach");

        var guarded = (Vec3[])start.Clone();
        for (int round = 0; round < BodyRetarget.RelaxRounds; round++)
        {
            var was = (Vec3[])guarded.Clone();
            BodyRetarget.Relax(sets, guarded, OffFlat, stay, faces: check, passCap: passCap, rounds: 1, standsOff: standsOff);
            Assert.False(Broke(was, guarded), $"round {round + 1} laid a face beyond reach");
            for (int t = 0; t < check.Tris.Count; t++)
                Assert.False(!turned[t] && check.TurnedOver(t, guarded), $"round {round + 1} turned triangle {t} over");
        }
        Assert.NotEqual(start, guarded);
        for (int n = 0; n < sets.NodeCount; n++)
            if (stay[n] || !sets.ClothNodes.Contains(n))
                Assert.Equal(start[n], guarded[n]);
        Assert.Contains(sets.SkinNodes, n => n == sets.NodeOf[0]);   // the welded corner is skin, never moved

        // Six single rounds are one six-round relax.
        var whole = (Vec3[])start.Clone();
        BodyRetarget.Relax(sets, whole, OffFlat, stay, faces: check, passCap: passCap);
        Assert.Equal(guarded, whole);
    }

    [Fact]
    [Trait("Plan", "AC-relax-sink-guard-3")]
    [Trait("Plan", "EDGE-relax-sink-guard-6")]
    public void Relax_without_faces_is_build_1373s()
    {
        // The pleated sheet of Clearing_faces_never_turns_a_neighbour_over, its far corner 12 mm out of line: the first round
        // lifts the near corners 2 mm, which tips the pleat onto its back, and is taken back for them.
        float[] pos =
        [
            -0.15f, -0.15f, SheetZ, 0.15f, -0.15f, SheetZ, 0f, 0.15f, SheetZ,
            0f, -0.1505f, SheetZ + 0.001f,
        ];
        float[] nrm = [0, 0, 1, 0, 0, 1, 0, 0, 1, 0, -1, 0];
        Vec3[] Relaxed(bool pleat)
        {
            var garment = Build(pleat ? pos : pos[..9], pleat ? nrm : nrm[..9], pleat ? [0, 1, 2, 1, 0, 3] : [0, 1, 2], ClothMaterial);
            var sets = BodyRetarget.Sets.From(garment);
            var delta = new Vec3[sets.NodeCount];
            delta[sets.NodeOf[2]] = new Vec3(0f, 0f, 0.012f);
            BodyRetarget.Relax(sets, delta, null);
            Assert.Equal(0, Folded(garment, delta, sets));
            return Enumerable.Range(0, 3).Select(v => delta[sets.NodeOf[v]]).ToArray();
        }
        var pleated = Relaxed(true);
        // The fold take-back is what holds the near corners back: without the pleat they rise further.
        Assert.True(Relaxed(false)[0].Z > pleated[0].Z + 1e-4f, $"{Relaxed(false)[0].Z} against {pleated[0].Z}");
        var bytes = pleated.SelectMany(d => BitConverter.GetBytes(d.X).Concat(BitConverter.GetBytes(d.Y)).Concat(BitConverter.GetBytes(d.Z)))
                           .ToArray();
        string sha = Convert.ToHexString(SHA256.HashData(bytes));
        Assert.True(Relax1373 == sha, sha);
    }

    /// <summary>The pleated sheet's relaxed corners as build 1373's Relax left them.</summary>
    private const string Relax1373 = "3901E48651FB3A59CD2AB2B40EC6548C0B3D17B6AD4AE2CA2E86F3CBD67BBC86";

    [Fact]
    [Trait("Plan", "AC-relax-sink-guard-3")]
    [Trait("Plan", "EDGE-relax-sink-guard-6")]
    [Trait("Plan", "EDGE-relax-sink-guard-22")]
    public void Relax_guard_follows_the_flag_not_clear_body()
    {
        // The push-out's own clearing of the body is not what asks for the guard: only the game's own gear's flag does,
        // on top of it, and only with no breast in play; the knob turns it off.
        Assert.False(BodyRetarget.GuardsRelax(relaxSinkGuard: false, clearBody: true, []));
        Assert.False(BodyRetarget.GuardsRelax(relaxSinkGuard: true, clearBody: false, []));
        Assert.True(BodyRetarget.GuardsRelax(relaxSinkGuard: true, clearBody: true, []));
        Assert.False(BodyRetarget.WithTuning(new BodyRetarget.Tuning(NoRelaxSinkGuard: true),
                                             () => BodyRetarget.GuardsRelax(relaxSinkGuard: true, clearBody: true, [])));
        Assert.False(BodyRetarget.GuardsRelax(relaxSinkGuard: true, clearBody: true, [Unreadable()]));
    }

    [Fact]
    [Trait("Plan", "EDGE-relax-sink-guard-22")]
    public void Breast_in_play_fails_closed()
    {
        // A chest whose file cannot be read counts as one with a breast; a chest pair with no file adds nothing.
        Assert.True(BodyRetarget.BreastInPlay([Unreadable()]));
        var cube = Cube(0.20f, SkinMaterial);
        Assert.True(IdentityCorrespondence.TryBuild(cube, cube, "chest", out var built, out string refusal), refusal);
        Assert.False(BodyRetarget.BreastInPlay([new BodyRetarget.SlotPair("_top", built!, cube)]));
        // Legs never count, readable or not.
        Assert.False(BodyRetarget.BreastInPlay([new BodyRetarget.SlotPair("_dwn", built!, cube, [1, 2, 3, 4])]));
    }

    /// <summary>A chest pair over the cube whose body file is not a model.</summary>
    private static BodyRetarget.SlotPair Unreadable()
    {
        var cube = Cube(0.20f, SkinMaterial);
        Assert.True(IdentityCorrespondence.TryBuild(cube, cube, "chest", out var built, out string refusal), refusal);
        return new BodyRetarget.SlotPair("_top", built!, cube, [1, 2, 3, 4]);
    }
}
