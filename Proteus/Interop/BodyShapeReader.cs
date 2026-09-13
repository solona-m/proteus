using System;
using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;

namespace Proteus.Interop;

/// <summary>
/// Reads which shape keys ("shapes"/morphs) the game currently has ENABLED on each of the local player's
/// drawn body models — the state that a body option like "Remove Hip Dips" toggles. The second-skin shell
/// is cut from the body's BASE geometry with shapes dropped (see SecondSkinWriter), so when a shape is
/// enabled the body deforms but the shell does not, and it diverges. To bake the morph into the shell we
/// first need to know, per body model, which shapes are on — and that lives only in live render state
/// (<c>Render.Model.EnabledShapeKeyIndexMask</c>), not in the .mdl file.
/// <para/>
/// The same sets also carry the model's switched-OFF variant attributes, prefixed with
/// <see cref="HiddenAttributePrefix"/> — see <see cref="ReadEnabledShapes"/>.
///
/// MUST be called on the framework thread (it walks live game objects).
/// </summary>
public static unsafe class BodyShapeReader
{
    /// <summary>
    /// Marks an entry in a model's set as an attribute the game is NOT drawing, rather than a shape it is.
    /// No shape or attribute name can start with it, so the two never collide.
    /// </summary>
    public const string HiddenAttributePrefix = "!";

    /// <summary>
    /// Map of each drawn model's file-name STEM (e.g. <c>c0201e0000_dwn</c>) to the set of shape-key names
    /// currently enabled on it. Empty when the player isn't drawable this frame. Keyed by stem, not full
    /// path, so a shell part matches whether the live resource reports a disk path or a game path — both
    /// end in the same <c>c{race}{slot}.mdl</c>. Scoped per model so only the shapes enabled on THAT body
    /// are baked, never a connector shape enabled on some other model.
    /// <para/>
    /// Also in each set, prefixed with <see cref="HiddenAttributePrefix"/>: the model's IMC variant attributes
    /// (<c>atr_dv_b</c>) the game has switched OFF. A body can ship two versions of one region and let an IMC
    /// option pick — Neolithe's legs carry a thin calf tagged <c>atr_dv_a</c> and a thicker one tagged
    /// <c>atr_dv_b</c>, and its "SHINS: Thicker" option flips bit a off and bit b on. The game draws one;
    /// cut from the file alone the shell draws both, which on a sheer overlay is a doubled stocking.
    /// <para/>
    /// Carried in the shape sets rather than beside them because it is the same kind of fact — what the game
    /// has toggled on this drawn model — and it has to reach every place the shapes already do: the settle
    /// loop's stability check and the composite fingerprint both hash these sets, so flipping the option
    /// recomposites exactly as toggling a shape key does.
    /// <para/>
    /// Variant attributes only. The body-suppression attributes (<c>atr_sne</c>, <c>atr_hij</c>) are driven by
    /// what gear is worn, and whether the shell should follow those is a separate question.
    /// </summary>
    public static Dictionary<string, HashSet<string>> ReadEnabledShapes(nint playerAddr)
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        if (playerAddr == 0) return result;

        var chara = (Character*)playerAddr;
        var draw  = chara->GameObject.DrawObject;
        if (draw == null || draw->GetObjectType() != ObjectType.CharacterBase) return result;

        var cb = (CharacterBase*)draw;
        foreach (var modelPtr in cb->ModelsSpan)
        {
            var model = modelPtr.Value;
            if (model == null) continue;

            var handle = model->ModelResourceHandle;
            if (handle == null) continue;

            var name = handle->FileName.ToString();
            if (string.IsNullOrEmpty(name)) continue;

            // ModelResourceHandle.Shapes maps shape NAME -> its index; a bit set in the mask means that
            // index's shape is enabled. Collect the names of the set bits.
            var enabled = new HashSet<string>(StringComparer.Ordinal);
            uint mask = model->EnabledShapeKeyIndexMask;
            if (mask != 0)
                foreach (var kv in handle->Shapes)
                {
                    int idx = kv.Item2;
                    if (idx >= 0 && idx < 32 && (mask & (1u << idx)) != 0)
                    {
                        var shapeName = kv.Item1.ToString();
                        if (!string.IsNullOrEmpty(shapeName)) enabled.Add(shapeName);
                    }
                }

            // Attributes the same way, inverted: the names whose bit is CLEAR.
            uint attrMask = model->EnabledAttributeIndexMask;
            foreach (var kv in handle->Attributes)
            {
                int idx = kv.Item2;
                if (idx < 0 || idx >= 32 || (attrMask & (1u << idx)) != 0) continue;
                var attrName = kv.Item1.ToString();
                if (IsVariantAttribute(attrName)) enabled.Add(HiddenAttributePrefix + attrName);
            }

            if (enabled.Count > 0)
                result[Stem(name)] = enabled;
        }

        return result;
    }

    /// <summary>
    /// <c>atr_</c>, a slot letter, <c>v_</c>, then a part letter a–j: the names an IMC attribute mask
    /// switches (see <c>MeshToggleService.AttributeSlotLetter</c>).
    /// </summary>
    public static bool IsVariantAttribute(string? name)
        => name is { Length: 8 } n
           && n.StartsWith("atr_", StringComparison.Ordinal)
           && n[5] == 'v' && n[6] == '_'
           && n[7] is >= 'a' and <= 'j';

    /// <summary>
    /// Split one model's set back into what it carries: the enabled shape keys, and the variant attributes
    /// the game is not drawing. Either comes back null when empty, which is what the writer takes as "none".
    /// </summary>
    public static (HashSet<string>? Shapes, HashSet<string>? HiddenAttributes) Split(HashSet<string>? set)
    {
        if (set == null || set.Count == 0) return (null, null);
        HashSet<string>? shapes = null, hidden = null;
        foreach (var s in set)
        {
            if (s.StartsWith(HiddenAttributePrefix, StringComparison.Ordinal))
                (hidden ??= new HashSet<string>(StringComparer.Ordinal)).Add(s[HiddenAttributePrefix.Length..]);
            else
                (shapes ??= new HashSet<string>(StringComparer.Ordinal)).Add(s);
        }
        return (shapes, hidden);
    }

    /// <summary>File-name stem, lower-cased: "…/c0201e0000_dwn.mdl" → "c0201e0000_dwn". Slot-unique per part.</summary>
    public static string Stem(string path)
    {
        var s = path.Trim().Trim('"').Replace('\\', '/');
        int slash = s.LastIndexOf('/');
        if (slash >= 0) s = s[(slash + 1)..];
        int dot = s.LastIndexOf('.');
        if (dot >= 0) s = s[..dot];
        return s.ToLowerInvariant();
    }
}
