using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Proteus.Gui;
using Proteus.Localization;
using Proteus.Services;
using Xunit;
using XivLiveMesh;

namespace Proteus.Tests;

/// <summary>
/// The Studio's "Port to all races" frozen as it was before its button bodies became shared statics (so the test
/// harness runs the Studio's own code): a verbatim copy of the Port and Remove bodies and of the result line. The
/// statics must write, remove and say exactly what this copy does — on twin mods, file for file.
/// </summary>
internal static class FrozenStudio
{
    /// <summary>The Port button's worker body as it was (from the pbd on), its log lines aside.</summary>
    public static (HairPortBuilder.Plan Plan, HairPortWriter.Outcome Outcome) Port(string modRoot, string sourceGamePath,
        IReadOnlyList<ushort> targets, PbdFile pbd, Func<string, byte[]?> readGameFile)
    {
        var ct = CancellationToken.None;
        var plan = HairPortBuilder.Build(modRoot, sourceGamePath, targets, pbd, readGameFile, withSharers: false, ct, (_, _) => { });
        ct.ThrowIfCancellationRequested();
        var outcome = HairPortWriter.Save(modRoot, plan.Writes, auto: false);
        HairPortScan.RememberSaved(modRoot, outcome.SavedWrites, plan.Warnings);
        return (plan, outcome);
    }

    /// <summary>The Remove ports button's worker body as it was.</summary>
    public static HairPortWriter.Outcome Remove(string modRoot, string sourceGamePath)
        => HairPortWriter.Undo(modRoot,
            e => e.SourceGamePath.Equals(sourceGamePath, StringComparison.OrdinalIgnoreCase), decline: false);

    /// <summary>Consume's result branch as it was: the line, and whether it was a failure or a warning.</summary>
    public static (string Text, bool Failed, bool Warn) ResultText(HairPortBuilder.Plan plan, HairPortWriter.Outcome outcome,
                                                                   bool removed)
    {
        string result;
        bool resultFailed = false, resultWarn = false;
        if (!outcome.Ok)
        {
            result = removed ? outcome.Message : string.Format(Strings.HairPort.FailedFmt, outcome.Message);
            resultFailed = true;
        }
        else if (removed)
            result = string.Format(Strings.HairPort.RemovedFmt, outcome.Count)
                   + (outcome.Message.Length > 0 ? " " + outcome.Message : "");
        else if (outcome.SavedWrites.Count(w => !w.Stripped) is > 0 and var ported)
        {
            result = string.Format(Strings.HairPort.DoneFmt, ported);
            if (plan.Warnings is { Count: > 0 } warnings)
            {
                result += "\n" + string.Join("\n", warnings.Select(w => $"c{w.Key:D4}: {w.Value}"));
                resultWarn = true;
            }
        }
        else result = Strings.HairPort.NothingToDo;
        return (result, resultFailed, resultWarn);
    }

    /// <summary>Every file of a mod folder, mod-relative, with its bytes — the twin's root taken out of any text.</summary>
    public static SortedDictionary<string, string> Tree(string root)
    {
        var tree = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            tree[Path.GetRelativePath(root, f).Replace('\\', '/')] = Convert.ToBase64String(File.ReadAllBytes(f));
        return tree;
    }
}

public class HairPortStudioCharacterizationTests
{
    private static readonly string Source = CrossSexFixtures.Source;

    private static HairPortFixtures.Mod Twin(bool mixedCase)
    {
        var mod = CrossSexFixtures.WomensMod();
        if (mixedCase)
            CrossSexFixtures.EditMeta(mod, n =>
            {
                var files = CrossSexFixtures.Option(n, "Long")["Files"]!.AsObject();
                files.Remove(Source);
                files["Chara/Human/c0201/Obj/Hair/h0005/Model/c0201h0005_hir.mdl"] = "long.mdl";
            });
        return mod;
    }

    [Theory]
    [Trait("Plan", "AC-hair-port-qa-9")]
    [Trait("Plan", "AC-hair-port-qa-2")]
    [Trait("Plan", "EDGE-hair-port-qa-2")]
    [InlineData(false)]
    [InlineData(true)]
    public void Studio_port_by_hand_is_unchanged(bool mixedCase)
    {
        using var frozen = Twin(mixedCase);
        using var now = Twin(mixedCase);
        var game = CrossSexFixtures.Game();
        // The path as the manifest spells it — what the Studio passes on.
        string source = frozen.Owner("Cut", "Long").Files.Keys.Single();
        ushort[] races = [401, 101];

        var (_, was) = FrozenStudio.Port(frozen.Root, source, races, CrossSexFixtures.Pbd(), game);
        var plan = HairPortPanel.PlanByHand(now.Root, source, races, CrossSexFixtures.Pbd(), game, CancellationToken.None, null);
        var outcome = HairPortPanel.SaveByHand(now.Root, plan, null);

        Assert.True(was.Ok, was.Message);
        Assert.Equal((was.Ok, was.Count, was.Message), (outcome.Ok, outcome.Count, outcome.Message));
        Assert.Equal(FrozenStudio.Tree(frozen.Root), FrozenStudio.Tree(now.Root));
        foreach (ushort race in races)
            Assert.Equal(HairPortScan.WarningFor(frozen.Root, HairPortPlan.HairPath(race, CrossSexFixtures.Id)),
                         HairPortScan.WarningFor(now.Root, HairPortPlan.HairPath(race, CrossSexFixtures.Id)));
    }

    [Fact]
    [Trait("Plan", "AC-hair-port-qa-9")]
    [Trait("Plan", "AC-hair-port-qa-5")]
    public void Studio_remove_by_hand_is_unchanged()
    {
        using var frozen = Twin(false);
        using var now = Twin(false);
        var game = CrossSexFixtures.Game();
        FrozenStudio.Port(frozen.Root, Source, [401, 101], CrossSexFixtures.Pbd(), game);
        FrozenStudio.Port(now.Root, Source, [401, 101], CrossSexFixtures.Pbd(), game);

        var was = FrozenStudio.Remove(frozen.Root, Source);
        var outcome = HairPortPanel.RemoveByHand(now.Root, Source);
        Assert.Equal((was.Ok, was.Count, was.Message), (outcome.Ok, outcome.Count, outcome.Message));
        Assert.Equal(FrozenStudio.Tree(frozen.Root), FrozenStudio.Tree(now.Root));
    }

    [Fact]
    [Trait("Plan", "AC-hair-port-qa-9")]
    public void Studio_result_line_is_unchanged()
    {
        HairPortWriter.PortWrite W(bool stripped = false) => new(new HairPortPlan.Owner("Cut", "Long", "Single", new Dictionary<string, string>()),
            Source, "long.mdl", "00000000", CrossSexFixtures.Target, [], [], Stripped: stripped);
        var none = new HairPortBuilder.Plan([], new Dictionary<ushort, string>(), new HashSet<ushort>());
        var warned = none with { Warnings = new Dictionary<ushort, string> { [101] = "it does not sit on c0101's head" } };
        var cases = new (HairPortBuilder.Plan Plan, HairPortWriter.Outcome Outcome, bool Removed)[]
        {
            (warned, new(true, "", 1, [W()]), false),
            (none, new(true, "", 2, [W(), W()]), false),
            (none, new(true, "", 1, [W(stripped: true)]), false),
            (none, new(true, "", 2), true),
            (none, new(true, "one set was edited since", 1), true),
            (none, new(false, "the mod could not be written", 0), false),
            (none, new(false, "nothing could be removed", 0), true),
        };
        foreach (var (plan, outcome, removed) in cases)
            Assert.Equal(FrozenStudio.ResultText(plan, outcome, removed), HairPortPanel.ResultOf(plan, outcome, removed));
    }
}
