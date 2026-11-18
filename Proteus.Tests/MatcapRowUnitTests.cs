using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Proteus.Gui;
using Proteus.Localization;
using Proteus.Services;
using Proteus.Services.Matcap;
using Proteus.TestHarness;
using Xunit;

namespace Proteus.Tests;

/// <summary>Row matcaps, the pure pieces: names, the pool, triangle footprints, staleness, scheduling, panel rules, preview pick, harness fields.</summary>
public class MatcapRowUnitTests
{
    private static ModelPart Part(string material, string label = "1.1", int island = -1, int mesh = 0) => new()
    {
        Mesh = mesh, Submesh = 0, Island = island, Label = label, Material = material, Triangles = [0, 1, 2], Ordinals = [0],
        AttributeMask = 0, Min = default, Max = default, Toggleable = true,
    };

    private const string Mat = "/mt_c0201e6255_top_a.mtrl";
    private const string Cut = "/mt_c0201e6255_top_a_mcutabc1231.mtrl";
    private const string Mcap = "/mt_c0201e6255_top_a_mcapabc1231.mtrl";

    // ── names and the pool ────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "EDGE-matcap-row-8")]
    [Trait("Plan", "EDGE-matcap-row-9")]
    public void Cut_part_is_on_matcap_a_change_alone_and_mixed_with_a_plain_part()
    {
        Assert.True(MatcapPlan.IsCutName(Cut));
        Assert.False(MatcapPlan.IsCutName(Mcap));
        Assert.True(MatcapPlan.IsOnMatcap(Part(Cut)));
        Assert.Equal(MatcapPlan.ApplyKind.Change, MatcapPlan.KindOfApply([Part(Cut)]));
        Assert.Equal(MatcapPlan.ApplyKind.Mixed, MatcapPlan.KindOfApply([Part(Cut), Part(Mat, "1.2")]));
    }

    [Fact]
    [Trait("Plan", "AC-matcap-row-6")]
    public void ChangeNames_maps_cut_names_to_their_owner()
    {
        var record = new MatcapRecord();
        record.Materials.Add(new MatcapRecord.MaterialEntry { Name = Mcap, Row = new MatcapRecord.RowRef { Cut = Cut } });
        Assert.Equal([Mcap], MatcapPlan.ChangeNames([Part(Cut), Part(Mcap, "1.2")], record));
        Assert.Same(record.Materials[0], record.EntryFor(Cut));
        Assert.Empty(MatcapPlan.ChangeNames([Part(Cut)], new MatcapRecord()));
    }

    [Fact]
    public void NewCutName_takes_the_source_stem_and_skips_taken_names()
    {
        Assert.Equal("/mt_x_mcutabc1231.mtrl", MatcapPlan.NewCutName("/mt_x.mtrl", "abc123", []));
        Assert.Equal("/mt_x_mcutabc1232.mtrl", MatcapPlan.NewCutName("/mt_x.mtrl", "abc123", ["/mt_x_mcutabc1231.mtrl"]));
        Assert.Equal("/mt_x.mtrl", MatcapRowCut.SourceNameOf("/mt_x_mcutabc1232.mtrl"));
    }

    [Fact]
    [Trait("Plan", "AC-matcap-row-1")]
    public void RowPool_is_every_whole_tickable_part_of_the_material_not_on_a_matcap()
    {
        IReadOnlyList<ModelPart> parts = [Part(Mat, "1.1"), Part(Mat, "1.1.1", island: 0), Part(Cut, "2.1"), Part("/mt_b.mtrl", "3.1"),
                                          Part(Mat, "4.1", mesh: 3)];
        Assert.Equal(["1.1", "4.1"], MatcapPlan.RowPool(parts, MatcapPlan.Kind.Gear, [], Mat).Select(p => p.Label));
        Assert.Equal([Mat, "/mt_b.mtrl"], MatcapPlan.RowMaterials(parts, MatcapPlan.Kind.Gear, []));
    }

    // ── triangle footprints ───────────────────────────────────────────────

    private static MatcapRowCut.Bake Bake(Func<int, int, bool> row, int w = 16, int h = 16, bool zero = true, float threshold = 0f,
                                          Func<int, int, byte>? blue = null)
    {
        var mask = new bool[w * h];
        var b = new byte[w * h];
        int count = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                mask[y * w + x] = row(x, y);
                if (mask[y * w + x]) count++;
                b[y * w + x] = blue?.Invoke(x, y) ?? 255;
            }
        return new MatcapRowCut.Bake
        {
            Key = "k", Temp = "", GamePath = "", Mask = mask, W = w, H = h, ThresholdZero = zero, Threshold = threshold,
            Blue = b, RowTexels = count,
        };
    }

    private static ModelMaterialWriter.TriangleUv Tri(float u0, float v0, float u1, float v1, float u2, float v2) =>
        new(new ModelMaterialWriter.TriangleKey(0, 0, 0), u0, v0, u1, v1, u2, v2);

    [Fact]
    [Trait("Plan", "AC-matcap-row-11")]
    [Trait("Plan", "EDGE-matcap-row-29")]
    public void Only_triangles_over_row_texels_touch_and_lace_holes_elsewhere_dont()
    {
        var left = Bake((x, _) => x < 8);
        Assert.True(MatcapRowCut.Touches(Tri(0.05f, 0.1f, 0.3f, 0.1f, 0.05f, 0.4f), [left]));
        Assert.False(MatcapRowCut.Touches(Tri(0.6f, 0.1f, 0.9f, 0.1f, 0.6f, 0.4f), [left]));
    }

    [Fact]
    [Trait("Plan", "EDGE-matcap-row-42")]
    public void Triangle_straddling_u1_wrap_is_kept()
    {
        var column0 = Bake((x, _) => x == 0);
        Assert.True(MatcapRowCut.Touches(Tri(0.98f, 0.2f, 1.02f, 0.2f, 0.98f, 0.4f), [column0]));
        Assert.False(MatcapRowCut.Touches(Tri(0.5f, 0.2f, 0.6f, 0.2f, 0.5f, 0.4f), [column0]));
    }

    [Fact]
    public void Garbage_uvs_near_float_max_finish_and_touch_nothing()
    {
        // Farfalla's wrist piece carries hidden triangles with UVs at ±3.4e38: the texel span overflowed and the scan never
        // ended (Apply and the click preview hung). Such a triangle samples nothing real: it touches no row and holds no
        // hole, like a non-finite one. Run with a deadline so a regression fails rather than hangs the suite.
        var big = 3.4028233E+38f;
        var garbage = Tri(0.5f, -big, 0.5f, 0.9f, big, 0.9f);
        var every = Bake((_, _) => true, w: 2048, h: 2048);
        var holes = Bake((x, _) => x < 8, zero: false, threshold: 0.5f, w: 2048, h: 2048, blue: (_, _) => 0);
        var run = System.Threading.Tasks.Task.Run(() => (MatcapRowCut.Touches(garbage, [every]), MatcapRowCut.HoleInside(garbage, [holes]),
            MatcapRowCut.Touches(Tri(1e20f, 0.1f, 1e20f, 0.2f, 1e20f, 0.15f), [every])));
        Assert.True(run.Wait(TimeSpan.FromSeconds(5)), "the footprint scan did not finish");
        Assert.Equal((false, false, false), run.Result);
        // Within the bound, a span a texture wide or more is the whole width, and a far tile still wraps.
        Assert.True(MatcapRowCut.Touches(Tri(-3f, 0.1f, 2f, 0.1f, 0f, 0.15f), [Bake((x, y) => x == 15 && y == 1)]));
        Assert.True(MatcapRowCut.Touches(Tri(9000.1f, 0.1f, 9000.15f, 0.1f, 9000.1f, 0.15f), [Bake((x, y) => x == 1 && y == 1)]));
        Assert.False(MatcapRowCut.Touches(Tri(9000.1f, 0.1f, 9000.15f, 0.1f, 9000.1f, 0.15f), [Bake((x, y) => x == 8 && y == 1)]));
    }

    [Fact]
    [Trait("Plan", "EDGE-matcap-row-40")]
    public void Union_mask_keeps_triangles_whose_row_is_only_in_another_options_index()
    {
        var worn = Bake((x, _) => x < 4);
        var other = Bake((x, _) => x >= 12);
        var right = Tri(0.8f, 0.2f, 0.95f, 0.2f, 0.8f, 0.4f);
        Assert.False(MatcapRowCut.Touches(right, [worn]));
        Assert.True(MatcapRowCut.Touches(right, [worn, other]));
    }

    [Fact]
    [Trait("Plan", "EDGE-matcap-row-30")]
    public void Alpha_holes_inside_row_triangles_are_reported_only_for_alpha_tested_bakes()
    {
        // Row on the left half; an off-row hole (blue 0) at column 9 inside a triangle spanning both halves.
        Func<int, int, byte> hole = (x, _) => x == 9 ? (byte)0 : (byte)255;
        var tested = Bake((x, _) => x < 8, zero: false, threshold: 0.5f, blue: hole);
        var untested = Bake((x, _) => x < 8, zero: true, blue: hole);
        var spanning = Tri(0.3f, 0.2f, 0.7f, 0.2f, 0.3f, 0.5f);
        Assert.True(MatcapRowCut.HoleInside(spanning, [tested]));
        Assert.False(MatcapRowCut.HoleInside(spanning, [untested]));
        Assert.False(MatcapRowCut.HoleInside(Tri(0.05f, 0.2f, 0.3f, 0.2f, 0.05f, 0.4f), [tested]));
    }

    [Theory]
    [Trait("Plan", "EDGE-matcap-row-25")]
    [Trait("Plan", "EDGE-matcap-row-24")]
    [InlineData(SubRowPick.Both, 32)]
    [InlineData(SubRowPick.B, 32)]
    [InlineData(SubRowPick.A, 0)]
    public void Cut_takes_the_larger_index_size_and_only_the_picked_sub_row(SubRowPick sub, int expectedColumns)
    {
        // An 8×8 normal and a 64×64 index (left half row 15 B): the cut is 64×64.
        var normal = Enumerable.Range(0, 8 * 8).SelectMany(_ => new byte[] { 128, 128, 255, 255 }).ToArray();
        var index = RowFixture.IndexRgba();
        var (rgba, w, h, mask, _, count) = MatcapRowCut.Cut(normal, 8, 8, index, 64, 64, 15, sub, true, null);
        Assert.Equal((64, 64), (w, h));
        Assert.Equal(expectedColumns * 64, count);
        Assert.Equal(expectedColumns > 0 ? 0 : 255, rgba[(5 * 64 + 2) * 4 + 2]);
        Assert.Equal(255, rgba[(5 * 64 + 60) * 4 + 2]);
        Assert.Equal(count, mask.Count(m => m));
    }

    // ── staleness ─────────────────────────────────────────────────────────

    [Theory]
    [Trait("Plan", "AC-matcap-row-12")]
    [Trait("Plan", "EDGE-matcap-row-35")]
    [Trait("Plan", "EDGE-matcap-row-45")]
    [Trait("Plan", "EDGE-matcap-row-39")]
    [InlineData("unchanged", false)]
    [InlineData("material", true)]
    [InlineData("normal", true)]
    [InlineData("new-container", true)]
    [InlineData("swap", true)]
    [InlineData("option-copy-with-cut", false)]
    [InlineData("option-copy-without-cut", true)]
    public void Detect_reports_RowStale_when_cut_and_material_resolve_from_different_sources(string change, bool stale)
    {
        using var f = new RowFixture(materialInOption: true);
        f.ColourOptions();
        Assert.True(f.Apply().Ok);
        var record = MatcapRecord.Read(f.ModRoot);
        var entry = record.Materials.Single();
        switch (change)
        {
            case "material":
                File.WriteAllBytes(MatcapWriter.Full(f.ModRoot, "mat/red.mtrl"), RowFixture.Material(tint: 9));
                break;
            case "normal":
                File.WriteAllBytes(MatcapWriter.Full(f.ModRoot, "tex/n.tex"), f.Tex(RowFixture.NormalRgba(blue: 200)));
                break;
            case "new-container":
                f.EditDefaults(files => files[RowFixture.MatPath()] = "mat\\red.mtrl");
                break;
            case "swap":
                f.EditDefaults(swaps => swaps[RowFixture.MatPath()] = "chara/x/other.mtrl", "FileSwaps");
                break;
            case "option-copy-with-cut":
            case "option-copy-without-cut":
                // Gear variants copies the Blue option's material to folder 2 — with our cut alongside (consistent: the
                // same source in a new folder) or without it (the new folder's cut triangles would draw no material).
                var blueCut = PenumbraModMeta.ReadContainerRedirects(f.ModRoot).First(r => r.Container.Option == "Blue" && r.GamePath.Contains("_mcut"));
                var add = new Dictionary<string, string> { [RowFixture.MatPath(2)] = "mat\\blue.mtrl" };
                if (change == "option-copy-with-cut") add[blueCut.GamePath.Replace("/v0001/", "/v0002/")] = blueCut.File;
                PenumbraModMeta.EditContainers(f.ModRoot, [new PenumbraModMeta.ContainerEdit(
                    new PenumbraModMeta.ContainerRef("Colour", "Blue"), add, new Dictionary<string, string>())]);
                break;
        }
        var states = MatcapWriter.Detect(f.ModRoot, p => f.Game.GetValueOrDefault(p));
        Assert.Equal(stale ? MatcapWriter.State.RowStale : MatcapWriter.State.Ok, states[entry.Name]);
    }

    [Fact]
    [Trait("Plan", "EDGE-matcap-row-35")]
    public void Default_data_material_game_cut_then_copied_key_is_stale()
    {
        // Folder 2 is served by the game at Apply (a game cut in default data); later default data serves the mod's file there.
        using var f = new RowFixture();
        f.Game[RowFixture.MatPath(2)] = RowFixture.Material(tint: 7);
        var target = MatcapPlan.TargetOf(RowFixture.ModelPath, out _)!;
        var env = f.Env();
        var (plan, refused) = MatcapRowCut.Make(new MatcapRowCut.Inputs(f.ModRoot, target, RowFixture.Mat, [1, 2], 1, env, 15,
            SubRowPick.B, "abc123", Cut));
        Assert.Null(refused);
        var entry = new MatcapRecord.MaterialEntry
        {
            Name = Mcap,
            Row = new MatcapRecord.RowRef
            {
                Cut = Cut, Fingerprint = new MatcapRecord.RowFingerprint { Folders = [.. plan!.Folders], SwapsSha8 = plan.SwapsSha8 },
                Files = plan.Files.Select((cf, k) => new MatcapRecord.RowFileRef
                {
                    File = $"Proteus Matcaps/cut{k}.mtrl", Source = cf.Source, Normal = cf.Normal, Index = cf.Index,
                }).ToList(),
            },
        };
        // Publish as the writer would.
        var adds = new Dictionary<PenumbraModMeta.ContainerRef, Dictionary<string, string>>();
        for (int k = 0; k < plan.Files.Count; k++)
            foreach (var (c, gp) in plan.Files[k].Redirects)
            {
                if (!adds.TryGetValue(c, out var d)) adds[c] = d = new(StringComparer.OrdinalIgnoreCase);
                d[gp] = entry.Row.Files[k].File;
            }
        PenumbraModMeta.EditContainers(f.ModRoot, adds.Select(kv => new PenumbraModMeta.ContainerEdit(kv.Key, kv.Value, new Dictionary<string, string>())).ToList());
        Assert.Null(MatcapRowCut.Stale(f.ModRoot, entry, target, p => f.Game.GetValueOrDefault(p)));

        f.EditDefaults(files => files[RowFixture.MatPath(2)] = "mat\\top_a.mtrl");
        Assert.NotNull(MatcapRowCut.Stale(f.ModRoot, entry, target, p => f.Game.GetValueOrDefault(p)));
    }

    // ── scheduling ────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "EDGE-matcap-row-44")]
    public void States_check_is_due_every_2s_when_idle_and_reruns_only_on_a_manifest_change()
    {
        Assert.True(MatcapStatesSchedule.Due(5000, 2999, idle: true));
        Assert.False(MatcapStatesSchedule.Due(5000, 3001, idle: true));
        Assert.False(MatcapStatesSchedule.Due(9000, 0, idle: false));
        var a = new Dictionary<string, long> { ["meta.json"] = 1 };
        Assert.False(MatcapStatesSchedule.Changed(a, new Dictionary<string, long> { ["meta.json"] = 1 }));
        Assert.True(MatcapStatesSchedule.Changed(a, new Dictionary<string, long> { ["meta.json"] = 2 }));
        Assert.True(MatcapStatesSchedule.Changed(null, a));
    }

    // ── the panel's rules ─────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-matcap-row-1")]
    [Trait("Plan", "EDGE-matcap-row-7")]
    [Trait("Plan", "EDGE-matcap-row-10")]
    [Trait("Plan", "EDGE-matcap-row-11")]
    [Trait("Plan", "EDGE-matcap-row-12")]
    public void Apply_rules_in_row_mode()
    {
        var s = Strings.Matcap;
        var choice = MatcapChoice.OfImage(new MatcapImage("gem.png", "gem.png", false));
        var record = new MatcapRecord();
        Assert.Null(MatcapPanelModel.ApplyDisabledReason(true, [Part(Mat)], choice, 2, record, row: 15));
        Assert.Equal(s.NeedParts, MatcapPanelModel.ApplyDisabledReason(true, [], choice, 2, record));
        Assert.Equal(s.RowNothingLeft, MatcapPanelModel.ApplyDisabledReason(true, [], choice, 2, record, row: 15));
        Assert.Equal(s.RowOneMaterial, MatcapPanelModel.ApplyDisabledReason(true, [Part(Mat), Part("/mt_b.mtrl", "1.2")], choice, 2, record, row: 15));
        Assert.NotNull(MatcapPanelModel.ApplyDisabledReason(true, [Part(Mat)], choice, 1, record, row: 15));
        Assert.Null(MatcapPanelModel.ApplyDisabledReason(true, [Part(Mat)], choice, 1, record));
        Assert.Equal(s.RowGameGear, MatcapPanelModel.ApplyDisabledReason(true, [Part(Mat)], choice, 2, record, row: 15, gamePiece: true));
    }

    [Fact]
    [Trait("Plan", "AC-matcap-row-12")]
    public void Row_stale_state_line()
    {
        Assert.Equal(Strings.Matcap.RowStale,
            MatcapPanelModel.EntryState(new MatcapRecord.MaterialEntry(), MatcapWriter.State.RowStale, true, true));
    }

    // ── preview pick ──────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-matcap-row-2")]
    [Trait("Plan", "EDGE-matcap-row-38")]
    public void Row_preview_pushes_the_cut_of_the_resolved_container_and_never_the_authors_paths()
    {
        using var f = new RowFixture(materialInOption: true);
        f.ColourOptions();
        var p = f.Prepare();
        Assert.True(p.Ok, p.Message);
        var plan = p.Request!.Materials.Single().Row!.Cut;
        var blue = plan.Files.Single(x => x.Source.Option == "Blue");
        var red = plan.Files.Single(x => x.Source.Option == "Red");

        var (resolved, used) = MatcapPreview.CutFiles(f.ModRoot, plan, Path.Combine(f.ModRoot, "mat", "blue.mtrl"));
        Assert.True(used);
        var file = Assert.Single(resolved);
        Assert.Equal(blue.Bytes, file.Bytes);
        Assert.All(resolved, x => Assert.All(x.GamePaths, gp => Assert.Contains("_mcut", gp)));

        var (guessed, usedGuess) = MatcapPreview.CutFiles(f.ModRoot, plan, null);
        Assert.False(usedGuess);
        Assert.Contains(Assert.Single(guessed).Bytes, new[] { red.Bytes, blue.Bytes });
    }

    // ── harness fields ────────────────────────────────────────────────────

    [Theory]
    [Trait("Plan", "AC-matcap-row-10")]
    [InlineData("{\"row\":15,\"sub\":\"B\"}", 15, true)]
    [InlineData("{\"row\":17}", 0, false)]
    [InlineData("{\"row\":\"x\"}", 0, false)]
    [InlineData("{\"sub\":\"A\"}", 0, false)]
    [InlineData("{\"material\":\"/mt_a.mtrl\"}", 0, false)]
    [InlineData("{\"row\":3,\"sub\":\"C\"}", 0, false)]
    [InlineData("{\"row\":3,\"material\":\"/mt_a.mtrl\"}", 3, true)]
    public void Matcap_route_validates_row_sub_material(string json, int row, bool ok)
    {
        using var doc = JsonDocument.Parse(json);
        var (r, _, _, bad) = HarnessRouter.RowFields(doc.RootElement);
        Assert.Equal(ok, bad == null);
        if (ok) Assert.Equal(row, r);
    }

    // ── wiring ────────────────────────────────────────────────────────────

    private static string Source(string rel)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "Proteus", rel))) dir = Path.GetDirectoryName(dir);
        return File.ReadAllText(Path.Combine(dir!, "Proteus", rel));
    }

    [Fact]
    [Trait("Plan", "AC-matcap-row-9")]
    public void Row_picker_has_no_prototype_or_raw_english_text()
    {
        var panel = Source("Gui/PartsPanel.Matcaps.cs");
        Assert.DoesNotContain("prototype", panel, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PROTOTYPE", Source("Services/Matcap/MatcapStudio.cs"));
        Assert.DoesNotContain("PROTOTYPE", Source("Services/ModelMaterialWriter.cs"));
        Assert.Contains("s.RowOnly", panel);
        Assert.DoesNotContain("\"Reading the colour rows", panel);
    }

    [Fact]
    [Trait("Plan", "AC-matcap-row-1")]
    [Trait("Plan", "AC-matcap-row-12")]
    [Trait("Plan", "EDGE-matcap-row-44")]
    public void Apply_preview_and_panel_share_MatcapApplyParts_and_Detect_runs_on_a_worker()
    {
        var panel = Source("Gui/PartsPanel.Matcaps.cs");
        Assert.DoesNotContain("MatcapPreviewParts", panel);
        Assert.True(panel.Split("MatcapApplyParts()").Length >= 5, "Apply, the preview build, the preview key and the pane all read MatcapApplyParts");
        var draw = panel[panel.IndexOf("private void DrawMatcaps()", StringComparison.Ordinal)..];
        draw = draw[..draw.IndexOf("private void StartMatcapApply", StringComparison.Ordinal)];
        var drawBody = draw[..draw.IndexOf("private void StartMatcapDetect", StringComparison.Ordinal)];
        Assert.DoesNotContain("MatcapWriter.Detect(", drawBody);
        Assert.DoesNotContain("GetLastWriteTime", drawBody);
        Assert.Contains("Task.Run(", draw[draw.IndexOf("private void StartMatcapDetect", StringComparison.Ordinal)..]);
    }
}
