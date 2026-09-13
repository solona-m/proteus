using System;
using System.IO;
using System.Runtime.InteropServices;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
using XivLiveMesh;

namespace Proteus.Interop;

/// <summary>What the live-character tools share: finding the player's draw object, a loaded model's bytes, and
/// the racial deformer the player's collection resolves.</summary>
public static unsafe class LiveCharacter
{
    public const string PbdGamePath = "chara/xls/boneDeformer/human.pbd";

    /// <summary>The local player's CharacterBase, or null while there is none to draw on. Main thread only.</summary>
    public static CharacterBase* Player(IObjectTable objects)
    {
        var addr = objects.Length > 0 ? objects[0]?.Address ?? nint.Zero : nint.Zero;
        if (addr == nint.Zero) return null;
        var draw = ((Character*)addr)->GameObject.DrawObject;
        return draw != null && draw->GetObjectType() == ObjectType.CharacterBase ? (CharacterBase*)draw : null;
    }

    /// <summary>A loaded model's file path with Penumbra's <c>|…|</c> prefix removed.</summary>
    public static string FilePath(string resourceName)
    {
        var path = resourceName.Trim();
        if (path.StartsWith('|') && path.IndexOf('|', 1) is var close and > 0) path = path[(close + 1)..];
        return path;
    }

    /// <summary>
    /// A model's bytes: straight out of the loaded resource when the game kept them, otherwise from the file on
    /// disk it was loaded from. Null when neither is available.
    /// </summary>
    public static byte[]? ModelBytes(ModelResourceHandle* handle, string resourceName)
    {
        var res = (ResourceHandle*)handle;
        var data = res->GetData();
        int len = (int)res->GetLength();
        if (data != null && len > 0)
        {
            var bytes = new byte[len];
            Marshal.Copy((nint)data, bytes, 0, len);
            return bytes;
        }
        var path = FilePath(resourceName);
        return Path.IsPathRooted(path) && File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    /// <summary>
    /// The game path a loaded model file stands in for — where the race it was authored for is written. A modded
    /// file's own name need not say; the path it replaces always does.
    /// </summary>
    public static string? GamePathOf(PenumbraBridge penumbra, string resourceName)
    {
        var key = BodyShapeReader.PathKey(resourceName);
        if (key.StartsWith("chara/", StringComparison.Ordinal)) return key;   // not redirected: it IS the game path
        var map = penumbra.GetActivePlayerModelGamePaths();
        return map != null && map.TryGetValue(key, out var gamePath) ? gamePath : null;
    }

    /// <summary>The racial deformer as the player's mods resolve it, or the game's own; null if neither reads.</summary>
    public static PbdFile? LoadPbd(PenumbraBridge penumbra, IDataManager data, IPluginLog log)
    {
        try
        {
            var resolved = penumbra.ResolvePlayer(PbdGamePath);
            byte[]? bytes = null;
            if (resolved != null && Path.IsPathRooted(resolved) && File.Exists(resolved))
                bytes = File.ReadAllBytes(resolved);
            bytes ??= data.GetFile(PbdGamePath)?.Data;
            var pbd = bytes != null ? new PbdFile(bytes) : null;
            log.Information("[Proteus] live character: pbd {0} from {1}", pbd != null ? "loaded" : "missing",
                            resolved ?? PbdGamePath);
            return pbd;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[Proteus] live character: could not read {0}", PbdGamePath);
            return null;
        }
    }
}
