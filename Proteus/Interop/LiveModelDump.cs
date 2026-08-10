using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;

namespace Proteus.Interop;

/// <summary>
/// What the RENDERER has, as opposed to what is on disk.
/// <para/>
/// Every diagnostic in this project reads the .mdl Proteus writes. That answers "did we build the right
/// model", never "is the game drawing the model we built" — and those came apart: a shell that measured
/// clean and imported into a modelling package correctly still had triangles missing in game. Walking
/// the live CharacterBase closes that gap: it reports the file each drawn model was actually loaded
/// from, so a stale redirect, a cached resource or a model coming from somewhere else entirely stops
/// being a guess.
/// </summary>
public sealed unsafe class LiveModelDump(IObjectTable objects, IPluginLog log)
{
    /// <summary>
    /// Report every model on the local player: which file it came from, and how many meshes and
    /// materials it has. Returns the lines so a caller can print them wherever it likes.
    /// </summary>
    public List<string> DescribeLocalPlayer()
    {
        var outp = new List<string>();
        var addr = objects.Length > 0 ? objects[0]?.Address ?? nint.Zero : nint.Zero;
        if (addr == nint.Zero) { outp.Add("no local player"); return outp; }

        var chara = (Character*)addr;
        var draw = chara->GameObject.DrawObject;
        if (draw == null || draw->GetObjectType() != ObjectType.CharacterBase)
        {
            outp.Add("local player has no CharacterBase draw object (mid-redraw?)");
            return outp;
        }

        var cb = (CharacterBase*)draw;
        int i = -1;
        foreach (var modelPtr in cb->ModelsSpan)
        {
            i++;
            var model = modelPtr.Value;
            if (model == null) continue;
            var handle = model->ModelResourceHandle;
            if (handle == null) { outp.Add($"slot {i,2}: model with no resource handle"); continue; }

            var name = handle->FileName.ToString();
            outp.Add($"slot {i,2}: {name}");
        }
        if (outp.Count == 0) outp.Add("no models on the draw object");
        return outp;
    }

    /// <summary>
    /// Write the raw bytes of every loaded model whose path matches <paramref name="filter"/> to
    /// <paramref name="dir"/>, straight out of the resource the renderer is using. Byte-comparing that
    /// against what Proteus wrote is the only way to prove the game is using it.
    /// </summary>
    public List<string> DumpMatching(string filter, string dir)
    {
        var outp = new List<string>();
        try { Directory.CreateDirectory(dir); }
        catch (Exception ex) { outp.Add($"cannot write to {dir}: {ex.Message}"); return outp; }

        var addr = objects.Length > 0 ? objects[0]?.Address ?? nint.Zero : nint.Zero;
        if (addr == nint.Zero) { outp.Add("no local player"); return outp; }
        var chara = (Character*)addr;
        var draw = chara->GameObject.DrawObject;
        if (draw == null || draw->GetObjectType() != ObjectType.CharacterBase)
        { outp.Add("no CharacterBase"); return outp; }

        var cb = (CharacterBase*)draw;
        int n = 0, i = -1;
        foreach (var modelPtr in cb->ModelsSpan)
        {
            i++;
            var model = modelPtr.Value;
            if (model == null) continue;
            var handle = model->ModelResourceHandle;
            if (handle == null) continue;
            var name = handle->FileName.ToString();
            if (string.IsNullOrEmpty(name)) continue;
            if (filter.Length > 0 && name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;

            // The resource keeps the file it was loaded from; length and pointer live on the handle's
            // ResourceHandle base.
            var res = (FFXIVClientStructs.FFXIV.Client.System.Resource.Handle.ResourceHandle*)handle;
            var data = res->GetData();
            var len = (int)res->GetLength();
            if (data == null || len <= 0)
            { outp.Add($"slot {i,2}: {name} — resource has no readable bytes"); continue; }

            var bytes = new byte[len];
            System.Runtime.InteropServices.Marshal.Copy((nint)data, bytes, 0, len);
            var safe = new StringBuilder();
            foreach (var c in Path.GetFileName(name)) safe.Append(char.IsLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '_');
            var path = Path.Combine(dir, $"live_{i}_{safe}");
            File.WriteAllBytes(path, bytes);
            outp.Add($"slot {i,2}: {name}  ->  {path}  ({len} bytes)");
            n++;
        }
        if (n == 0) outp.Add($"no loaded model matched '{filter}'");
        return outp;
    }
}
