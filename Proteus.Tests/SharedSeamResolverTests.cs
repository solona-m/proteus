using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Proteus.Services;
using Xunit;
using Confidence = Proteus.Services.BodySizeMatch.Confidence;

namespace Proteus.Tests;

/// <summary>
/// Which seam a waist split's pieces share and whether it is held (see <see cref="RefitCore.ResolveSeamGate"/>), and what
/// a refit's record keeps of it: on a synthetic mod of the split tube (<see cref="SharedSeamTests"/>) in a temp folder.
/// </summary>
public sealed class SharedSeamResolverTests : IDisposable
{
    private const string Race = "0201";
    private const string TopPath = "chara/equipment/e0628/model/c0201e0628_top.mdl";
    private const string LegsPath = "chara/equipment/e0628/model/c0201e0628_dwn.mdl";
    private readonly string root = Path.Combine(Path.GetTempPath(), "proteus-shared-seam-" + Guid.NewGuid().ToString("N"));

    public SharedSeamResolverTests()
    {
        Directory.CreateDirectory(root);
        Write("top/a.mdl", SharedSeamTests.TopBytes(doubleSided: true));
        Write("top/b.mdl", SharedSeamTests.TopBytes(doubleSided: true, y1: 1.25f));
        Write("legs/a.mdl", SharedSeamTests.LegsBytes(doubleSided: true));
        Write("body/chest.mdl", BodyBytes(0.126f));
        Write("body/chest2.mdl", BodyBytes(0.13f));
        Write("body/legs.mdl", BodyBytes(0.126f));
        Write("body/fat.mdl", BodyBytes(0.145f));
    }

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); } catch (IOException) { }
    }

    private void Write(string rel, byte[] bytes)
    {
        string path = Path.Combine(root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    private static byte[] BodyBytes(float radius)
        => SyntheticModel.BuildSurface([], SharedSeamTests.Tube(SharedSeamTests.Skin, radius, 0.7f, 1.4f, 14,
                                                                (_, _) => [("j_kosi", 1f)], cap: true));

    private static BodyOption Option(string rel, string slot)
        => new("Size", "", Path.GetFileNameWithoutExtension(rel), rel,
               slot == "_top" ? "chara/equipment/e0000/model/c0201e0000_top.mdl" : "chara/equipment/e0000/model/c0201e0000_dwn.mdl",
               slot);

    private BodySizeCatalog Catalog(params (string Rel, string Slot)[] options)
        => new(root, options.Select(o => Option(o.Rel, o.Slot)).ToList());

    private static List<PenumbraModMeta.Redirect> Redirects(params (string Path, string File, string Source)[] rows)
        => rows.Select(r => new PenumbraModMeta.Redirect(r.Path, r.File, r.Source)).ToList();

    private static List<PenumbraModMeta.Redirect> Mod()
        => Redirects((TopPath, "top/a.mdl", "Top Size / Body A"), (TopPath, "top/b.mdl", "Top Size / Other B"),
                     (LegsPath, "legs/a.mdl", "Leg Size / A"));

    private static AutoRefitDecisions.Pick Pick(string dir, Confidence confidence = Confidence.Likely, bool fromCloth = false)
        => new(dir, Option("body/chest.mdl", "_top"), confidence, fromCloth);

    private RefitCore.SeamGate Gate(Func<RefitCore.SeamFile, AutoRefitDecisions.Pick?> read, BodySizeCatalog? chest = null,
                                    BodySizeCatalog? legs = null, List<string>? notes = null, bool bellyCheck = true,
                                    List<PenumbraModMeta.Redirect>? redirects = null)
    {
        var files = RefitCore.SeamFilesOf(redirects ?? Mod(), null, Race)!;
        return RefitCore.ResolveSeamGate(root, files, Race, "Body", chest ?? Catalog(("body/chest.mdl", "_top")), "Body",
                                         legs ?? Catalog(("body/legs.mdl", "_dwn")), (f, _, _, _) => read(f),
                                         notes == null ? null : notes.Add, bellyCheck);
    }

    [Fact]
    [Trait("Plan", "EDGE-split-waist-seam-6")]
    [Trait("Plan", "EDGE-split-waist-seam-19")]
    public void SameGate_FromTopAndLegs_MixedParts()
    {
        var gate = Gate(_ => Pick("Body"));
        Assert.Equal("top/a.mdl", gate.Top!.File);
        byte[] top = File.ReadAllBytes(Path.Combine(root, "top/a.mdl"));
        byte[] legs = File.ReadAllBytes(Path.Combine(root, "legs/a.mdl"));
        // The same key for both pieces, whichever asks: it names the dirs it was given (the chest's and the legs' body).
        var fromTop = RefitCore.SeamForGarment(gate, ModelPartReader.Read(top)!, top, "Chest Body", "Legs Body");
        var fromLegs = RefitCore.SeamForGarment(gate, ModelPartReader.Read(legs)!, legs, "Chest Body", "Legs Body");
        Assert.NotNull(fromTop.Seam);
        Assert.NotNull(fromLegs.Seam);
        Assert.Equal(fromTop.Key, fromLegs.Key);
        Assert.Equal("Chest Body|Legs Body|top/a.mdl", fromTop.Key);
    }

    [Fact]
    [Trait("Plan", "EDGE-split-waist-seam-2")]
    public void NoCandidateOnChestBody_NoPin_Noted()
    {
        var gate = Gate(_ => Pick("Elsewhere"));
        Assert.Null(gate.Top);
        Assert.Contains("made on Body", gate.Refusal, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData((int)Confidence.NoBodyMesh, false)]
    [InlineData((int)Confidence.Ambiguous, false)]
    [InlineData((int)Confidence.TooLittle, false)]
    [InlineData((int)Confidence.Likely, true)]
    [Trait("Plan", "EDGE-split-waist-seam-3")]
    [Trait("Plan", "EDGE-split-waist-seam-9")]
    public void SkinlessOrClothOrLowConfidence_NeverQualifies(int confidenceValue, bool fromCloth)
    {
        var confidence = (Confidence)confidenceValue;
        Assert.False(RefitCore.IsSkinReading(Pick("Body", confidence, fromCloth)));
        Assert.False(RefitCore.IsSkinReading(null));
        Assert.True(RefitCore.IsSkinReading(Pick("Body")));
        Assert.Null(Gate(_ => Pick("Body", confidence, fromCloth)).Top);
    }

    [Fact]
    [Trait("Plan", "EDGE-split-waist-seam-4")]
    public void ChestSpread_FailsAbove_PassesBelow()
    {
        var spread = Catalog(("body/chest.mdl", "_top"), ("body/chest2.mdl", "_top"));
        var refused = Gate(_ => Pick("Body"), chest: spread);
        Assert.Null(refused.Top);
        Assert.Contains("nearer one chest than another", refused.Refusal, StringComparison.Ordinal);
        Assert.NotNull(Gate(_ => Pick("Body"), chest: spread, bellyCheck: false).Top);
        Assert.NotNull(Gate(_ => Pick("Body")).Top);
    }

    [Fact]
    [Trait("Plan", "EDGE-split-waist-seam-5")]
    public void SeamPointInsideChestOrLegsOption_NoPin()
    {
        var inChest = Gate(_ => Pick("Body"), chest: Catalog(("body/chest.mdl", "_top"), ("body/fat.mdl", "_top")), bellyCheck: false);
        Assert.Null(inChest.Top);
        Assert.Contains("lies inside", inChest.Refusal, StringComparison.Ordinal);
        var inLegs = Gate(_ => Pick("Body"), legs: Catalog(("body/fat.mdl", "_dwn")));
        Assert.Null(inLegs.Top);
        Assert.Contains("lies inside", inLegs.Refusal, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Plan", "EDGE-split-waist-seam-7")]
    public void ThreeWaists_CoverageSeparates()
    {
        // The seam top's split; a legs piece of another waist (its ring 15 mm lower) meets none of it.
        var gate = Gate(_ => Pick("Body"));
        byte[] other = SyntheticModel.BuildSurface([], SharedSeamTests.Tube(SharedSeamTests.Cloth, SharedSeamTests.ClothRadius,
                                                                            0.8f, 0.985f, 3, (_, _) => [("j_kosi", 1f)],
                                                                            doubleSided: true));
        var (seam, _, why) = RefitCore.SeamForGarment(gate, ModelPartReader.Read(other)!, other, "Body", "Body");
        Assert.Null(seam);
        Assert.Contains("another waist", why, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Plan", "EDGE-split-waist-seam-10")]
    public void OverCap_NoneRead_NoPin()
    {
        var many = Enumerable.Range(0, RefitCore.MostSeamFiles + 1)
                             .Select(i => (TopPath, $"top/{i}.mdl", $"Top Size / {i}")).Append((LegsPath, "legs/a.mdl", "Leg Size / A"))
                             .ToArray();
        var notes = new List<string>();
        Assert.Null(RefitCore.SeamFilesOf(Redirects(many), null, Race, notes.Add));
        Assert.Contains(notes, n => n.Contains("none read", StringComparison.Ordinal));
        var sarelle = Enumerable.Range(0, 16).Select(i => (TopPath, $"top/{i}.mdl", $"Top Size / {i}"))
                                .Concat(Enumerable.Range(0, 7).Select(i => (LegsPath, $"legs/{i}.mdl", $"Leg Size / {i}"))).ToArray();
        Assert.NotNull(RefitCore.SeamFilesOf(Redirects(sarelle), null, Race));
    }

    [Fact]
    [Trait("Plan", "EDGE-split-waist-seam-11")]
    public void UnreadableFile_Skipped()
    {
        var notes = new List<string>();
        var redirects = Redirects((TopPath, "top/missing.mdl", "Top Size / Missing"), (TopPath, "top/a.mdl", "Top Size / Body A"),
                                  (LegsPath, "legs/a.mdl", "Leg Size / A"));
        var gate = Gate(_ => Pick("Body"), notes: notes, redirects: redirects);
        Assert.Equal("top/a.mdl", gate.Top!.File);
        Assert.Contains(notes, n => n.Contains("missing.mdl", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Plan", "EDGE-split-waist-seam-12")]
    [Trait("Plan", "EDGE-split-waist-seam-28")]
    public void ProteusOptions_ExcludedFromCandidatesAndHash()
    {
        var record = new BodyRetargetWriter.Record
        {
            Options = [new BodyRetargetWriter.Entry { Group = "Top Size", InAuthorGroup = true, Name = "Tre S", GamePath = TopPath }],
        };
        var plain = RefitCore.SeamFilesOf(Mod(), record, Race)!;
        var withOurs = Mod();
        withOurs.Add(new(TopPath, "Body Retarget/Tre M/" + TopPath, "Top Size / Tre M"));      // under the refit folder
        withOurs.Add(new(TopPath, "elsewhere/tre s.mdl", "Top Size / Tre S"));                  // a recorded option
        var filtered = RefitCore.SeamFilesOf(withOurs, record, Race)!;
        Assert.Equal(plain.Tops.Select(t => t.File), filtered.Tops.Select(t => t.File));
        Assert.Equal(RefitCore.CandidateListHash(plain), RefitCore.CandidateListHash(filtered));

        // An author option added in the mod's groups changes the hash.
        var added = Mod();
        added.Add(new(TopPath, "top/c.mdl", "Top Size / C"));
        Assert.NotEqual(RefitCore.CandidateListHash(plain), RefitCore.CandidateListHash(RefitCore.SeamFilesOf(added, null, Race)!));
    }

    [Fact]
    [Trait("Plan", "EDGE-split-waist-seam-28")]
    public void CrossRunCache_ReusedUntilListBodiesOrStampChanges()
    {
        var files = RefitCore.SeamFilesOf(Mod(), null, Race)!;
        string key = RefitCore.SeamCacheKey(root, files, "Body", "Body", Race, "bodies v1");
        Assert.NotEqual(key, RefitCore.SeamCacheKey(root, files, "Body", "Body", Race, "bodies v2"));
        Assert.NotEqual(key, RefitCore.SeamCacheKey(root, files, "Other", "Body", Race, "bodies v1"));
        File.SetLastWriteTimeUtc(Path.Combine(root, "top/a.mdl"), DateTime.UtcNow.AddMinutes(1));
        Assert.NotEqual(key, RefitCore.SeamCacheKey(root, files, "Body", "Body", Race, "bodies v1"));

        var cache = new RefitCore.SeamCache();
        int made = 0;
        RefitCore.SeamGate Make() { made++; return new RefitCore.SeamGate(null, "none"); }
        System.Threading.Tasks.Parallel.For(0, 8, _ => cache.GetOrAdd("k", Make));
        Assert.Equal(1, cache.Count);
        var first = cache.GetOrAdd("k", Make);
        Assert.Same(first, cache.GetOrAdd("k", Make));
    }

    [Fact]
    [Trait("Plan", "EDGE-split-waist-seam-34")]
    public void ReadingBudget_NamedFirst_CapEight()
    {
        var tops = Enumerable.Range(0, 10).Select(i => new RefitCore.SeamFile(TopPath, $"top/{i}.mdl", i == 7 ? "Top Size / Tre M" : $"Top Size / Yab {i}"))
                             .ToList();
        Assert.Equal("top/7.mdl", RefitCore.SeamCandidateOrder(tops, "[YAS] Tre").First().File);
        Assert.Equal(tops.Select(t => t.File), RefitCore.SeamCandidateOrder(tops, "Neolithe").Select(t => t.File));

        // Nine split tops, none read on the body: the gate stops after eight readings.
        var redirects = Enumerable.Range(0, 9).Select(_ => (TopPath, "top/a.mdl", "x")).ToList();
        for (int i = 0; i < 9; i++)
        {
            Write($"top/n{i}.mdl", SharedSeamTests.TopBytes(doubleSided: true));
            redirects[i] = (TopPath, $"top/n{i}.mdl", $"Top Size / N{i}");
        }
        int reads = 0;
        var gate = Gate(_ => { reads++; return Pick("Elsewhere"); },
                        redirects: Redirects([.. redirects, (LegsPath, "legs/a.mdl", "Leg Size / A")]));
        Assert.Null(gate.Top);
        Assert.Equal(RefitCore.SeamReadingCap, reads);
    }

    [Fact]
    [Trait("Plan", "EDGE-split-waist-seam-20")]
    public void NoChestOrLegsCatalog_NoPin()
    {
        var files = RefitCore.SeamFilesOf(Mod(), null, Race)!;
        var gate = RefitCore.ResolveSeamGate(root, files, Race, "Body", null, "Body", Catalog(("body/legs.mdl", "_dwn")),
                                             (_, _, _, _) => Pick("Body"));
        Assert.Null(gate.Top);
        Assert.Contains("could not be read", gate.Refusal, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Plan", "AC-split-waist-seam-10")]
    [Trait("Plan", "EDGE-split-waist-seam-25")]
    public void Existing_SeamKey_PutBackUnlessForced()
    {
        static BodyRetargetWriter.Entry E(string? seam) => new() { Seam = seam };
        const string key = "Chest|Legs|top/a.mdl";
        // Not compared at all: always the same.
        Assert.True(AutoRefitWatcher.SameSeam(E(null), null, forced: true));
        // Recorded before the seam was held: put back unless forced; forced, remade only when a seam applies now.
        Assert.True(AutoRefitWatcher.SameSeam(E(null), (key, true), forced: false));
        Assert.False(AutoRefitWatcher.SameSeam(E(null), (key, true), forced: true));
        Assert.True(AutoRefitWatcher.SameSeam(E(null), (null, true), forced: true));
        // Recorded: equal (or dropped, the same seam) is the same; another seam, or none now, is not.
        Assert.True(AutoRefitWatcher.SameSeam(E(key), (key, true), forced: true));
        Assert.True(AutoRefitWatcher.SameSeam(E(key + "|dropped"), (key, true), forced: false));
        Assert.False(AutoRefitWatcher.SameSeam(E(key), ("Chest|Legs|top/b.mdl", true), forced: false));
        Assert.False(AutoRefitWatcher.SameSeam(E(key), (null, true), forced: false));
        Assert.Equal(key, RefitCore.RecordedSeam(key, kept: true));
        Assert.Equal(key + "|dropped", RefitCore.RecordedSeam(key, kept: false));
        Assert.Null(RefitCore.RecordedSeam(null, kept: true));
    }

    [Fact]
    [Trait("Plan", "EDGE-split-waist-seam-25")]
    public void EntrySeam_OmittedWhenNull_RoundTrips()
    {
        var options = new JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never };
        string none = JsonSerializer.Serialize(new BodyRetargetWriter.Entry { Name = "A" }, options);
        Assert.DoesNotContain("\"Seam\"", none, StringComparison.Ordinal);
        Assert.Null(JsonSerializer.Deserialize<BodyRetargetWriter.Entry>("{\"Name\":\"A\"}")!.Seam);
        string some = JsonSerializer.Serialize(new BodyRetargetWriter.Entry { Name = "A", Seam = "C|L|t.mdl" }, options);
        Assert.Equal("C|L|t.mdl", JsonSerializer.Deserialize<BodyRetargetWriter.Entry>(some)!.Seam);
    }

    [Fact]
    [Trait("Plan", "EDGE-split-waist-seam-27")]
    public void SizeSetMadeOnOtherBody_NoPin()
    {
        byte[] legs = File.ReadAllBytes(Path.Combine(root, "legs/a.mdl"));
        var notes = new List<string>();
        var request = new SizeSetRefit.Request(root, ["rue"], false, false, [], MadeOn: "Other Body");
        var piece = new SizeSetRefit.Piece(LegsPath, "_dwn", null, [], "Legs", "Leg Size", Race);
        Assert.Null(SizeSetRefit.SharedSeamFor(request, piece, ModelPartReader.Read(legs)!, legs, "Body",
                                               Catalog(("body/legs.mdl", "_dwn")), Race, Mod(), [], new(), null, notes.Add));
        Assert.Contains(notes, n => n.Contains("Made on", StringComparison.Ordinal));
    }
}
