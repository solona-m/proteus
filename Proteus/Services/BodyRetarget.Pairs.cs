using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Proteus.Localization;

namespace Proteus.Services;

internal static partial class BodyRetarget
{
    /// <summary>
    /// Read a source and target body and work out which point of one is which point of the other — vertex for vertex
    /// when they are the same mesh, by texture coordinate otherwise (see <see cref="BodyCorrespondence"/>). Null on
    /// success; otherwise the reason, worded for the user.
    /// <para/>
    /// Shared by the Studio panel and the headless refit (Proteus.Refit), so a size generated for a packed mod is the
    /// same refit the panel would have made.
    /// </summary>
    /// <param name="name">The slot's name, for the refusal.</param>
    /// <param name="male">The garment is a man's, and so both bodies are.</param>
    /// <param name="sourceMask">What the source body mod draws of <paramref name="slot"/> (see
    /// <see cref="RefitCore.MaskOf"/>): the body is read without the IMC variant parts its mod does not draw — its
    /// <c>Atr</c> switches are not applied, since which alternative the author made the garment on the reader's options
    /// cannot say. Null when every part is drawn.</param>
    /// <param name="targetMask">What the target body mod draws: read without its undrawn IMC variants and the
    /// alternatives its <c>Atr</c> options switch off (see <see cref="BodyDraw.Hidden"/>), the size refitted onto taken as
    /// chosen by the caller (<see cref="BodyDraw.Onto"/>).</param>
    /// <param name="uvRemap">Only needed when the two bodies use different texture layouts.</param>
    /// <param name="targetMale">The target body's sex when it is not the garment's: a refit for the other sex (see
    /// <see cref="CrossSexRefit"/>). Null reads both as <paramref name="male"/>.</param>
    internal static string? BuildPair(string slot, string sourcePath, string targetPath, string name, bool male,
                                      BodyDraw? sourceMask, BodyDraw? targetMask, UVRemapService? uvRemap,
                                      out SlotPair pair, bool? targetMale = null)
    {
        return BuildPair(slot, File.ReadAllBytes(sourcePath), File.ReadAllBytes(targetPath), name, male, sourceMask,
                         targetMask, uvRemap, out pair, BodyMaterialReader(targetPath), targetMale);
    }

    /// <inheritdoc cref="BuildPair(string, string, string, string, bool, BodyDraw?, BodyDraw?, UVRemapService?, out SlotPair, bool?)"/>
    /// <param name="targetMaterial">Reads the target body mod's materials (see <see cref="SlotPair.TargetMaterial"/>);
    /// null when there is no mod to read them from.</param>
    internal static string? BuildPair(string slot, byte[] sourceBytes, byte[] targetBytes, string name, bool male,
                                      BodyDraw? sourceMask, BodyDraw? targetMask, UVRemapService? uvRemap,
                                      out SlotPair pair, Func<string, byte[]?>? targetMaterial = null,
                                      bool? targetMale = null)
        => BuildPair(slot, sourceBytes, targetBytes, name, male, sourceMask, targetMask, uvRemap, out pair, out _,
                     targetMaterial, targetMale);

    /// <inheritdoc cref="BuildPair(string, byte[], byte[], string, bool, BodyDraw?, BodyDraw?, UVRemapService?, out SlotPair, Func{string, byte[]}?, bool?)"/>
    /// <param name="stats">How the two bodies' skin landed on each other; null for the same mesh. On a refusal, set only
    /// across the sexes and only when it was for too little coverage; null for every other refusal.</param>
    internal static string? BuildPair(string slot, byte[] sourceBytes, byte[] targetBytes, string name, bool male,
                                      BodyDraw? sourceMask, BodyDraw? targetMask, UVRemapService? uvRemap,
                                      out SlotPair pair, out UvAtlasCorrespondence.Stats? stats,
                                      Func<string, byte[]?>? targetMaterial = null, bool? targetMale = null)
    {
        stats = null;
        pair = default;

        var source = ModelPartReader.Read(sourceBytes);
        var target = ModelPartReader.Read(targetBytes);
        if (source == null || target == null) return string.Format(Strings.Parts.RetargetUnreadableFmt, name);
        // The source as today, every alternative of its options included: which one the author made the garment on,
        // the reader's choice of the body mod's options cannot say. The target as the options draw it.
        source = Drawn(source, slot, sourceMask, atr: false, out _, out _);
        target = Drawn(target, slot, targetMask, atr: true, out var targetHidden, out var targetSwitched);
        // Its options draw none of its skin. Asked only when they hid something: every other body goes on as before.
        if (targetMask is { AtrOff.Count: > 0 } && targetHidden?.Overlaps(targetMask.AtrOff) == true
            && !target.Parts.Any(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material)))
            return string.Format(Strings.Parts.RetargetUnreadableFmt, name);

        if (!BodyCorrespondence.TryBuild(source, Uv(sourceBytes), target, Uv(targetBytes), name,
                                         out var correspondence, out string refusal, out stats, uvRemap, male, targetMale))
            return refusal;

        pair = new SlotPair(slot, correspondence!, target, targetBytes, sourceBytes, targetHidden,
                            TargetMaterial: targetMaterial,
                            SourceIsGameChest: slot == "_top" && VanillaBodyCatalog.IsGameModel(sourceBytes, slot),
                            TargetSwitched: targetSwitched);
        if (slot == "_dwn" && Bulge.TbseBulge.Shared is { } bulge)
            pair = pair with { Featureless = FeaturelessFactory(pair, source, sourceBytes, targetBytes, targetHidden, name, uvRemap,
                                                               male, targetMale, bulge) };
        return null;
    }

    /// <summary>
    /// A body model without the parts its mod does not draw, the tags of those parts, and the drawn tags its options
    /// switch. With <paramref name="atr"/> off only the IMC variants are judged, as before custom attributes were read.
    /// </summary>
    private static ModelParts Drawn(ModelParts body, string slot, BodyDraw? draw, bool atr,
                                    out IReadOnlySet<string>? hidden, out IReadOnlySet<string>? switched)
    {
        hidden = draw?.Hidden(body.AttributeNames, slot, atr);
        // The undrawn IMC variants alone, as an empty set when there are none: what TargetHidden always held.
        if (draw is { ImcMask: null } && hidden is { Count: 0 }) hidden = null;
        switched = atr ? draw?.Switched(body.AttributeNames, hidden ?? new HashSet<string>()) : null;
        if (switched is { Count: 0 }) switched = null;
        return Without(body, hidden);
    }

    internal static float[] Uv(byte[] mdl)
        => SecondSkinWriter.TryReadLod0Geometry(mdl, out _, out var uv, out _, out _, out _, false, false, null)
            ? uv
            : [];

    /// <summary>
    /// The pair onto the new body made featureless (plan featureless-bulge, path A), built on first call and kept: the
    /// body's genital piece rewritten to the owner's bulge (<see cref="Bulge.BulgeSwap"/>), read with the same options'
    /// mask, and the correspondence built again onto it — the bulge's landing uvs standing in for its own in the
    /// correspondence only, so a garment's crotch lands on it where the transfer maps send it. Never throws: a body it
    /// cannot be made for says why.
    /// </summary>
    private static Func<(SlotPair? Pair, string Why)> FeaturelessFactory(SlotPair plain, ModelParts source, byte[] sourceBytes,
        byte[] targetBytes, IReadOnlySet<string>? targetHidden, string name, UVRemapService? uvRemap, bool male, bool? targetMale,
        Bulge.TbseBulge bulge)
    {
        var made = new Lazy<(SlotPair?, string)>(() =>
        {
            try
            {
                var swapped = Bulge.BulgeSwap.Featureless(targetBytes, bulge, out string why);
                if (swapped == null) return (null, why);
                var parts = ModelPartReader.Read(swapped.Model);
                if (parts == null) return (null, "the featureless body could not be read back");
                var drawn = Without(parts, targetHidden);
                var uv = Uv(swapped.Model);
                for (int i = 0; i < swapped.SlotOf.Length; i++)
                    if (swapped.Land[i] is { } l && swapped.SlotOf[i] * 2 + 1 < uv.Length)
                    {
                        uv[swapped.SlotOf[i] * 2] = l[0];
                        uv[swapped.SlotOf[i] * 2 + 1] = l[1];
                    }
                if (!BodyCorrespondence.TryBuild(source, Uv(sourceBytes), drawn, uv, name, out var correspondence,
                                                 out string refusal, out _, uvRemap, male, targetMale))
                    return (null, "the featureless body was refused: " + refusal);
                // A source of the same mesh would land vertex for vertex: its genitals onto bulge slots in whatever order
                // they were written. Only a landing by uv knows where the bulge is.
                if (correspondence is IdentityCorrespondence)
                    return (null, "the garment's body is the same mesh as the new body: no landing onto the bulge");
                // A re-stitched bulge says so (FitBulges logs a non-empty reason beside its featureless line).
                return (plain with { Correspondence = correspondence!, Target = drawn, TargetModel = swapped.Model, Featureless = null },
                        swapped.Restitched ?? "");
            }
            catch (Exception e)
            {
                return (null, "failed: " + e.Message);
            }
        }, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);
        return () => made.Value;
    }
}
