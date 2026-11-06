using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// Where the row hover, the hidden rows and the two live highlighters meet ImGui and the game, pinned by reading the
/// source: ImGui can't run in a test and the highlighters touch live GPU textures. Textual and deliberately so: change
/// these with the code. The decisions themselves are unit-tested in <see cref="RowHighlightTests"/>,
/// <see cref="OverlayRowUseTests"/> and <see cref="RowPickerViewTests"/>.
/// </summary>
public class RowHoverWiringTests
{
    private static string ProteusDir([CallerFilePath] string here = "")
        => Path.Combine(Path.GetDirectoryName(here)!, "..", "Proteus");

    private static string Source(string relative) => File.ReadAllText(Path.Combine(ProteusDir(), relative));

    /// <summary>The body of the first member whose signature contains <paramref name="signature"/>, braces matched.</summary>
    private static string Method(string source, string signature)
    {
        int at = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at >= 0, $"no {signature}");
        int open = source.IndexOf('{', at), depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source[open..(i + 1)];
        }
        Assert.Fail($"unbalanced {signature}");
        return "";
    }

    /// <summary>An expression-bodied member: from its signature to the end of its statement.</summary>
    private static string Expr(string source, string signature)
    {
        int at = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at >= 0, $"no {signature}");
        int end = source.IndexOf(';', at);
        return source[at..(end + 1)];
    }

    private static string Squash(string s) => Regex.Replace(s, @"\s+", " ").Trim();

    private static int Count(string body, string needle)
    {
        int n = 0;
        for (int i = body.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = body.IndexOf(needle, i + needle.Length, StringComparison.Ordinal)) n++;
        return n;
    }

    private static string Editor => Source("Gui/ColorTableEditor.cs");
    private static string DrawRows => Method(Editor, "public static void DrawRows(");
    private static string Commit => Method(Editor, "internal static void CommitRowHover(");
    private static string Status => Source("Gui/StatusWindow.cs");
    private static string Gear => Source("Interop/ColorTableHighlighter.cs");
    private static string Skin => Source("Interop/SkinDiffuseGlow.cs");

    // ── the picker ──────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-row-hover-highlight-8")]
    [Trait("Plan", "AC-row-hover-highlight-9")]
    public void DrawRows_draws_the_visible_rows_and_the_checkbox_when_relevant()
    {
        var body = DrawRows;
        Assert.Contains("RowPickerView.Visible(usedRows, _showAllRows)", body);
        Assert.Contains("int row = visible[i];", body);
        Assert.DoesNotContain("for (int row = 1; row <= 16; row++)", body);

        int guard = body.IndexOf("if (RowPickerView.ToggleRelevant(usedRows))", StringComparison.Ordinal);
        Assert.True(guard >= 0);
        var guarded = Method(body[guard..], "if (RowPickerView.ToggleRelevant(usedRows))");
        Assert.Contains("ImGui.Checkbox($\"{cs.ShowAllRows}##showallrows_{idScope}\", ref _showAllRows)", guarded);

        Assert.Contains("private static bool _showAllRows;", Editor);
        Assert.DoesNotContain("ShowAllRows", Source("Configuration.cs"));
    }

    [Fact]
    [Trait("Plan", "AC-row-hover-highlight-10")]
    [Trait("Plan", "EDGE-row-hover-highlight-26")]
    public void DrawRows_lands_through_RowPickerView()
    {
        var body = DrawRows;
        Assert.Contains("selectedRow = RowPickerView.Land(selectedRow, visible, _showAllRows, InUse);", body);
        Assert.DoesNotContain("if (selectedRow is <= 0 or > 16)", body);
    }

    [Fact]
    [Trait("Plan", "AC-row-hover-highlight-9")]
    [Trait("Plan", "EDGE-row-hover-highlight-32")]
    [Trait("Plan", "EDGE-row-hover-highlight-34")]
    public void Both_overlay_paths_union_every_overlay_through_OverlayRowUse()
    {
        var option = Method(Status, "private (HashSet<int>? Rows, string? Column) ScanOptionRows(");
        Assert.Contains("activeOpt.Overlays.Select(o => ReadIndex(entry, o.Index, bodyType))", option);
        Assert.Contains("OverlayRowUse.Of(", option);
        Assert.DoesNotContain("FirstOrDefault(o => o.Index != null)", option);

        var simple = Method(Status, "private void DrawSimpleModEditor(");
        Assert.Contains("(entry.Metadata.Overlays ?? []).Select(o => ReadIndex(entry, o.Index, bodyType))", simple);
        Assert.Contains("OverlayRowUse.Of(", simple);

        Assert.DoesNotContain("OverlayRowFilter", Status);
        Assert.DoesNotContain("private static string? AgreedColumn", Status);
    }

    [Fact]
    [Trait("Plan", "AC-row-hover-highlight-9")]
    [Trait("Plan", "EDGE-row-hover-highlight-36")]
    [Trait("Plan", "EDGE-row-hover-highlight-37")]
    public void Index_scans_go_through_the_stamped_cache()
    {
        Assert.Contains("private readonly StampedScanCache _indexRowCache", Status);
        var read = Expr(Status, "private IndexRead ReadIndexFile(");
        Assert.Contains("_indexRowCache.Get(path, FileStamp.Of(path), () => ScanIndexFile(path, bodyType))", read);
        Assert.Contains("private ContentIndexTexture.Scan? ScanIndexFile(", Status);
        Assert.Contains("return null;", Method(Status, "private ContentIndexTexture.Scan? ScanIndexFile("));
    }

    // ── the hover ───────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-row-hover-highlight-1")]
    [Trait("Plan", "AC-row-hover-highlight-2")]
    [Trait("Plan", "EDGE-row-hover-highlight-11")]
    public void Every_row_button_offers_its_hover_with_the_shared_route()
    {
        var body = DrawRows;
        int button = body.IndexOf("ImGui.Button($\"#{row:D2}##row_{idScope}_{row}\", btn)", StringComparison.Ordinal);
        int hover = body.IndexOf("if (ImGui.IsItemHovered()) hoveredRow = row;", StringComparison.Ordinal);
        Assert.True(button >= 0 && hover > button, "the hover is read right after the row button");
        // Not gated on InUse: a dimmed row previews too.
        Assert.DoesNotContain("used && ImGui.IsItemHovered()) hoveredRow", body);

        int offer = body.IndexOf("_hoverFrame.Offer(new RowHoverOffer(", StringComparison.Ordinal);
        Assert.True(offer > hover);
        Assert.Contains("RowHighlightRoute.Pick(mode,", body[offer..]);
    }

    [Fact]
    [Trait("Plan", "AC-row-hover-highlight-7")]
    public void The_Glow_button_uses_the_same_route_and_still_pins()
    {
        var locator = Method(Source("Gui/ColorTableEditor.SubRowPanel.cs"), "private void DrawLocator(");
        Assert.Contains("RowHighlightRoute.Pick(mode,", locator);
        Assert.Contains("int gameRow = (row - 1) * 2 + (isA ? 0 : 1);", locator);
        Assert.Contains("Highlighter.SetTarget(shellMaterialLeaves, gameRow)", locator);
        Assert.Contains("Highlighter.Clear()", locator);
        Assert.Contains("Highlighter.IsTarget(shellMaterialLeaves, gameRow)", locator);
        Assert.Contains("SkinGlow.SetTarget(skinGlowTargets, row, isA)", locator);
        Assert.Contains("SkinGlow.Clear()", locator);
        Assert.Contains("SkinGlow.IsTarget(skinGlowTargets, row, isA)", locator);
        Assert.DoesNotContain("SetPreview", locator);
    }

    [Fact]
    [Trait("Plan", "AC-row-hover-highlight-4")]
    [Trait("Plan", "EDGE-row-hover-highlight-30")]
    public void Hover_never_writes_the_selected_row()
    {
        var body = DrawRows;
        var writes = Regex.Matches(body, @"selectedRow\s*=[^=]").Select(m => body.Substring(m.Index, Math.Min(60, body.Length - m.Index))).ToArray();
        Assert.Equal(2, writes.Length);
        Assert.Contains(writes, w => w.StartsWith("selectedRow = RowPickerView.Land(", StringComparison.Ordinal));
        Assert.Contains(writes, w => w.StartsWith("selectedRow = row;", StringComparison.Ordinal));
        Assert.DoesNotContain("selectedRow", Commit);
    }

    [Fact]
    [Trait("Plan", "AC-row-hover-highlight-1")]
    [Trait("Plan", "AC-row-hover-highlight-2")]
    [Trait("Plan", "EDGE-row-hover-highlight-15")]
    public void Commit_routes_by_Decide()
    {
        var body = Commit;
        Assert.Contains("_hoverFrame.Take()", body);
        Assert.Contains("RowHoverCommit.Decide(pluginEnabled, a?.Route, compositeBusy, skinNeedsBuild)", body);

        string Case(string label)
        {
            int at = body.IndexOf(label, StringComparison.Ordinal);
            Assert.True(at >= 0, label);
            int end = body.IndexOf("break;", at, StringComparison.Ordinal);
            return body[at..end];
        }
        var gear = Case("case HoverAction.Gear:");
        Assert.Contains("Highlighter?.SetPreview(a!.ShellLeaves!, ColorRowMask.Pair(a.Row))", gear);
        Assert.Contains("SkinGlow?.EndPreview()", gear);

        var skin = Case("case HoverAction.Skin:");
        Assert.Contains("SkinGlow?.SetPreview(a!.SkinTargets!, a.Row, skinNeedsBuild)", skin);
        Assert.Contains("Highlighter?.EndPreview()", skin);

        var wait = Case("case HoverAction.WaitSkin:");
        Assert.Contains("SkinGlow?.RenewPreview()", wait);
        Assert.Contains("Highlighter?.EndPreview()", wait);
        Assert.DoesNotContain("SetPreview", wait);

        var end = Case("default:");
        Assert.Contains("Highlighter?.EndPreview()", end);
        Assert.Contains("SkinGlow?.EndPreview()", end);
    }

    [Fact]
    [Trait("Plan", "EDGE-row-hover-highlight-15")]
    public void One_skin_build_decision_per_frame()
    {
        var body = Commit;
        Assert.Equal(1, Count(body, "PreviewNeedsBuild("));
        Assert.Contains("bool skinNeedsBuild =", body);
        Assert.Contains("compositeBusy, skinNeedsBuild))", body);   // into Decide
        Assert.Contains("a.Row, skinNeedsBuild);", body);   // the same value into SetPreview

        var setPreview = Method(Skin, "public void SetPreview(");
        int ifBuild = setPreview.IndexOf("if (build)", StringComparison.Ordinal);
        Assert.True(ifBuild >= 0);
        var inside = Method(setPreview[ifBuild..], "if (build)");
        Assert.Equal(1, Count(setPreview, "Start("));
        Assert.Contains("Start(st);", inside);
        Assert.DoesNotContain("Task.Run(", setPreview);
    }

    [Fact]
    [Trait("Plan", "AC-row-hover-highlight-3")]
    [Trait("Plan", "AC-row-hover-highlight-6")]
    [Trait("Plan", "EDGE-row-hover-highlight-6")]
    [Trait("Plan", "EDGE-row-hover-highlight-12")]
    public void Commit_runs_every_frame_with_both_gates()
    {
        var body = Method(Status, "private void DrawColorWindow(");
        const string commit =
            "ColorTableEditor.CommitRowHover(RowHighlightClock.NowMs(), config.PluginEnabled, compositor.CompositeBusy);";
        int begin = body.IndexOf("if (ImGui.Begin(", StringComparison.Ordinal);
        int end = body.IndexOf("ImGui.End();", StringComparison.Ordinal);
        int at = body.IndexOf(commit, StringComparison.Ordinal);
        Assert.True(begin >= 0 && end > begin && at > end, "committed after End, outside Begin");
    }

    [Fact]
    [Trait("Plan", "AC-row-hover-highlight-5")]
    public void Closing_the_window_clears_both_highlighters_and_the_hover()
    {
        var window = Method(Status, "private void DrawColorWindow(");
        Assert.Equal(2, Count(window, "ClearColorHighlights(); return;"));
        Assert.DoesNotContain("ColorTableEditor.Highlighter?.Clear(); return;", window);

        // Closing the main window hides the Colors window (drawn from its PostDraw), so it clears them too.
        Assert.Contains("ClearColorHighlights();", Method(Status, "public override void OnClose("));

        var clear = Method(Status, "private static void ClearColorHighlights(");
        Assert.Contains("ColorTableEditor.Highlighter?.Clear();", clear);
        Assert.Contains("ColorTableEditor.ResetRowHover();", clear);
        Assert.Contains("if (ColorTableEditor.SkinGlow?.IsActive == true) ColorTableEditor.SkinGlow.Clear();", clear);

        var reset = Method(Editor, "internal static void ResetRowHover(");
        Assert.Contains("_dwell.Reset();", reset);
        Assert.Contains("Highlighter?.EndPreview();", reset);
        Assert.Contains("SkinGlow?.EndPreview();", reset);
    }

    [Fact]
    [Trait("Plan", "AC-row-hover-highlight-5")]
    public void Skin_IsActive_covers_pin_preview_and_held_slots()
        => Assert.Contains("public bool IsActive => _pinned != null || _preview != null || _applied.Count > 0;", Skin);

    [Fact]
    [Trait("Plan", "AC-row-hover-highlight-6")]
    public void Hover_adds_no_recomposite()
    {
        Assert.DoesNotContain("TriggerRecomposite(", DrawRows);
        Assert.DoesNotContain("TriggerRecomposite(", Commit);
    }

    // ── the gear highlighter ────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "EDGE-row-hover-highlight-5")]
    [Trait("Plan", "AC-row-hover-highlight-7")]
    public void A_recomposite_still_clears_the_gear_highlighter()
    {
        var trigger = Source("Services/CompositorService.Trigger.cs");
        int clear = trigger.IndexOf("Highlighter?.Clear();", StringComparison.Ordinal);
        int gate = trigger.IndexOf("recompositeGate.Next()", StringComparison.Ordinal);
        Assert.True(clear >= 0 && gate > clear);

        var gearClear = Method(Gear, "public void Clear(");
        Assert.Contains("_pinned  = null;", gearClear);
        Assert.Contains("_preview = null;", gearClear);
        Assert.Contains("Ghost?.Clear();", gearClear);
    }

    [Fact]
    [Trait("Plan", "EDGE-row-hover-highlight-29")]
    public void Gear_Dispose_goes_through_Clear()
    {
        var dispose = Squash(Method(Gear, "public void Dispose("));
        Assert.StartsWith("{ Clear(); Apply(objects.LocalPlayer?.Address ?? 0);", dispose);
        Assert.Contains("framework.Update -= OnFramework;", dispose);
    }

    [Fact]
    [Trait("Plan", "EDGE-row-hover-highlight-20")]
    public void Previews_never_touch_the_ghost()
    {
        foreach (var src in new[] { Gear, Skin })
        {
            Assert.DoesNotContain("Ghost", Method(src, "public void SetPreview("));
            Assert.DoesNotContain("Ghost", Method(src, "private void OnFramework("));
        }
        Assert.DoesNotContain("Ghost", Expr(Gear, "public void EndPreview()"));
        Assert.DoesNotContain("Ghost", Method(Skin, "public void EndPreview("));
        Assert.DoesNotContain("Ghost", Expr(Skin, "public void RenewPreview()"));
        Assert.Equal(1, Count(Gear, "Ghost?.GhostAbove("));
        Assert.Contains("Ghost?.GhostAbove(", Method(Gear, "public void SetTarget("));
        Assert.Equal(1, Count(Skin, "Ghost?.GhostAbove("));
        Assert.Contains("Ghost?.GhostAbove(", Method(Skin, "public void SetTarget("));
    }

    [Fact]
    [Trait("Plan", "EDGE-row-hover-highlight-8")]
    public void IsHighlighting_reads_what_Apply_painted_and_the_highlighter_ticks_first()
    {
        Assert.Contains("public bool IsHighlighting(string leaf) => _appliedLeaves.Contains(leaf);", Gear);
        var plugin = Source("Plugin.cs");
        int gear = plugin.IndexOf("new ColorTableHighlighter(", StringComparison.Ordinal);
        int applier = plugin.IndexOf("new ShellColorsetApplier(", StringComparison.Ordinal);
        Assert.True(gear >= 0 && applier > gear, "the highlighter subscribes to Framework.Update first");
    }

    [Fact]
    [Trait("Plan", "AC-row-hover-highlight-4")]
    [Trait("Plan", "EDGE-row-hover-highlight-9")]
    public void IsTarget_reads_the_pin_only()
    {
        foreach (var src in new[] { Gear, Skin })
        {
            var isTarget = Method(src, "public bool IsTarget(");
            Assert.Contains("_pinned", isTarget);
            Assert.DoesNotContain("_preview", isTarget);
        }
    }

    // ── the skin highlighter ────────────────────────────────────────────────

    /// <summary>
    /// SkinDiffuseGlow.Apply as it stood before the hover (test d39ccf9d, lines 167–216) with its one intended change:
    /// where it reads its targets. The slot swaps — swap first, then DecRef; never DecRef a pruned slot; leave what's
    /// showing while a build isn't ready — must stay exactly as they were.
    /// </summary>
    private const string GoldenApply = """
        {
            if (addr == 0) { _applied.Clear(); return; }   // character gone; textures went with it
            var targets = EffectiveTargets();
            if (targets.Count == 0 && _applied.Count == 0) return;

            var seen = new HashSet<nint>();
            bool walked = ForEachProteusDiffuse(addr, (slot, name) =>
            {
                seen.Add(slot);
                var cur = *(nint*)slot;

                if (targets.TryGetValue(Normalize(name), out var t))
                {
                    bool haveApplied = _applied.TryGetValue(slot, out var ap);

                    // Already showing exactly this target's texture? Nothing to do.
                    if (haveApplied && ap.Applied == t && cur == ap.Ours) return;

                    var bgra = t.Bgra;
                    if (bgra == null) return;          // desired build not ready — leave what's showing
                    var tex = CreateTex(bgra, t.W, t.H);
                    if (tex == 0) return;

                    // Swap FIRST, then release the old highlight (only if the slot really still held ours) —
                    // never DecRef a texture that is still the live slot value, or the render thread can sample
                    // freed memory. Keep the true game original across a row change; else the displaced value is it.
                    var old = Interlocked.Exchange(ref *(nint*)slot, tex);
                    bool swappedOurs = haveApplied && old == ap.Ours;
                    if (swappedOurs) ((Texture*)ap.Ours)->DecRef();
                    _applied[slot] = (tex, swappedOurs ? ap.Original : old, t);
                }
                else if (_applied.TryGetValue(slot, out var ap))
                {
                    if (cur == ap.Ours)                // still ours → restore original, release ours
                    {
                        Interlocked.Exchange(ref *(nint*)slot, ap.Original);
                        ((Texture*)ap.Ours)->DecRef();
                    }
                    _applied.Remove(slot);
                }
            });

            // Prune only when the traversal actually ran: an applied slot it visited-but-didn't-match had its
            // model rebuilt (the game already freed our texture with the old handle, so no DecRef — that would
            // crash). If it couldn't walk this frame (mid-redraw / not a CharacterBase), keep tracking for next.
            if (walked && _applied.Count > 0)
                foreach (var k in _applied.Keys.Where(k => !seen.Contains(k)).ToList())
                    _applied.Remove(k);
        }
        """;

    [Fact]
    [Trait("Plan", "EDGE-row-hover-highlight-19")]
    [Trait("Plan", "EDGE-row-hover-highlight-13")]
    [Trait("Plan", "EDGE-row-hover-highlight-29")]
    public void SkinDiffuseGlow_Apply_is_golden_but_for_its_targets_source()
    {
        Assert.Equal(Squash(GoldenApply), Squash(Method(Skin, "private void Apply(nint addr)")));

        var dispose = Squash(Method(Skin, "public void Dispose("));
        Assert.Equal("{ framework.Update -= OnFramework; Clear(); Apply(objects.LocalPlayer?.Address ?? 0); // restore anything still swapped }",
            dispose);
    }

    [Fact]
    [Trait("Plan", "AC-row-hover-highlight-3")]
    [Trait("Plan", "EDGE-row-hover-highlight-19")]
    public void A_failed_build_never_leaves_another_rows_texture_showing()
    {
        // Apply leaves what's showing while a target's build isn't ready. A FAILED diffuse — of the pin (which never
        // retries) or of the preview — is left out of what Apply sees, so its slot is restored instead of keeping another
        // row's gold (the last preview's after a hover, or the previous hovered row's while this one fails).
        var effective = Method(Skin, "private Dictionary<string, Target> EffectiveTargets(");
        int pick = effective.IndexOf("var st = _preview ?? _pinned;", StringComparison.Ordinal);
        int count = effective.IndexOf("if (t.Failed) failed++;", StringComparison.Ordinal);
        int remove = effective.IndexOf("if (t.Failed) live.Remove(path);", StringComparison.Ordinal);
        Assert.True(pick >= 0 && count > pick && remove > count);
        Assert.DoesNotContain("return v.ByPath", effective);
    }

    [Fact]
    [Trait("Plan", "EDGE-row-hover-highlight-7")]
    [Trait("Plan", "EDGE-row-hover-highlight-13")]
    public void Expiry_counts_ticks_in_OnFramework()
    {
        foreach (var src in new[] { Gear, Skin })
        {
            var tick = Method(src, "private void OnFramework(");
            int t = tick.IndexOf("_keep.Tick();", StringComparison.Ordinal);
            int e = tick.IndexOf("_keep.Expired(RowHighlightClock.NowMs())", StringComparison.Ordinal);
            int a = tick.IndexOf("Apply(", StringComparison.Ordinal);
            Assert.True(t >= 0 && e > t && a > e);
            Assert.Contains("private readonly PreviewKeepAlive _keep = new();", src);
            Assert.DoesNotContain("Environment.TickCount64", src);
        }
        Assert.Contains("public sealed class PreviewKeepAlive", Source("Services/RowHighlight.cs"));
    }

    [Fact]
    [Trait("Plan", "EDGE-row-hover-highlight-16")]
    [Trait("Plan", "EDGE-row-hover-highlight-2")]
    public void Skin_build_checks_completeness_stamp_and_abandonment()
    {
        var build = Method(Skin, "private void Build(");
        int abandoned1 = build.IndexOf("if (t.Abandoned) return;", StringComparison.Ordinal);
        int complete = build.IndexOf("TextureLoader.IsCompleteTex(t.DiskPath)", StringComparison.Ordinal);
        int s0 = build.IndexOf("var s0 = FileStamp.Of(t.DiskPath);", StringComparison.Ordinal);
        int decode = build.IndexOf("textures.LoadTexShared(t.DiskPath)", StringComparison.Ordinal);
        int s1 = build.IndexOf("FileStamp.Of(t.DiskPath) != s0", StringComparison.Ordinal);
        int abandoned2 = build.IndexOf("if (t.Abandoned) return;", decode, StringComparison.Ordinal);
        int paint = build.IndexOf("Paint(", StringComparison.Ordinal);
        Assert.True(abandoned1 >= 0 && complete > abandoned1 && s0 > complete && decode > s0 && s1 > decode
                    && abandoned2 > s1 && paint > abandoned2, "checks in order around the decode");
        Assert.DoesNotContain("LoadTexAsRgba(", build);
    }

    [Fact]
    [Trait("Plan", "EDGE-row-hover-highlight-16")]
    [Trait("Plan", "EDGE-row-hover-highlight-38")]
    public void Fail_publishes_the_time_before_the_flag_and_the_catch_fails()
    {
        var fail = Method(Skin, "private static void Fail(");
        int at = fail.IndexOf("t.FailedAtMs = RowHighlightClock.NowMs();", StringComparison.Ordinal);
        int flag = fail.IndexOf("t.Failed = true;", StringComparison.Ordinal);
        Assert.True(at >= 0 && flag > at);

        var build = Method(Skin, "private void Build(");
        int c = build.IndexOf("catch (Exception ex)", StringComparison.Ordinal);
        Assert.True(c >= 0);
        Assert.Contains("Fail(t);", Method(build[c..], "catch (Exception ex)"));
    }

    [Fact]
    [Trait("Plan", "EDGE-row-hover-highlight-38")]
    [Trait("Plan", "EDGE-row-hover-highlight-17")]
    public void Skin_preview_has_no_pin_sharing_and_counts_attempts_per_hover()
    {
        var setPreview = Method(Skin, "public void SetPreview(");
        Assert.DoesNotContain("_pinned", setPreview);
        Assert.Contains("same ? old!.Attempt + 1 : 1", setPreview);
        var end = Method(Skin, "public void EndPreview(");
        Assert.Contains("_preview = null;", end);
        Assert.Contains("Abandon(v);", end);
    }

    [Fact]
    [Trait("Plan", "AC-row-hover-highlight-2")]
    public void Skin_texture_create_and_build_are_timed()
    {
        var create = Method(Skin, "private nint CreateTex(");
        Assert.Contains("Stopwatch.StartNew()", create);
        Assert.Contains("log.Debug(\"[SkinGlow] texture {0}x{1} created in {2} ms\"", create);
        var build = Method(Skin, "private void Build(");
        Assert.Contains("GC.CollectionCount(2)", build);
    }

    [Fact]
    [Trait("Plan", "AC-row-hover-highlight-2")]
    public void Skin_reads_the_diffuse_through_the_shared_decode_cache()
    {
        var shared = Method(Source("Services/TextureLoader.cs"), "public (byte[] Rgba, int Width, int Height)? LoadTexShared(");
        Assert.Contains("DiskKey(\"TEXS\", diskPath)", shared);
        Assert.Contains("GetOrDecode(key, () => LoadTexAsRgba(diskPath))", shared);
    }
}
