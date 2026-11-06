using System;
using System.Collections.Generic;
using Proteus.Services;

namespace Proteus.Gui;

/// <summary>Index-texture scans for the Colors window, each checked against its file's stamp on lookup, so an
/// <c>_id</c> edited on disk is rescanned without reopening the window.</summary>
internal sealed class StampedScanCache
{
    private readonly Dictionary<string, (FileStamp Stamp, ContentIndexTexture.Scan? Scan)> _map = new();

    public ContentIndexTexture.Scan? Get(string path, FileStamp stamp, Func<ContentIndexTexture.Scan?> scan)
    {
        if (_map.TryGetValue(path, out var e) && e.Stamp == stamp) return e.Scan;
        var s = scan();
        _map[path] = (stamp, s);
        return s;
    }

    public void RemoveWhere(Func<string, bool> pathPredicate)
    {
        foreach (var k in new List<string>(_map.Keys))
            if (pathPredicate(k)) _map.Remove(k);
    }

    public void Clear() => _map.Clear();

    public int Count => _map.Count;
}
