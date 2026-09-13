using System.Collections.Concurrent;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// The per-body measurement cache: when it re-measures and when it must not.
/// <para/>
/// The shell is rebuilt on every equipment change, every settings tweak and every ambient trigger, while
/// the measurement it needs is a property of the body model and cannot have moved. Two levels, cheapest
/// first — reference equality for the usual case where an unchanged mod re-resolves to the same array, then
/// a content hash for the case that matters: a mod swapped underneath one path and handed over as a fresh
/// array. Getting the second wrong is the dangerous one, because the shell would then be cut using the
/// SHAPE of a body the character is no longer wearing.
/// </summary>
public class ConnectorProfileCacheTests
{
    private const string BodyMaterial = "/mt_c0201b0001_a.mtrl";
    private const string Path = "chara/human/c0201/obj/body/b0001/model/c0201b0001.mdl";

    private static byte[] Model(int tris) => SyntheticModel.Build(
        ["atr_top"],
        new SyntheticModel.Mesh(BodyMaterial,
            new SyntheticModel.Sub(0, Islands: 1, TrianglesPerIsland: tris)));

    private sealed class Store
    {
        public readonly ConcurrentDictionary<string,
            (byte[] Src, ulong Hash, SecondSkinWriter.ConnectorProfile P)> Map = new();
        public int Reads;

        public SecondSkinWriter.ConnectorProfile? Get(string path, byte[] bytes)
            => SecondSkinService.CachedConnectorProfile(Map, path, bytes, b =>
            {
                Reads++;
                return SecondSkinWriter.ReadConnectorProfile(b);
            });
    }

    /// <summary>The same array back is the common case, and it must cost nothing — not even a hash.</summary>
    [Fact]
    public void The_same_array_is_measured_once()
    {
        var store = new Store();
        var model = Model(40);

        var first = store.Get(Path, model);
        var second = store.Get(Path, model);

        Assert.Equal(1, store.Reads);
        Assert.Same(first, second);
    }

    /// <summary>
    /// A fresh array holding the same bytes is the same body. It re-hashes once to establish that, then
    /// adopts the new array so the free check wins from then on rather than hashing the model forever.
    /// </summary>
    [Fact]
    public void An_equal_copy_reuses_the_measurement_and_stops_rehashing()
    {
        var store = new Store();
        var model = Model(40);
        var copy = (byte[])model.Clone();

        var first = store.Get(Path, model);
        var second = store.Get(Path, copy);
        Assert.Equal(1, store.Reads);
        Assert.Same(first, second);

        Assert.True(ReferenceEquals(store.Map[Path].Src, copy),
            "the copy was not adopted, so every later composite re-hashes the model");
    }

    /// <summary>
    /// Different bytes under the same path is a body mod swapped out. This is the case the reference check
    /// alone cannot see and the content hash is here for: measuring the old shape against the new body is
    /// how the pass would drop geometry that is no longer redundant.
    /// </summary>
    [Fact]
    public void Changed_bytes_are_measured_again_and_replace_the_entry()
    {
        var store = new Store();
        var first = store.Get(Path, Model(40));
        var second = store.Get(Path, Model(60));

        Assert.Equal(2, store.Reads);
        Assert.NotSame(first, second);
        Assert.Equal(60, second!.Subs[0].Triangles);
        Assert.Equal(60, store.Map[Path].P.Subs[0].Triangles);
    }

    /// <summary>Two bodies are two entries — the path is the key, and a shell cuts several parts at once.</summary>
    [Fact]
    public void Each_path_is_measured_separately()
    {
        var store = new Store();
        store.Get(Path, Model(40));
        store.Get("chara/human/c0201/obj/body/b0001/model/c0201b0001_hand.mdl", Model(40));

        Assert.Equal(2, store.Reads);
        Assert.Equal(2, store.Map.Count);
    }

    /// <summary>An unreadable model caches nothing, so a later composite gets another chance at it rather
    /// than a remembered "no".</summary>
    [Fact]
    public void An_unreadable_model_is_not_cached()
    {
        var store = new Store();
        Assert.Null(store.Get(Path, [1, 2, 3, 4]));
        Assert.Empty(store.Map);
    }
}
