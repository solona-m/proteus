using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestPlatform.Common.Filtering;
using Xunit;
using Xunit.Abstractions;

namespace Proteus.Tests;

/// <summary>
/// The full suite's time budget (AGENTS.md → Testing): the slowest tests carry <c>[Trait("Suite", "Diagnostic")]</c> and
/// leave the full and quick suites; they run only on demand. These tests keep that partition honest: a Diagnostic test
/// is also Slow (so the unchanged quick filter skips it), exempt tests (Sensitive Areas writers, golden baselines, rule
/// tests, write-behaviour checks, known failures) are never Diagnostic, classes whose fixture setup is heavy move whole,
/// and the commands AGENTS.md documents select what they say. The wall-clock budget itself is a measured figure, not a
/// test (a timed whole-suite run inside the suite would be recursive and fail under concurrent load).
/// </summary>
public class SuiteBudgetTests
{
    private readonly ITestOutputHelper output;
    public SuiteBudgetTests(ITestOutputHelper output) => this.output = output;

    // ---------------------------------------------------------------- the lists

    /// <summary>Classes whose tests are never Diagnostic: golden baselines, Sensitive Areas writers' own tests, tests
    /// calling those writers directly, and the build-enforced rule tests.</summary>
    internal static readonly string[] ExemptClasses =
    {
        "GoldenTests", "GoldenCrossBodyLocalTests", "SkinTuckGoldenTests",
        "SecondSkinWriterVerbatimTests", "ModelLodTrimmerTests", "ModelMaterialWriterTests", "ModelMaterialWriterLocalTests",
        "VertexColorWriterTests", "MatcapRowWriterTests", "MatcapReadbackLocalTests", "MatcapWriterGuardTests",
        "MatcapFaceHornLocalTests", "ShaderUpgradeOrderingTests", "NailSocketTests", "PrGateTests",
        "LocalizationTests", "SupplyChainTests",
        // Binary game-file writer guards the owner keeps in the quick suite (with SecondSkinWriterVerbatim, LodTrimmer, VertexColor).
        "FaceModelWriteTests",
    };

    /// <summary>Single methods that are never Diagnostic ("Class.Method"): writes into users' mods checked in a class
    /// otherwise cut, and direct writer calls.</summary>
    internal static readonly string[] ExemptMethods =
    {
        "HairPortScanLocalTests.A_check_of_copied_real_mods_saves_ports_that_read_back", // HairPortWriter.Undo
        // Write-behaviour checks on ThisOldThingRun's output: groups append, body mods untouched, Penumbra's migration, no local paths.
        "SizeSetBatchLocalTests.Cli_only_appends_groups_and_options",
        "SizeSetBatchLocalTests.Body_mods_are_not_written",
        "SizeSetBatchLocalTests.Work_copy_conversion_matches_Penumbras",
        "SizeSetBatchLocalTests.Pack_holds_no_local_paths",
        "SizeSetBatchLocalTests.Second_run_adds_nothing",
        // The same kind of check in the other size-set fixture classes, so their fixtures stay paid (measured 2026-11-18).
        "SizeSetKeepSkinLocalTests.Ticked_without_make_again_leaves_every_file",
        "SizeSetKeepSkinLocalTests.Old_sizes_change_only_through_make_again",
        "SizeSetMadeOnLocalTests.Second_made_on_run_adds_nothing_and_says_so",
        "SizeSetMadeOnLocalTests.Cli_made_on_not_a_body_is_refused_and_writes_nothing",
        "SizeSetMadeOnLocalTests.Oblique_without_made_on_gets_nothing_and_is_told_to_choose_it", // manifest byte-identical after a refused run
        "SizeSetMadeOnSkinlessLocalTests.Second_Tutting_run_adds_nothing",
        // Direct ModelMaterialWriter / ModelAttributeWriter calls on real models (kept although ≥ 20 s in the full run).
        "MatcapTailLocalTests.Real_tail_models_take_matcap_published_beside_the_games_material",
        "MatcapDownstreamLocalTests.Refit_sizeset_hatcompat_lod0geometry_brush_no_nan_boundary_coincident",
        "MatcapVieraEarLocalTests.Real_viera_ear_model_takes_matcap",
        // Direct HairPortWriter.Save / SecondSkinWriter.Build calls. (Size-set tests that call PenumbraModMeta.MigrateWorkCopy
        // only to prepare a temp copy do not count: Work_copy_conversion_matches_Penumbras above checks it directly.)
        "HairPortLocalTests.The_reported_hairstyle_8_ports_with_the_skeleton_each_race_loads",
        "HairPortStryxLocalTests.Stryx_ports_to_midlander_man_alone",
        "FaceShellUnmirrorTests.A_face_shell_with_its_shapes_baked_in_keeps_its_sides",
        // The racial bake's layout/rename writer guards, kept quick by the owner.
        "RacialModelBakeTests.The_bake_keeps_the_model_s_layout",
        "RacialModelBakeTests.Renaming_a_material_moves_every_name_after_it",
    };

    /// <summary>Failing tests stay in the release run so the failure stays visible. Drop an entry once it is fixed.</summary>
    internal static readonly string[] KnownFailures =
    {
    };

    /// <summary>Backstop: a test class named like a Sensitive Areas writer or a rule test is never Diagnostic.</summary>
    internal static readonly Regex ExemptClassPattern = new(
        @"^(Golden|SkinTuckGolden|SecondSkinWriter|Model\w*Writer|ModelLodTrimmer|GearMaterialWriter|VertexColorWriter|RefitModService|MeshToggleService|PenumbraModMeta|ModelBackupOrder|HairPortWriter|EarlessHairWriter|ShaderUpgradeWriter|ShaderUpgradeOrdering|VariantRepointWriter|Matcap\w*Writer|MatcapReadback|Configuration|LogAnonymizer|LogExportService|Harness|PrGate|ContributorGate|SupplyChain|Localization|IpcContract)",
        RegexOptions.CultureInvariant);

    /// <summary>Test classes that moved whole because their class fixture's setup measured 7.5 s or more idle
    /// (AGENTS.md: a fixture's setup counts against every test of its class). Each carries the class-level tags.</summary>
    // Kept paid because their classes hold exempt write-behaviour tests (measured idle 2026-11-18): ThisOldThingRun ~127 s,
    // ObliqueRun ~156 s, KeepSkinRuns ~264 s, DanceRun ~82 s.
    internal static readonly (string Class, string Fixture, double SetupSeconds)[] MovedFixtures =
    {
        ("EquinoxSizeSetLocalTests", "EquinoxRun", 129),
        ("CrossSexGloveReshapeLocalTests", "HalonesMensSizeSet", 33),
        ("UvTransferMapsLocalTests", "TbseBakeFixture", 32),
        ("SizeSetMadeOnBootsLocalTests", "OopsBootsRun", 120),   // born Diagnostic, 2026-11-20
    };

    /// <summary>The heaviest methods (slowest row ≥ 300 s in the 2026-11-18 full run); any one alone would break the budget.</summary>
    internal static readonly string[] Heaviest =
    {
        "SkinlessTargetCensusLocalTests.Census_of_pairs_the_rule_changes",
        "CodeineDressHolesLocalTests.Codeine_upscales_are_no_worse",
        "SizeSetSkinMisfitTests.Luxe_sizes_pass_over_the_misread_Uranus",
        "SizeSetSourceTests.Body_key_chooses_the_size_the_refit_moves_least",
        "BustStandoffNiraeLocalTests.Real_chest_keeps_one_chest",
        "CodeineDressHolesLocalTests.Codeine_onto_other_bodies_is_no_worse",
        "SizeSetGameCopyLocalTests.BOObies_men_get_the_design_and_keep_the_copy",
        "NailSocketSweep.Every_installed_hand_closes_or_keeps_its_sockets",
        "SkirtHipsLocalTests.Hip_level_pants_are_no_worse",
        "CutOpenSkinOtherModsLocalTests.Other_garments_are_no_worse",
        "SizeSetRefitTests.Geometrics_jacket_is_sized_by_the_top_in_its_option_and_kept_outside_its_refit",
        "OutfitOrderLocalTests.Geometrics_jacket_stays_outside_the_bras_refit_on_Rue",
        "CrossSexNudeLegsLocalTests.Nude_legs_reach_tbse_gen_a",
        "OtherRaceRefitLocalTests.A_midlander_refit_of_the_highlander_shirt_has_no_new_folds",
        "SizeSetMadeOnLocalTests.Made_on_is_only_a_fallback_for_This_Old_Thing",
        "SizeSetSourceTests.A_covered_bust_garment_is_chosen_by_its_body",
    };

    // ---------------------------------------------------------------- the tests

    /// <summary>Keeps the <c>Suite</c> property present in every discovery and gives the diagnostic command a cheap target.
    /// (The xunit adapter accepts a filter naming a property no test has — <c>!=</c> then matches everything — so the guard
    /// against an empty selection is <c>TreatNoTestsAsError</c> in the documented commands.)</summary>
    [Fact]
    [Trait("Speed", "Slow")]
    [Trait("Suite", "Diagnostic")]
    [Trait("Plan", "EDGE-suite-budget-3")]
    [Trait("Plan", "AC-suite-budget-3")]
    public void Diagnostic_anchor() => Assert.True(true);

    [Fact]
    [Trait("Plan", "AC-suite-budget-6")]
    [Trait("Plan", "EDGE-suite-budget-1")]
    [Trait("Plan", "EDGE-suite-budget-3")]
    [Trait("Plan", "EDGE-suite-budget-24")]
    public void Every_diagnostic_test_is_also_slow()
    {
        var diagnostic = TestTraits.AllTests().Where(t => TestTraits.Has(t.Class, t.Method, "Suite", "Diagnostic")).ToList();
        Assert.Contains(diagnostic, t => t.Class == typeof(SuiteBudgetTests) && t.Method.Name == nameof(Diagnostic_anchor));
        var notSlow = diagnostic.Where(t => !TestTraits.Has(t.Class, t.Method, "Speed", "Slow")).Select(TestTraits.Name).ToList();
        Assert.True(notSlow.Count == 0, "Diagnostic tests must also be [Trait(\"Speed\", \"Slow\")] or the quick suite runs them: " + string.Join(", ", notSlow));
        var names = diagnostic.Select(TestTraits.Name).Distinct().OrderBy(n => n, StringComparer.Ordinal).ToList();
        output.WriteLine($"{names.Count} Diagnostic tests:");
        foreach (var n in names) output.WriteLine(n);
    }

    [Fact]
    [Trait("Plan", "AC-suite-budget-5")]
    [Trait("Plan", "EDGE-suite-budget-6")]
    [Trait("Plan", "EDGE-suite-budget-7")]
    [Trait("Plan", "EDGE-suite-budget-18")]
    [Trait("Plan", "EDGE-suite-budget-20")]
    public void Exempt_tests_are_never_diagnostic()
    {
        var tests = TestTraits.AllTests().ToList();
        var problems = new List<string>();
        foreach (var cls in ExemptClasses)
        {
            var inClass = tests.Where(t => t.Class.Name == cls).ToList();
            if (inClass.Count == 0) problems.Add($"exempt class {cls} has no tests (renamed?)");
            problems.AddRange(inClass.Where(IsDiagnostic).Select(t => "exempt class test is Diagnostic: " + TestTraits.Name(t)));
        }
        foreach (var name in ExemptMethods.Concat(KnownFailures))
        {
            var match = tests.Where(t => TestTraits.Name(t) == name).ToList();
            if (match.Count == 0) problems.Add($"exempt method {name} does not exist (renamed?)");
            problems.AddRange(match.Where(IsDiagnostic).Select(t => "exempt method is Diagnostic: " + name));
        }
        problems.AddRange(tests.Where(t => ExemptClassPattern.IsMatch(t.Class.Name) && IsDiagnostic(t))
                               .Select(t => "test in a writer/rule class is Diagnostic: " + TestTraits.Name(t)));
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems.Distinct()));
    }

    [Fact]
    [Trait("Plan", "AC-suite-budget-8")]
    [Trait("Plan", "AC-suite-budget-4")]
    public void The_heaviest_tests_stay_diagnostic()
    {
        var tests = TestTraits.AllTests().ToList();
        foreach (var name in Heaviest)
        {
            var match = tests.Where(t => TestTraits.Name(t) == name).ToList();
            Assert.True(match.Count > 0, $"{name} does not exist (renamed?)");
            Assert.True(match.All(IsDiagnostic), $"{name} alone breaks the full suite's budget: keep it [Trait(\"Suite\", \"Diagnostic\")]");
        }
    }

    [Fact]
    [Trait("Plan", "AC-suite-budget-9")]
    [Trait("Plan", "EDGE-suite-budget-11")]
    [Trait("Plan", "EDGE-suite-budget-14")]
    [Trait("Plan", "EDGE-suite-budget-17")]
    public void Moved_fixture_classes_are_wholly_diagnostic()
    {
        var types = typeof(SuiteBudgetTests).Assembly.GetTypes();
        foreach (var (cls, fixture, _) in MovedFixtures)
        {
            var type = types.SingleOrDefault(t => t.Name == cls);
            Assert.True(type != null, $"moved fixture class {cls} does not exist (renamed?)");
            Assert.True(type!.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IClassFixture<>)
                                                       && i.GetGenericArguments()[0].Name == fixture),
                $"{cls} no longer uses the fixture {fixture}");
            var classTraits = TestTraits.OfType(type).ToList();
            Assert.True(classTraits.Any(p => TestTraits.Is(p, "Suite", "Diagnostic")) && classTraits.Any(p => TestTraits.Is(p, "Speed", "Slow")),
                $"{cls} moved whole for its fixture {fixture}: it needs class-level Suite=Diagnostic and Speed=Slow");
            var exempt = TestTraits.AllTests().Where(t => t.Class == type && IsExemptByList(t)).Select(TestTraits.Name).ToList();
            Assert.True(exempt.Count == 0, $"{cls} holds exempt tests, so it cannot move whole: " + string.Join(", ", exempt));
        }
        var collectionFixtures = types.Where(t => t.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ICollectionFixture<>)))
                                      .Select(t => t.Name).ToList();
        Assert.True(collectionFixtures.Count == 0,
            "a collection fixture's setup is not covered by the fixture rule yet: " + string.Join(", ", collectionFixtures));
    }

    [Fact]
    [Trait("Plan", "AC-suite-budget-1")]
    [Trait("Plan", "AC-suite-budget-2")]
    [Trait("Plan", "AC-suite-budget-3")]
    [Trait("Plan", "AC-suite-budget-10")]
    [Trait("Plan", "EDGE-suite-budget-8")]
    [Trait("Plan", "EDGE-suite-budget-10")]
    [Trait("Plan", "EDGE-suite-budget-13")]
    public void Documented_filters_select_the_right_tests()
    {
        // This checks vstest's filter grammar and semantics on the exact strings AGENTS.md documents (and catches typos);
        // the xunit adapter's own trait provider is proven by the measured runs, not here.
        string commands = Section(File.ReadAllText(Path.Combine(RepoRoot, "AGENTS.md")), "## Commands");
        string full = CommandSpan(commands, "the full suite");
        string quick = CommandSpan(commands, "the quick suite");
        string diagnostic = CommandSpan(commands, "the diagnostic tests");
        foreach (var span in new[] { full, diagnostic })
        {
            int sep = span.IndexOf(" -- ", StringComparison.Ordinal);
            Assert.True(sep > 0 && span.IndexOf("RunConfiguration.TreatNoTestsAsError=true", sep, StringComparison.Ordinal) > sep,
                $"an empty selection must fail: put `-- RunConfiguration.TreatNoTestsAsError=true` after the filter in `{span}`");
        }

        var plain = new Dictionary<string, string[]>();
        var slow = new Dictionary<string, string[]> { ["Speed"] = new[] { "Slow" } };
        var slowDiag = new Dictionary<string, string[]> { ["Speed"] = new[] { "Slow" }, ["Suite"] = new[] { "Diagnostic" } };
        var diag = new Dictionary<string, string[]> { ["Suite"] = new[] { "Diagnostic" } };
        var sets = new[] { plain, slow, slowDiag, diag };

        Assert.Equal(new[] { true, true, false, false }, sets.Select(s => Matches(Filter(full), s)).ToArray());
        // The quick filter only knows Slow: a Diagnostic test without Slow (the last set) would run in it. That leak is
        // why Every_diagnostic_test_is_also_slow forbids that tagging; every real Diagnostic test is the third set.
        Assert.Equal(new[] { true, false, false, true }, sets.Select(s => Matches(Filter(quick), s)).ToArray());
        Assert.Equal(new[] { false, false, true, true }, sets.Select(s => Matches(Filter(diagnostic), s)).ToArray());

        // Iterating on one Diagnostic test: the usual targeted filter skips it; adding the category selects it.
        Assert.False(Matches("FullyQualifiedName~SnapPushTests&Speed!=Slow", slowDiag));
        Assert.True(Matches("Suite=Diagnostic&FullyQualifiedName~SnapPushTests", slowDiag));
    }

    [Fact]
    [Trait("Plan", "AC-suite-budget-7")]
    public void AgentsMd_documents_the_budget()
    {
        string agents = File.ReadAllText(Path.Combine(RepoRoot, "AGENTS.md"));
        string testing = Section(agents, "## Testing");
        foreach (var required in new[] { "10 minutes", "15 s", "7.5 s", "[Trait(\"Suite\", \"Diagnostic\")]", "fixture", "golden", "failing", "report the overrun" })
            Assert.True(testing.Contains(required, StringComparison.Ordinal), $"AGENTS.md Testing must say '{required}'");
        string commands = Section(agents, "## Commands");
        Assert.Contains("never before a release", LineWith(commands, "the diagnostic tests"));
        Assert.Contains("before a release tag", LineWith(commands, "the full suite"));
    }

    [Fact]
    [Trait("Plan", "EDGE-suite-budget-5")]
    [Trait("Plan", "EDGE-suite-budget-24")]
    public void Trait_reader_sees_method_class_base_and_partial_tags()
    {
        Assert.True(TestTraits.Has(typeof(SampleMethodTagged), typeof(SampleMethodTagged).GetMethod(nameof(SampleMethodTagged.M))!, "Suite", "Diagnostic"));
        Assert.False(TestTraits.Has(typeof(SampleMethodTagged), typeof(SampleMethodTagged).GetMethod(nameof(SampleMethodTagged.Other))!, "Suite", "Diagnostic"));
        Assert.True(TestTraits.Has(typeof(SampleClassTagged), typeof(SampleClassTagged).GetMethod(nameof(SampleClassTagged.M))!, "Speed", "Slow"));
        Assert.True(TestTraits.Has(typeof(SamplePartial), typeof(SamplePartial).GetMethod(nameof(SamplePartial.M))!, "Suite", "Diagnostic"));
        Assert.True(TestTraits.Has(typeof(SampleDerived), typeof(SampleDerived).GetMethod(nameof(SampleBase.M))!, "Speed", "Slow"));
        Assert.True(TestTraits.Has(typeof(SampleWrongCase), typeof(SampleWrongCase).GetMethod(nameof(SampleWrongCase.M))!, "Suite", "Diagnostic"));
        Assert.False(TestTraits.Has(typeof(SampleWrongCase), typeof(SampleWrongCase).GetMethod(nameof(SampleWrongCase.M))!, "Speed", "Slow"));
    }

    // ---------------------------------------------------------------- samples for the trait reader (no [Fact]: never run)

    public class SampleMethodTagged
    {
        [Trait("Suite", "Diagnostic")] public void M() { }
        public void Other() { }
    }

    [Trait("Speed", "Slow")]
    public class SampleClassTagged { public void M() { } }

    [Trait("Suite", "Diagnostic")]
    public partial class SamplePartial { }
    public partial class SamplePartial { public void M() { } }

    [Trait("Speed", "Slow")]
    public class SampleBase { public void M() { } }
    public class SampleDerived : SampleBase { }

    public class SampleWrongCase
    {
        [Trait("suite", "diagnostic")] public void M() { }
    }

    // ---------------------------------------------------------------- helpers

    private static bool IsDiagnostic((Type Class, MethodInfo Method) t) => TestTraits.Has(t.Class, t.Method, "Suite", "Diagnostic");

    private static bool IsExemptByList((Type Class, MethodInfo Method) t)
    {
        string name = TestTraits.Name(t);
        return ExemptClasses.Contains(t.Class.Name) || ExemptMethods.Contains(name) || KnownFailures.Contains(name)
               || ExemptClassPattern.IsMatch(t.Class.Name);
    }

    private static string RepoRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "proteus.slnx"))) dir = dir.Parent;
            Assert.True(dir != null, "run from the checkout: proteus.slnx not found");
            return dir!.FullName;
        }
    }

    /// <summary>The text of a "## " section, up to the next "## " heading.</summary>
    private static string Section(string doc, string heading)
    {
        int start = doc.IndexOf("\n" + heading, StringComparison.Ordinal);
        Assert.True(start >= 0, $"AGENTS.md has no '{heading}' section");
        int end = doc.IndexOf("\n## ", start + heading.Length + 1, StringComparison.Ordinal);
        return end < 0 ? doc[start..] : doc[start..end];
    }

    private static string LineWith(string section, string wording)
    {
        var lines = section.Split('\n').Where(l => l.Contains(wording, StringComparison.Ordinal)).ToList();
        Assert.True(lines.Count == 1, $"expected exactly one AGENTS.md Commands line saying '{wording}', found {lines.Count}");
        return lines[0];
    }

    /// <summary>The backtick span holding the `dotnet test` command on the line with that wording.</summary>
    private static string CommandSpan(string section, string wording)
    {
        var m = Regex.Match(LineWith(section, wording), "`(dotnet test [^`]*)`");
        Assert.True(m.Success, $"the '{wording}' line has no `dotnet test …` command");
        return m.Groups[1].Value;
    }

    private static string Filter(string command)
    {
        var m = Regex.Match(command, "--filter \"([^\"]*)\"");
        Assert.True(m.Success, $"no --filter \"…\" in `{command}`");
        return m.Groups[1].Value;
    }

    private static bool Matches(string filter, Dictionary<string, string[]> traits)
    {
        var wrapper = new FilterExpressionWrapper(filter);
        Assert.True(wrapper.ParseError == null, $"vstest cannot parse '{filter}': {wrapper.ParseError}");
        return wrapper.Evaluate(property =>
        {
            if (property == "FullyQualifiedName") return "Proteus.Tests.SnapPushTests.Between_sizes_of_one_body_the_model_is_byte_identical";
            return traits.TryGetValue(property, out var v) ? v : null;
        });
    }
}

/// <summary>Reads xunit <see cref="TraitAttribute"/>s from the method, its reflected test class and that class's base
/// types, comparing keys and values case-insensitively. Values match the adapter (measured: <c>Suite=diagnostic</c>
/// selects a <c>Diagnostic</c> test); keys are stricter than it (<c>suite=…</c> selects nothing there, so a wrong-case key
/// stays in the full run), and here such a tag still counts as Diagnostic and must carry Slow.</summary>
internal static class TestTraits
{
    public static IEnumerable<(string Key, string Value)> OfType(Type type)
    {
        for (var t = type; t != null && t != typeof(object); t = t.BaseType)
            foreach (var p in Read(t.GetCustomAttributesData())) yield return p;
    }

    public static IEnumerable<(string Key, string Value)> Of(Type testClass, MethodInfo method)
        => Read(method.GetCustomAttributesData()).Concat(OfType(testClass));

    public static bool Is((string Key, string Value) pair, string key, string value)
        => string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase) && string.Equals(pair.Value, value, StringComparison.OrdinalIgnoreCase);

    public static bool Has(Type testClass, MethodInfo method, string key, string value) => Of(testClass, method).Any(p => Is(p, key, value));

    /// <summary>Every test method in the assembly, with the class it runs in.</summary>
    public static IEnumerable<(Type Class, MethodInfo Method)> AllTests()
        => typeof(TestTraits).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                .Where(m => m.GetCustomAttributes(typeof(FactAttribute), inherit: true).Length > 0)
                .Select(m => (t, m)));

    public static string Name((Type Class, MethodInfo Method) t) => t.Class.Name + "." + t.Method.Name;

    private static IEnumerable<(string Key, string Value)> Read(IEnumerable<CustomAttributeData> attributes)
        => attributes.Where(a => a.AttributeType == typeof(TraitAttribute) && a.ConstructorArguments.Count == 2)
                     .Select(a => ((string)a.ConstructorArguments[0].Value!, (string)a.ConstructorArguments[1].Value!));
}
