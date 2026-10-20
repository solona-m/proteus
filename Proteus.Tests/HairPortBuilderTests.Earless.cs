using System.IO;
using System.Linq;
using Proteus.Services;
using Xunit;
using static Proteus.Tests.HairPortFixtures;

namespace Proteus.Tests;

/// <summary>Ports made from a Miqo'te hairstyle the earless setting pointed at a copy: from the author's file, always.</summary>
public partial class HairPortBuilderTests
{
    private static Mod EarlessCut()
    {
        var mod = new Mod("[" + Group("Cut", "Single", ("Long", new() { [Source] = "long.mdl" })) + "]");
        mod.Put("long.mdl", EaredHairModel(801));
        var saved = EarlessHairWriter.Save(mod.Root, EarlessHairBuilder.Build(mod.Root, Source).Writes);
        Assert.True(saved.Ok, saved.Message);
        Assert.Equal("Proteus Earless/long.mdl", HairPortPlan.FileFor(mod.Owner("Cut", "Long"), Source));
        return mod;
    }

    [Fact]
    [Trait("Plan", "AC-earless-miqote-hair-8")]
    [Trait("Plan", "EDGE-earless-miqote-hair-24")]
    public void Port_from_an_earless_swapped_source_tracks_the_author_file()
    {
        using var mod = EarlessCut();
        var plan = HairPortBuilder.Build(mod.Root, Source, [201], Pbd(), Game(Races), withSharers: false);
        Assert.Empty(plan.Refused);
        var write = Assert.Single(plan.Writes);
        Assert.Equal("long.mdl", write.SourceFile);
        Assert.Equal(HairPortPlan.Sha8(mod.Get("long.mdl")), write.SourceSha8);

        // Saved, then the ears put back: the port is still current, made from the same file.
        Assert.True(HairPortWriter.Save(mod.Root, plan.Writes, auto: true).Ok);
        Assert.True(EarlessHairWriter.Restore(mod.Root).Ok);
        var entry = HairPortRecord.Read(mod.Root).Ports.Single();
        Assert.False(HairPortBuilder.IsOutOfDate(mod.Root, entry, mod.Get("long.mdl"), HairPortPlan.ReadOwners(mod.Root),
                                                 HairPortRecord.Read(mod.Root).Ports, Game(Races)));
    }

    [Fact]
    [Trait("Plan", "EDGE-earless-miqote-hair-26")]
    public void Port_from_a_lost_record_copy_sources_the_derived_author_file()
    {
        using var mod = EarlessCut();
        File.Delete(Path.Combine(mod.Root, "Proteus", EarlessHairRecord.RecordFile));
        var plan = HairPortBuilder.Build(mod.Root, Source, [201], Pbd(), Game(Races), withSharers: false);
        Assert.Equal("long.mdl", Assert.Single(plan.Writes).SourceFile);
    }

    [Fact]
    [Trait("Plan", "EDGE-earless-miqote-hair-29")]
    public void Earless_source_without_author_file_is_refused_for_that_owner()
    {
        using var mod = EarlessCut();
        File.Delete(Path.Combine(mod.Root, "Proteus", EarlessHairRecord.RecordFile));
        File.Delete(Path.Combine(mod.Root, "long.mdl"));
        var plan = HairPortBuilder.Build(mod.Root, Source, [201], Pbd(), Game(Races), withSharers: false);
        Assert.Empty(plan.Writes);
        Assert.True(plan.Refused.ContainsKey(201));
    }

    [Fact]
    [Trait("Plan", "EDGE-earless-miqote-hair-34")]
    public void Earless_source_with_unreadable_record_is_no_verdict()
    {
        using var mod = EarlessCut();
        File.WriteAllText(Path.Combine(mod.Root, "Proteus", EarlessHairRecord.RecordFile), "{ not json");
        var plan = HairPortBuilder.Build(mod.Root, Source, [201], Pbd(), Game(Races), withSharers: false);
        Assert.Empty(plan.Writes);
        Assert.True(plan.Unreadable);
        Assert.False(plan.IsVerdict(201));
    }
}
