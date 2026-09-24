using System;
using System.Collections.Generic;
using System.Linq;

namespace Proteus.Services;

internal static partial class BodyRetarget
{
    /// <summary>
    /// The IMC variant tags of a body model that its mod's current options do NOT draw.
    /// <para/>
    /// A body mod can ship alternatives of one piece in one model and let an IMC option pick: Neolithe's legs carry the
    /// shins twice, <c>atr_dv_a</c> and a thicker <c>atr_dv_b</c>, 10 mm apart around the top of the calf. The game draws
    /// one. Everything a refit does with the body has to see only that one — the correspondence, the laid skin, the
    /// push-out, the skin swap — or the garment is fitted to a shape nobody sees, and the swap puts both in: the
    /// Comfy Valentione Skirt's socks came out with the thick calf through the cuff.
    /// </summary>
    /// <param name="attributeNames">The model's attribute names, in bit order.</param>
    /// <param name="slot">"_top", "_dwn", "_glv" or "_sho": only that slot's variant tags are judged by its mask.</param>
    /// <param name="mask">The IMC attribute mask the body mod gives the model — see <see cref="ImcEntrySource.MaskFor"/>.</param>
    internal static IReadOnlySet<string> UndrawnVariants(IEnumerable<string> attributeNames, string slot, ushort mask)
    {
        var hidden = new HashSet<string>(StringComparer.Ordinal);
        if (VariantLetter(slot) is not { } letter) return hidden;
        foreach (string name in attributeNames)
        {
            if (!SecondSkinWriter.IsVariantAttribute(name) || name[4] != letter) continue;
            if ((mask & (1 << (name[7] - 'a'))) == 0) hidden.Add(name);
        }
        return hidden;
    }

    /// <summary>The model with every part tagged with a <paramref name="hidden"/> attribute taken out.</summary>
    internal static ModelParts Without(ModelParts model, IReadOnlySet<string>? hidden)
    {
        if (hidden is not { Count: > 0 }) return model;
        uint bits = 0;
        for (int i = 0; i < model.AttributeNames.Count && i < 32; i++)
            if (hidden.Contains(model.AttributeNames[i])) bits |= 1u << i;
        if (bits == 0) return model;

        return new ModelParts
        {
            Positions = model.Positions,
            Normals = model.Normals,
            MeshSpans = model.MeshSpans,
            Parts = model.Parts.Where(p => (p.AttributeMask & bits) == 0).ToList(),
            AttributeNames = model.AttributeNames,
            Min = model.Min,
            Max = model.Max,
            ShatteredSubmeshes = model.ShatteredSubmeshes,
        };
    }

    /// <summary>The letter an IMC variant tag names its slot by (<c>atr_dv_a</c> is the legs'), or null.</summary>
    private static char? VariantLetter(string slot) => slot switch
    {
        "_top" => 't',
        "_dwn" => 'd',
        "_glv" => 'g',
        "_sho" => 's',
        _      => null,
    };

    /// <summary>The equipment slot an IMC group names for a body slot ("Legs" for "_dwn"), or null.</summary>
    internal static string? ImcSlotName(string slot) => slot switch
    {
        "_top" => "Body",
        "_dwn" => "Legs",
        "_glv" => "Hands",
        "_sho" => "Feet",
        _      => null,
    };
}
