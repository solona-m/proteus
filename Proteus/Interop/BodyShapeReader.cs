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
///
/// MUST be called on the framework thread (it walks live game objects).
/// </summary>
public static unsafe class BodyShapeReader
{
    /// <summary>
    /// Map of each drawn model's file-name STEM (e.g. <c>c0201e0000_dwn</c>) to the set of shape-key names
    /// currently enabled on it. Empty when the player isn't drawable this frame. Keyed by stem, not full
    /// path, so a shell part matches whether the live resource reports a disk path or a game path — both
    /// end in the same <c>c{race}{slot}.mdl</c>. Scoped per model so only the shapes enabled on THAT body
    /// are baked, never a connector shape enabled on some other model.
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

            uint mask = model->EnabledShapeKeyIndexMask;
            if (mask == 0) continue;                       // nothing enabled — skip

            var handle = model->ModelResourceHandle;
            if (handle == null) continue;

            var name = handle->FileName.ToString();
            if (string.IsNullOrEmpty(name)) continue;

            // ModelResourceHandle.Shapes maps shape NAME -> its index; a bit set in the mask means that
            // index's shape is enabled. Collect the names of the set bits.
            var enabled = new HashSet<string>(StringComparer.Ordinal);
            foreach (var kv in handle->Shapes)
            {
                int idx = kv.Item2;
                if (idx >= 0 && idx < 32 && (mask & (1u << idx)) != 0)
                {
                    var shapeName = kv.Item1.ToString();
                    if (!string.IsNullOrEmpty(shapeName)) enabled.Add(shapeName);
                }
            }

            if (enabled.Count > 0)
                result[Stem(name)] = enabled;
        }

        return result;
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
