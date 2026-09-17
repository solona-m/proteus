using System;
using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;

namespace Proteus.Interop;

/// <summary>
/// Reads which shape keys the game currently has ENABLED on each of the local player's drawn models, so the
/// second skin (cut from base geometry) can bake them. That lives only in live render state
/// (<c>Render.Model.EnabledShapeKeyIndexMask</c>), not the .mdl. The sets also carry switched-off variant
/// attributes; see <see cref="ReadEnabledShapes"/>.
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
    /// Map of each drawn model to the set of shape-key names enabled on it; empty when the player isn't
    /// drawable. Keyed by full path (<see cref="PathKey"/>), and by file-name stem (<see cref="Stem"/>) only where
    /// no two drawn models share that stem, so a path miss never lands on another model's set.
    /// <para/>
    /// Each set also holds, prefixed with <see cref="HiddenAttributePrefix"/>, the IMC variant attributes
    /// (<c>atr_dv_*</c>) the game has switched OFF, so the shell drops the variant the game does not draw. They
    /// ride in the shape sets so the settle check and composite fingerprint see them. Suppression attributes
    /// (<c>atr_sne</c>, <c>atr_hij</c>) are not included.
    /// </summary>
    public static Dictionary<string, HashSet<string>> ReadEnabledShapes(nint playerAddr)
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        if (playerAddr == 0) return result;

        var chara = (Character*)playerAddr;
        var draw  = chara->GameObject.DrawObject;
        if (draw == null || draw->GetObjectType() != ObjectType.CharacterBase) return result;

        var cb = (CharacterBase*)draw;
        // Every drawn model's stem and path, including models with nothing enabled, to detect ambiguous stems.
        var stemOwner = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var ambiguous = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var byStem = new List<(string Stem, HashSet<string> Set)>();
        foreach (var modelPtr in cb->ModelsSpan)
        {
            var model = modelPtr.Value;
            if (model == null) continue;

            var handle = model->ModelResourceHandle;
            if (handle == null) continue;

            var name = handle->FileName.ToString();
            if (string.IsNullOrEmpty(name)) continue;

            string full = PathKey(name), stem = Stem(name);
            if (!stemOwner.TryAdd(stem, full)
                && !string.Equals(stemOwner[stem], full, StringComparison.OrdinalIgnoreCase))
                ambiguous.Add(stem);

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
                if (Services.SecondSkinWriter.IsVariantAttribute(attrName))
                    enabled.Add(HiddenAttributePrefix + attrName);
            }

            if (enabled.Count > 0)
            {
                result[full] = enabled;
                byStem.Add((stem, enabled));
            }
        }

        // A path always contains a separator and a stem never does, so the two kinds of key cannot collide.
        foreach (var (stem, set) in byStem)
            if (!ambiguous.Contains(stem)) result.TryAdd(stem, set);

        return result;
    }

    /// <summary>
    /// A model path as a lookup key: lower-cased, forward slashes, and without Penumbra's <c>|…|</c> redirect
    /// prefix, so live and resolved paths match.
    /// </summary>
    public static string PathKey(string path)
    {
        var s = path.Trim().Trim('"');
        if (s.StartsWith('|') && s.IndexOf('|', 1) is var close and > 0) s = s[(close + 1)..];
        return s.Replace('\\', '/').ToLowerInvariant();
    }

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
