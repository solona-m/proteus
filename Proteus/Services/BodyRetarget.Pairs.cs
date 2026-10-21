using System;
using System.Collections.Generic;
using System.IO;
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
    /// <param name="sourceMask">The source body mod's IMC mask for <paramref name="slot"/> (see
    /// <see cref="ImcEntrySource.MaskFor"/>): the body is read without the variant parts its mod does not draw. Null
    /// when every part is drawn.</param>
    /// <param name="targetMask">The same, for the target body.</param>
    /// <param name="uvRemap">Only needed when the two bodies use different texture layouts.</param>
    /// <param name="targetMale">The target body's sex when it is not the garment's: a refit for the other sex (see
    /// <see cref="CrossSexRefit"/>). Null reads both as <paramref name="male"/>.</param>
    internal static string? BuildPair(string slot, string sourcePath, string targetPath, string name, bool male,
                                      ushort? sourceMask, ushort? targetMask, UVRemapService? uvRemap,
                                      out SlotPair pair, bool? targetMale = null)
    {
        return BuildPair(slot, File.ReadAllBytes(sourcePath), File.ReadAllBytes(targetPath), name, male, sourceMask,
                         targetMask, uvRemap, out pair, BodyMaterialReader(targetPath), targetMale);
    }

    /// <inheritdoc cref="BuildPair(string, string, string, string, bool, ushort?, ushort?, UVRemapService?, out SlotPair)"/>
    /// <param name="targetMaterial">Reads the target body mod's materials (see <see cref="SlotPair.TargetMaterial"/>);
    /// null when there is no mod to read them from.</param>
    internal static string? BuildPair(string slot, byte[] sourceBytes, byte[] targetBytes, string name, bool male,
                                      ushort? sourceMask, ushort? targetMask, UVRemapService? uvRemap,
                                      out SlotPair pair, Func<string, byte[]?>? targetMaterial = null,
                                      bool? targetMale = null)
        => BuildPair(slot, sourceBytes, targetBytes, name, male, sourceMask, targetMask, uvRemap, out pair, out _,
                     targetMaterial, targetMale);

    /// <inheritdoc cref="BuildPair(string, byte[], byte[], string, bool, ushort?, ushort?, UVRemapService?, out SlotPair, Func{string, byte[]}?, bool?)"/>
    /// <param name="stats">How the two bodies' skin landed on each other; null for the same mesh.</param>
    internal static string? BuildPair(string slot, byte[] sourceBytes, byte[] targetBytes, string name, bool male,
                                      ushort? sourceMask, ushort? targetMask, UVRemapService? uvRemap,
                                      out SlotPair pair, out UvAtlasCorrespondence.Stats? stats,
                                      Func<string, byte[]?>? targetMaterial = null, bool? targetMale = null)
    {
        stats = null;
        pair = default;

        var source = ModelPartReader.Read(sourceBytes);
        var target = ModelPartReader.Read(targetBytes);
        if (source == null || target == null) return string.Format(Strings.Parts.RetargetUnreadableFmt, name);
        source = Drawn(source, slot, sourceMask, out _);
        target = Drawn(target, slot, targetMask, out var targetHidden);

        if (!BodyCorrespondence.TryBuild(source, Uv(sourceBytes), target, Uv(targetBytes), name,
                                         out var correspondence, out string refusal, out stats, uvRemap, male, targetMale))
            return refusal;

        pair = new SlotPair(slot, correspondence!, target, targetBytes, sourceBytes, targetHidden,
                            TargetMaterial: targetMaterial);
        return null;
    }

    /// <summary>A body model without the variant parts its mod does not draw, and the tags of those parts.</summary>
    private static ModelParts Drawn(ModelParts body, string slot, ushort? mask, out IReadOnlySet<string>? hidden)
    {
        hidden = mask is { } m ? UndrawnVariants(body.AttributeNames, slot, m) : null;
        return Without(body, hidden);
    }

    private static float[] Uv(byte[] mdl)
        => SecondSkinWriter.TryReadLod0Geometry(mdl, out _, out var uv, out _, out _, out _, false, false, null)
            ? uv
            : [];
}
