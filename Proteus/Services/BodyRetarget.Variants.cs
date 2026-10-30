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
    /// <para/>
    /// A body mod can do the same with custom attributes its options switch (<see cref="BodyAtrSwitches"/>); those join
    /// these in what a refit leaves out of the new body (<see cref="BodyDraw.Hidden"/>).
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

    /// <summary>
    /// What of a body model its mod draws, for one slot under one choice of its options: the IMC variant mask (see
    /// <see cref="UndrawnVariants"/>) and the custom attributes its <c>Atr</c> options switch (see
    /// <see cref="BodyAtrSwitches"/>). Read once per slot; <see cref="Onto"/> takes the size being refitted onto as
    /// chosen, from the manifest already read. Equal by value — the mask and the names — as the bare mask it replaces was.
    /// </summary>
    /// <param name="ImcMask">The IMC attribute mask (<see cref="ImcEntrySource.MaskFor"/>); null when the mod gives none.</param>
    /// <param name="AtrOff">The custom attributes switched off.</param>
    /// <param name="AtrSwitched">Every custom attribute the mod switches on a body slot (<see cref="BodyAtrSwitches.Result"/>).</param>
    /// <param name="Atr">The manifest and choice the two sets were read under, for <see cref="Onto"/>; not compared.</param>
    internal sealed record BodyDraw(ushort? ImcMask, IReadOnlySet<string> AtrOff, IReadOnlySet<string> AtrSwitched,
                                    BodyAtrSwitches.Query? Atr = null)
    {
        public bool Equals(BodyDraw? other)
            => other is not null && ImcMask == other.ImcMask && AtrOff.SetEquals(other.AtrOff)
               && AtrSwitched.SetEquals(other.AtrSwitched);

        public override int GetHashCode()
        {
            // Order-independent over each set: a set's own order is not its value.
            int Of(IReadOnlySet<string> names) => names.Aggregate(0, (h, n) => h ^ StringComparer.Ordinal.GetHashCode(n));
            return HashCode.Combine(ImcMask, Of(AtrOff), Of(AtrSwitched));
        }

        /// <summary>The same body with <paramref name="onto"/> — the size being refitted onto — taken as chosen in its own
        /// group, for the <c>Atr</c> switches only; the IMC mask is the slot's. Reads no file.</summary>
        internal BodyDraw Onto(BodyOption? onto)
        {
            if (onto == null || Atr == null) return this;
            var result = BodyAtrSwitches.Resolve(Atr, onto);
            return this with
            {
                AtrOff = result?.Off ?? new HashSet<string>(StringComparer.Ordinal),
                AtrSwitched = result?.Switched ?? new HashSet<string>(StringComparer.Ordinal),
            };
        }

        /// <summary>The attributes of a model with <paramref name="names"/> whose parts are not drawn: its undrawn IMC
        /// variants, and with <paramref name="atr"/> those its <c>Atr</c> options switch off.</summary>
        internal IReadOnlySet<string> Hidden(IReadOnlyList<string> names, string slot, bool atr)
        {
            var hidden = new HashSet<string>(ImcMask is { } mask ? UndrawnVariants(names, slot, mask) : [],
                                             StringComparer.Ordinal);
            if (atr) hidden.UnionWith(names.Where(AtrOff.Contains));
            return hidden;
        }

        /// <summary>The drawn attributes of a model with <paramref name="names"/> that the mod switches: the tags dropped
        /// from skin a refit brings in (see <see cref="SlotPair.TargetSwitched"/>).</summary>
        internal IReadOnlySet<string> Switched(IReadOnlyList<string> names, IReadOnlySet<string> hidden)
            => names.Where(n => AtrSwitched.Contains(n) && !hidden.Contains(n)).ToHashSet(StringComparer.Ordinal);
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
