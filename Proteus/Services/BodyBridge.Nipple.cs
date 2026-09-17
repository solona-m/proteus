using System;
using System.Collections.Generic;

namespace Proteus.Services;

using static Proteus.Services.SecondSkinWriter;

internal static partial class BodyBridge
{
    /// <summary>
    /// The base-skeleton hip bone. Its influence covers the buttocks, the cleft AND the crotch, so unlike
    /// the bust it cannot seed a region on its own. See <see cref="Facing"/>.
    /// </summary>
    internal const string HipBone = "j_kosi";

    /// <summary>
    /// The two thigh roots: not a seed but the rival. Where they outweigh the hip the vertex is on a leg.
    /// See <c>MeshRegionWeights</c>'s losesTo.
    /// </summary>
    internal const string ThighBoneL = "j_asi_a_l", ThighBoneR = "j_asi_a_r";

    /// <summary>
    /// Applies the shell's nipple relax (and the crotch fold) to the BODY the shell is cut from, as a modified
    /// copy of the model; null when nothing qualified. A smoothed shell alone sinks inside the body, so the body
    /// must move too, by the SAME <see cref="BustBridgeSolve"/> on the same coverage so the two surfaces agree.
    /// Gated on coverage; positions and normals are overwritten in place, so no offset moves.
    /// </summary>
    /// <param name="nippleStrength">The nipple relax, on the torso. 0 leaves it alone.</param>
    /// <param name="foldStrength">The crotch fold, on the legs part. 0 leaves it alone.</param>
    internal static byte[]? SmoothBodyNipples(byte[] mdl, SecondSkinLayer gate, float nippleStrength,
                                              Action<string>? log = null, float foldStrength = 0f)
    {
        return new BodySmoothing(mdl, gate, nippleStrength, log, foldStrength).Run();
    }

    /// <summary>Only vertices this close to the smoothed skin are considered at all.</summary>
    private const float PullReach = 0.012f;

    /// <summary>
    /// Smooths the nipple out like a modeller's relax brush: find each nipple by PROMINENCE (height above a
    /// ring at about a nipple's radius, not the forward-most point), then relax a small disc around it in 3-D.
    /// Runs on the body, not the shell. The relax must NOT run to convergence: a converged masked Laplacian is
    /// a harmonic patch, a bowl over a convex breast; hence a fixed <see cref="NipplePasses"/>. The falloff is
    /// applied to the result, not per pass, where it would cancel out.
    /// Returns a per-node 3-D displacement, or null if there was nothing to do.
    /// </summary>
    /// <param name="pos">Node positions to relax: the surface AFTER the span, so the two compose.</param>
    /// <param name="h">Height along the chest axis, for the prominence locator only.</param>
    /// <param name="w">How much each node may be smoothed: the region ramp, gated on this layer's coverage.</param>
    /// <param name="ax">The chest's outward axis, along which the dome floor lifts.</param>
    /// <param name="onBust">
    /// Every node the bust bones reach, WITHOUT the coverage gate. Everything measured reads this (the nipple is
    /// a fact about the body, not the garment); everything written is still gated by <paramref name="w"/>.
    /// </param>
    private static Vec3[]? NippleSmoothTarget(Vec3[] pos, float[] h, float[] lat, float[] ver, float[] w,
                                              bool[] onBust, int count, List<int>[] adj, Vec3 ax,
                                              float strength, Action<string>? log, Vec3[]? nrm = null)
    {
        return new NippleSmoothing(pos, h, lat, ver, w, onBust, count, adj, ax, strength, log, nrm).Run();
    }

    /// <summary>
    /// The ring a node's prominence is measured against, as a fraction of the bust's lateral extent: roughly a
    /// nipple's radius out to twice it. Smaller rings pick up other detail (collarbone, armpit).
    /// </summary>
    private const float NippleRingInner = 0.045f, NippleRingOuter = 0.09f;

    /// <summary>
    /// The relaxed disc's radius, as a fraction of the bust's lateral extent. It sets the displacement's
    /// PROFILE; <see cref="NipplePasses"/> only sets its depth.
    /// </summary>
    private const float NippleDiscRadius = 0.050f;

    /// <summary>
    /// How far each relax pass moves a vertex toward its neighbours' average. Under-relaxed: at 1 the Jacobi
    /// step's checkerboard mode flips sign every pass instead of decaying.
    /// </summary>
    private const float NippleRelaxLambda = 0.5f;

    /// <summary>
    /// How many relax passes. A FIXED count with no stopping rule: a converged masked Laplacian is a bowl. Sets
    /// the depth only, and is large because a Laplacian moves a feature many vertices wide only slowly.
    /// </summary>
    private const int NipplePasses = 1400;

    /// <summary>
    /// Rounds of the finishing plain relax. See the light-relax block in <c>NippleSmoothTarget</c>.
    /// </summary>
    private const int NippleFinishPasses = 6;

    /// <summary>How far each finishing pass moves a vertex toward its neighbours' centroid; under-relaxed
    /// like the main relax.</summary>
    private const float NippleFinishLambda = 0.5f;

    /// <summary>
    /// Where the dome fit samples the breast, as multiples of the relaxed disc's radius. The band starts outside
    /// the nipple's trough and stays tight to keep the fit isotropic; moving it out lowers the tip.
    /// </summary>
    private const float NippleDomeRingInner = 1.6f;

    /// <inheritdoc cref="NippleDomeRingInner"/>
    private const float NippleDomeRingOuter = 2.3f;

    /// <summary>
    /// The fraction of the region's radius pulled fully to the dome before the pull fades out at the edge.
    /// </summary>
    private const float NippleDomeSolid = 0.5f;

    /// <summary>
    /// How far the dome floor reaches beyond the relaxed disc, as a multiple of its radius: it must cover the
    /// nipple's trough. Trades against <see cref="NippleDomeSolid"/>; well past it the annulus leaves the breast.
    /// </summary>
    private const float NippleDomeReach = 2.0f;
}
