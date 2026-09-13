using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;

namespace Proteus.Interop;

/// <summary>
/// The item variant each of the local player's drawn gear slots was built with, read off the draw object.
/// <para/>
/// A second-skin host references its appended material variant-relatively, and the folder the game builds
/// that path under comes from this variant (through the item's IMC entry). The live material walk answers
/// the same question more directly, but only once the item's materials have loaded — and they load after
/// the redraw that swaps the item in, so for a freshly equipped accessory that walk has nothing to say. This
/// is on the draw object from the moment the slot is set.
///
/// MUST be called on the framework thread (it walks live game objects).
/// </summary>
public static unsafe class EquippedSlotVariants
{
    /// <summary>One drawn slot: the host-slot suffix it loads a model under, its set id and item variant.</summary>
    public readonly record struct Slot(string Suffix, int SetId, int Variant);

    /// <summary>
    /// <see cref="Human.EquipmentModels"/> order — Head, Top, Arms, Legs, Feet, Ear, Neck, Wrist, RFinger,
    /// LFinger — as the model-path suffix each loads under.
    /// </summary>
    private static readonly string[] EquipmentSuffixes =
        ["met", "top", "glv", "dwn", "sho", "ear", "nek", "wrs", "rir", "ril"];

    /// <summary>
    /// Every occupied slot. Empty when the player is not drawable this frame. Facewear is reported under
    /// "met" beside a hat, because both load a <c>_met</c> model — a caller tells them apart by set id.
    /// </summary>
    public static List<Slot> Read(nint playerAddr)
    {
        var result = new List<Slot>();
        if (playerAddr == 0) return result;

        var chara = (Character*)playerAddr;
        var draw  = chara->GameObject.DrawObject;
        if (draw == null || draw->GetObjectType() != ObjectType.CharacterBase) return result;

        var cb = (CharacterBase*)draw;
        if (cb->GetModelType() != CharacterBase.ModelType.Human) return result;

        var human = (Human*)cb;
        var equipment = human->EquipmentModels;
        for (int i = 0; i < equipment.Length && i < EquipmentSuffixes.Length; i++)
            Add(result, EquipmentSuffixes[i], equipment[i]);

        foreach (var glasses in human->GlassesModels)
            Add(result, "met", glasses);

        return result;
    }

    private static void Add(List<Slot> into, string suffix, in EquipmentModelId id)
    {
        // Set 0 is an empty slot; the game draws its bare-body stand-in, which no host is ever built on.
        if (id.Id == 0) return;
        into.Add(new Slot(suffix, id.Id, id.Variant));
    }
}
