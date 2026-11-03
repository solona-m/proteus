using System;
using System.Linq;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>Checks on a saved model's material table.</summary>
internal static class MaterialAssert
{
    /// <summary>
    /// The saved model names no material that none of its meshes draws, unless the author's model already named it
    /// undrawn. A name the game cannot find hides the model (build 1397's invisible men's legs), so a refit must never
    /// bring one in — Flaccid M A's piercing material only with a kept mesh that draws it (tbsex-nude-legs, EDGE-11).
    /// </summary>
    internal static void NoNewUndrawnNames(byte[] saved, byte[] authored)
    {
        static (string[] Named, string[] Drawn) Read(byte[] model)
            => (SecondSkinWriter.Parse(model).MatNames.ToArray(),
                (ModelPartReader.Read(model)?.Parts ?? []).Select(p => p.Material).Distinct(StringComparer.Ordinal).ToArray());
        var (named, drawn) = Read(saved);
        var (authorNamed, authorDrawn) = Read(authored);
        var authorUndrawn = authorNamed.Except(authorDrawn, StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        var bad = named.Except(drawn, StringComparer.Ordinal).Where(n => !authorUndrawn.Contains(n)).ToList();
        Assert.True(bad.Count == 0, "named but drawn by no mesh: " + string.Join(", ", bad));
    }
}
