using System;
using System.Collections.Concurrent;
using System.Threading;

namespace Proteus.Services;

/// <summary>
/// A memo for work repeated within a build, emptied when the last overlapping build leaves, so nothing it holds
/// outlives the builds that use it. Builds can overlap on one service; clearing at one build's end would drop entries
/// another is still reusing, so the in-flight count decides.
/// </summary>
internal sealed class BuildScopedMemo<TKey, TValue> where TKey : notnull
{
    private readonly ConcurrentDictionary<TKey, TValue> entries = new();
    private int inFlight;

    /// <summary>Enter a build's scope; dispose the result when the build ends (any path). Disposing twice counts once.</summary>
    public IDisposable Enter()
    {
        Interlocked.Increment(ref inFlight);
        return new Leave(this);
    }

    public bool TryGetValue(TKey key, out TValue value) => entries.TryGetValue(key, out value!);

    public TValue this[TKey key] { set => entries[key] = value; }

    public void Clear() => entries.Clear();

    public int Count => entries.Count;

    public int InFlight => Volatile.Read(ref inFlight);

    private void Exit()
    {
        if (Interlocked.Decrement(ref inFlight) == 0) entries.Clear();
    }

    private sealed class Leave(BuildScopedMemo<TKey, TValue> owner) : IDisposable
    {
        private int done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref done, 1) == 0) owner.Exit();
        }
    }
}
