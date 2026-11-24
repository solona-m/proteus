using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// Every Undo that throws away an edit saved into a mod goes through <c>ProteusStyle.ArmedButton</c> (Ctrl or Shift to
/// arm). The panels need ImGui, so the wiring is read from their source, as <see cref="PartsPanelVanillaCopyTests"/>
/// does; the rules themselves are in <see cref="ArmedActionTests"/>.
/// </summary>
public class ArmedUndoWiringTests
{
    private static string Repo([CallerFilePath] string here = "") => Path.Combine(Path.GetDirectoryName(here)!, "..");
    private static string Gui(string file) => File.ReadAllText(Path.Combine(Repo(), "Proteus", "Gui", file)).Replace("\r\n", "\n");

    /// <summary>The body of the first method whose signature contains <paramref name="signature"/>, braces matched.</summary>
    private static string Method(string source, string signature)
    {
        int at = source.IndexOf(signature, System.StringComparison.Ordinal);
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

    /// <summary>
    /// The spans governed by each <c>using (ImRaii.Disabled(cond))</c> in <paramref name="body"/>: a braced block, or
    /// the single statement after it (up to its first semicolon).
    /// </summary>
    private static (int Start, int End)[] DisabledScopes(string body, string cond)
    {
        var head = $"using (ImRaii.Disabled({cond}))";
        var spans = new System.Collections.Generic.List<(int, int)>();
        for (int at = body.IndexOf(head, System.StringComparison.Ordinal); at >= 0;
             at = body.IndexOf(head, at + 1, System.StringComparison.Ordinal))
        {
            int after = at + head.Length;
            int next = after;
            while (char.IsWhiteSpace(body[next])) next++;
            if (body[next] == '{') spans.Add((next, next + Method(body[next..], "{").Length));
            else spans.Add((after, body.IndexOf(';', after)));
        }
        return spans.ToArray();
    }

    private static void ArmedCallOutsideItsDisabled(string body, string call, string cond)
    {
        int at = body.IndexOf(call, System.StringComparison.Ordinal);
        Assert.True(at >= 0, $"no {call}");
        foreach (var (s, e) in DisabledScopes(body, cond))
            Assert.False(at > s && at < e, $"{call} sits inside using (ImRaii.Disabled({cond}))");
    }

    private static string Style => Method(Gui("ProteusStyle.cs"), "public static bool ArmedButton(");

    [Fact]
    [Trait("Plan", "AC-ctrl-click-undo-6")]
    [Trait("Plan", "EDGE-ctrl-click-undo-9")]
    [Trait("Plan", "EDGE-ctrl-click-undo-10")]
    [Trait("Plan", "EDGE-ctrl-click-undo-11")]
    public void The_helper_dims_only_when_unarmed_disables_on_blocked_or_unarmed_and_returns_through_Clicked()
    {
        var m = Style;
        Assert.Contains("ArmedAction.Armed(io.KeyCtrl, io.KeyShift)", m);
        Assert.Matches(@"ImRaii\.PushStyle\(ImGuiStyleVar\.Alpha, ImGui\.GetStyle\(\)\.Alpha \* 0\.5f, !armed\)", m);
        Assert.Contains("ImRaii.Disabled(blocked || !armed)", m);
        Assert.Contains("ReasonTooltip(ArmedAction.Tooltip(tip, blocked ? blockedTip : null, Localization.Strings.Parts.BrushRevertArmTip))", m);
        var returns = Regex.Matches(m, @"\breturn\b[^;]*;").Select(x => x.Value).ToArray();
        Assert.Equal(["return ArmedAction.Clicked(pressed, armed, blocked);"], returns);
    }

    [Fact]
    [Trait("Plan", "AC-ctrl-click-undo-1")]
    public void Restore_original_models_is_armed()
    {
        Assert.Contains("if (ProteusStyle.ArmedButton(ps.RevertBtn)) Revert();", Method(Gui("PartsPanel.cs"), "private void DrawExisting()"));
        Assert.DoesNotContain("ImGui.Button(ps.RevertBtn", Gui("PartsPanel.cs"));
    }

    [Fact]
    [Trait("Plan", "AC-ctrl-click-undo-2")]
    [Trait("Plan", "AC-ctrl-click-undo-5")]
    [Trait("Plan", "EDGE-ctrl-click-undo-4")]
    [Trait("Plan", "EDGE-ctrl-click-undo-5")]
    [Trait("Plan", "EDGE-ctrl-click-undo-13")]
    public void Hair_settings_undos_are_armed()
    {
        var src = Gui("HairPortSettingsPanel.cs");
        var draw = Method(src, "public void Draw()");
        Assert.Contains("ProteusStyle.ArmedButton(s.Undo, s.UndoTip, blocked: view.Busy, blockedTip: s.Working)) watcher.Undo();", draw);
        Assert.Matches(@"ProteusStyle\.ArmedButton\(string\.Format\(s\.UndoAllFmt, view\.PortsInMod\), blocked: view\.Busy, blockedTip: s\.Working\)\)\s*watcher\.UndoAll\(\);", draw);

        var check = Method(src, "private void DrawCheck(");
        Assert.Contains("ProteusStyle.ArmedButton(s.Undo, s.UndoTip, blocked: !can, small: true)) watcher.UndoRow(row);", check);
        // Each row's buttons share one label, told apart by the row's id.
        Assert.True(check.IndexOf("ImGui.PushID(", System.StringComparison.Ordinal) < check.IndexOf("ArmedButton(", System.StringComparison.Ordinal));

        Assert.DoesNotContain("ImGui.Button(s.Undo)", src);
        Assert.DoesNotContain("ImGui.SmallButton(s.Undo)", src);
        Assert.DoesNotContain("ImGui.Button(string.Format(s.UndoAllFmt", src);
    }

    [Fact]
    [Trait("Plan", "AC-ctrl-click-undo-3")]
    [Trait("Plan", "EDGE-ctrl-click-undo-6")]
    [Trait("Plan", "EDGE-ctrl-click-undo-9")]
    public void Hat_undos_are_armed()
    {
        var src = Gui("HatCompatPanel.cs");
        var all = Method(src, "private void DrawUndoAll()");
        Assert.Contains("ProteusStyle.ArmedButton(string.Format(s.UndoAllFmt, count), s.UndoAllTip)) watcher.RevertAll();", all);
        Assert.DoesNotContain("IsItemHovered()", all);
        Assert.Contains("ProteusStyle.ArmedButton(s.Undo)) watcher.Revert();", Method(src, "private void DrawState()"));
    }

    [Fact]
    [Trait("Plan", "AC-ctrl-click-undo-4")]
    [Trait("Plan", "AC-ctrl-click-undo-5")]
    [Trait("Plan", "EDGE-ctrl-click-undo-8")]
    public void Matcap_undo_and_remove_ports_are_armed()
    {
        var existing = Method(Gui("MatcapPanel.cs"), "private void DrawExisting(Context ctx)");
        Assert.Contains("string.Format(s.UndoGroupFmt, string.Join(\", \", ctx.GroupFiles)) : null;", existing);
        Assert.Contains("ProteusStyle.ArmedButton(s.Undo, groupTip, blocked: ctx.Busy, blockedTip: Strings.Matcap.Working)) ctx.Undo();", existing);
        Assert.Contains("ImGui.Button(s.Forget)", existing);

        Assert.Contains("if (ProteusStyle.ArmedButton(s.Remove)) Remove(modRoot, source);", Gui("HairPortPanel.cs"));
    }

    [Theory]
    [Trait("Plan", "AC-ctrl-click-undo-5")]
    [Trait("Plan", "EDGE-ctrl-click-undo-3")]
    [Trait("Plan", "EDGE-ctrl-click-undo-4")]
    [InlineData("ShaderUpgradePanel.cs", "GearShaders", "UpgradeWornNow()")]
    [InlineData("VariantRepointPanel.cs", "GearVariants", "RepointWornNow()")]
    public void Gear_undo_for_what_I_am_wearing_is_armed(string file, string prefix, string doNow)
    {
        var src = Gui(file);
        var draw = Method(src, "public void Draw()");
        Assert.Matches($@"ProteusStyle\.ArmedButton\(s\.{prefix}UndoWorn, s\.{prefix}UndoWornTip, blocked: view\.Busy, blockedTip: s\.{prefix}Busy\)\)\s*watcher\.UndoWorn\(\);", draw);
        // The do-it-now button keeps its busy block; only the Undo moved out of it.
        var scopes = DisabledScopes(draw, "view.Busy");
        Assert.Single(scopes);
        Assert.Contains(doNow, draw[scopes[0].Start..scopes[0].End]);
        Assert.DoesNotContain($"ImGui.Button(s.{prefix}UndoWorn", src);
    }

    [Theory]
    [Trait("Plan", "AC-ctrl-click-undo-5")]
    [Trait("Plan", "EDGE-ctrl-click-undo-10")]
    [InlineData("HairPortSettingsPanel.cs", "public void Draw()", "ArmedButton(s.Undo, s.UndoTip, blocked: view.Busy", "view.Busy")]
    [InlineData("HairPortSettingsPanel.cs", "public void Draw()", "ArmedButton(string.Format(s.UndoAllFmt", "view.Busy")]
    [InlineData("HairPortSettingsPanel.cs", "private void DrawCheck(", "ArmedButton(s.Undo, s.UndoTip, blocked: !can", "!can")]
    [InlineData("MatcapPanel.cs", "private void DrawExisting(Context ctx)", "ArmedButton(s.Undo, groupTip", "ctx.Busy")]
    [InlineData("ShaderUpgradePanel.cs", "public void Draw()", "ArmedButton(s.GearShadersUndoWorn", "view.Busy")]
    [InlineData("VariantRepointPanel.cs", "public void Draw()", "ArmedButton(s.GearVariantsUndoWorn", "view.Busy")]
    [InlineData("BodyRetargetPanel.cs", "private void DrawSaved(", "ArmedButton(ps.RetargetUndo", "saveTask != null")]
    public void No_armed_site_sits_inside_a_disabled_on_its_own_blocked_condition(string file, string method, string call, string cond)
        => ArmedCallOutsideItsDisabled(Method(Gui(file), method), call, cond);

    [Fact]
    [Trait("Plan", "AC-ctrl-click-undo-8")]
    public void Existing_armed_buttons_use_the_helper_or_stay_as_they_were()
    {
        Assert.Contains("ProteusStyle.ArmedButton(ps.BrushRevert, ps.BrushRevertTip, size: FullWidth())) RevertBrush();", Gui("PartsPanel.cs"));
        Assert.Matches(@"ProteusStyle\.ArmedButton\(ps\.RetargetUndo, blocked: saveTask != null, blockedTip: ps\.RetargetWorking, size: FullWidth\(\)\)\)\s*StartUndo\(ctx, saved\);",
                       Method(Gui("BodyRetargetPanel.cs"), "private void DrawSaved("));

        var auto = Gui("AutoRefitPanel.cs");
        Assert.Contains("bool armed = ImGui.GetIO().KeyCtrl || ImGui.GetIO().KeyShift;", auto);
        Assert.Contains("using (ImRaii.Disabled(!armed || watcher.Undoing))", auto);
        Assert.Contains("var armed = !isPack && ImGui.GetIO().KeyCtrl;", Gui("PresetBar.cs"));
        Assert.Contains("bool armed = !disabled && ImGui.GetIO().KeyCtrl;", Gui("ColorTableEditor.GlowFooter.cs"));
    }

    [Fact]
    [Trait("Plan", "AC-ctrl-click-undo-8")]
    [Trait("Plan", "EDGE-ctrl-click-undo-14")]
    public void Step_undo_and_two_click_confirm_are_untouched()
    {
        var parts = Gui("PartsPanel.cs");
        Assert.Contains("ImGui.Button(PartTool ? ps.MoveUndo : ps.BrushUndo, FullWidth())", parts);
        Assert.Contains("ImGui.Button(ps.BrushReset, FullWidth())", parts);

        var undoChecked = Method(Gui("HairPortSettingsPanel.cs"), "private void DrawUndoChecked(");
        Assert.Contains("confirmUndoChecked = true", undoChecked);
        Assert.Contains("watcher.UndoChecked()", undoChecked);
        Assert.DoesNotContain("ArmedButton", undoChecked);

        Assert.DoesNotContain("IsKeyPressed", Style);
        Assert.DoesNotContain("IsKeyDown", Style);
    }

    [Fact]
    [Trait("Plan", "AC-ctrl-click-undo-1")]
    [Trait("Plan", "AC-ctrl-click-undo-2")]
    [Trait("Plan", "AC-ctrl-click-undo-3")]
    [Trait("Plan", "AC-ctrl-click-undo-4")]
    [Trait("Plan", "AC-ctrl-click-undo-5")]
    public void No_destructive_undo_is_a_bare_button()
    {
        string[] everywhere = ["ps.RevertBtn", "ps.BrushRevert", "ps.RetargetUndo", "s.GearShadersUndoWorn", "s.GearVariantsUndoWorn"];
        string[] local = ["s.Undo", "string.Format(s.UndoAllFmt", "s.Remove"];
        string[] localFiles = ["HairPortSettingsPanel.cs", "HatCompatPanel.cs", "MatcapPanel.cs", "HairPortPanel.cs"];
        foreach (var path in Directory.GetFiles(Path.Combine(Repo(), "Proteus", "Gui"), "*.cs"))
        {
            var src = File.ReadAllText(path);
            var name = Path.GetFileName(path);
            var labels = localFiles.Contains(name) ? everywhere.Concat(local) : everywhere;
            foreach (var label in labels)
                foreach (var button in new[] { "ImGui.Button(", "ImGui.SmallButton(" })
                {
                    // A label followed by ')' or ',' — never s.UndoTip or s.UndoAllTip matching s.Undo.
                    var rx = new Regex(Regex.Escape(button + label) + @"(?![A-Za-z0-9_])");
                    Assert.False(rx.IsMatch(src), $"{name}: {button}{label}");
                }
        }
    }

    private const string EnglishMark = "(hold Ctrl or Shift and click)";

    private static readonly (string Lang, string Mark)[] Marks =
    [
        ("de", "(Strg oder Shift gedrückt halten und klicken)"),
        ("es", "(mantén Ctrl o Mayús y haz clic)"),
        ("fr", "(maintenez Ctrl ou Maj et cliquez)"),
        ("ja", "（CtrlまたはShiftを押しながらクリック）"),
        ("ko", "(Ctrl이나 Shift를 누른 채로 클릭)"),
        ("ru", "(щелчок с зажатым Ctrl или Shift)"),
        ("zh", "（按住 Ctrl 或 Shift 并点击）"),
    ];

    private static int Count(string text, string mark) => Regex.Matches(text, Regex.Escape(mark)).Count;

    [Fact]
    [Trait("Plan", "AC-ctrl-click-undo-7")]
    public void Readme_marks_every_armed_undo()
    {
        var en = File.ReadAllText(Path.Combine(Repo(), "README.md")).Replace("\r\n", "\n").Split('\n');
        string[] named =
        [
            "**Undo matcaps on this model**", "**Remove the last refit**", "**Undo — restore the original models**",
            "Each has its own **Undo**", "**Undo all** puts back every hairstyle", "puts the author's files back and leaves those pieces alone",
            "takes Proteus's entries back out and leaves those pieces alone",
        ];
        foreach (var n in named)
        {
            var line = en.SingleOrDefault(l => l.Contains(n));
            Assert.True(line != null, $"README.md has no single line with {n}");
            Assert.Contains(EnglishMark, line);
        }
        int english = en.Sum(l => Count(l, EnglishMark));
        Assert.Equal(9, english);

        foreach (var (lang, mark) in Marks)
        {
            var text = File.ReadAllText(Path.Combine(Repo(), "docs", $"README.{lang}.md"));
            Assert.True(english == Count(text, mark), $"README.{lang}.md has {Count(text, mark)} arm marks, English {english}");
        }
    }
}
