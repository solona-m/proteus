using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// Where a design binding may be written and when the active design is restored again, pinned by reading the source:
/// <c>DesignBindingService</c> cannot be built in a test (its constructor reaches <c>Plugin.ClientState</c>). The
/// decisions themselves are unit-tested in <see cref="DesignBindingTests"/> (<c>DecideActiveApply</c>,
/// <c>IsReapplyOfActive</c>); these only check the call sites. Textual and deliberately so: change these with the code.
/// </summary>
public class DesignBindingWiringTests
{
    private static string RepoDir([CallerFilePath] string here = "") => Path.Combine(Path.GetDirectoryName(here)!, "..");

    private static string Source(string relative) => File.ReadAllText(Path.Combine(RepoDir(), "Proteus", relative));

    private static string Service() => Source("Services/DesignBindingService.cs");

    /// <summary>The body of the first block whose header contains <paramref name="signature"/>, braces matched.</summary>
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

    /// <summary>Every call to <paramref name="callee"/> in <paramref name="body"/>, its arguments included.</summary>
    private static string[] Calls(string body, string callee)
        => Regex.Matches(body, @"(?<![\w.])" + Regex.Escape(callee) + @"\((?>[^()]+|\((?<d>)|\)(?<-d>))*(?(d)(?!))\)")
                .Select(m => m.Value).ToArray();

    private static string HandleStateFinalized() => Method(Service(), "private void HandleStateFinalized(");

    private static string ActiveBranch()
        => Method(Method(Service(), "private void EvaluateAppliedDesign("), "if (activeDesignId == pick)");

    [Fact]
    [Trait("Plan", "AC-binding-reapply-6")]
    public void Only_DesignApplied_is_explicit()
    {
        var call = Assert.Single(Calls(HandleStateFinalized(), "EvaluateAppliedDesign"));
        Assert.Contains("explicitApply: type is StateFinalizationType.DesignApplied", call);
    }

    [Fact]
    [Trait("Plan", "AC-binding-reapply-6")]
    public void Ambient_paths_never_restore_or_capture()
    {
        foreach (var sig in new[] { "private void AdoptRemembered(", "private void RearmForNextLogin(", "private bool SuspendForPluginDisabled(" })
        {
            var body = Method(Service(), sig);
            Assert.Empty(Calls(body, "Restore"));
            Assert.Empty(Calls(body, "Capture"));
        }
        // A relogin re-marks the still-published design active; only a fresh boot adopts from the store.
        Assert.Contains("if (!stillPublished) AdoptOverrides(", Method(Service(), "private void AdoptRemembered("));
    }

    [Fact]
    [Trait("Plan", "AC-binding-reapply-1")]
    [Trait("Plan", "EDGE-binding-reapply-1")]
    public void Active_branch_uses_the_pure_decision()
    {
        var branch = ActiveBranch();

        var reapply = Assert.Single(Calls(branch, "IsReapplyOfActive"));
        Assert.Contains("preApply", reapply);
        Assert.DoesNotContain("null", reapply);
        Assert.Contains("explicitApply", Assert.Single(Calls(branch, "DecideActiveApply")));

        Assert.Equal("Restore(pick.Value, stripImported: unequipUnset)", Assert.Single(Calls(branch, "Restore")));
        Assert.Equal("DisableUnboundImportedPacks(pick.Value)", Assert.Single(Calls(branch, "DisableUnboundImportedPacks")));
        Assert.Empty(Calls(branch, "Capture"));

        var evaluate = Method(Service(), "private void EvaluateAppliedDesign(");
        Assert.True(evaluate.IndexOf("UnequipUnsetSlots(", StringComparison.Ordinal)
                  < evaluate.IndexOf("if (activeDesignId == pick)", StringComparison.Ordinal),
            "the unset slots are unequipped before the already-active branch");
    }

    [Fact]
    [Trait("Plan", "AC-binding-reapply-2")]
    [Trait("Plan", "EDGE-binding-reapply-4")]
    [Trait("Plan", "EDGE-binding-reapply-5")]
    [Trait("Plan", "EDGE-binding-reapply-17")]
    [Trait("Plan", "EDGE-binding-reapply-18")]
    public void Restore_is_the_shared_full_restore()
    {
        var src = Service();
        var restore = Method(src, "public void Restore(Guid designId");
        Assert.True(restore.IndexOf("if (!FeatureOn) return;", StringComparison.Ordinal)
                  < restore.IndexOf("RestoreCharacter(", StringComparison.Ordinal));
        Assert.Contains("RestoreProteusOnly(", restore);

        var character = Method(src, "private void RestoreCharacter(");
        var proteusOnly = Method(src, "private void RestoreProteusOnly(");
        Assert.Contains("suppressEcho: true", Assert.Single(Calls(character, "AdoptOverrides")));
        Assert.Contains("suppressEcho: true", Assert.Single(Calls(proteusOnly, "AdoptOverrides")));
        Assert.Single(Calls(character, "ArmTemporaryGuard"));
        Assert.Single(Calls(character, "ArmPostLoadSweep"));

        var adopt = Method(src, "private void AdoptOverrides(");
        Assert.Contains("PresetsSuperseded?.Invoke()", adopt);
        Assert.Contains("CloneOverrides(b.Mods)", adopt);

        // Unbound mods that publish their own content stay on; imported packs only go with stripImported.
        Assert.Contains("UnboundContentMods(b, allMods, stripImported)", proteusOnly);
        Assert.Contains("if (boundDirs.Contains(e.ModDirectory) || held.Contains(e.ModDirectory)) continue;", proteusOnly);
        Assert.Contains("if (stripImported && e.Metadata.HasContent) continue;",
            Method(src, "private static HashSet<string> UnboundContentMods(HashSet<string> bound"));
    }

    [Fact]
    [Trait("Plan", "EDGE-binding-reapply-3")]
    public void Echo_window_is_checked_before_evaluation()
    {
        var body = HandleStateFinalized();
        int echo = body.IndexOf("if (Environment.TickCount64 < suppressUntilTick) return;", StringComparison.Ordinal);
        Assert.True(echo >= 0, "no echo return");
        Assert.True(echo < body.IndexOf("EvaluateAppliedDesign(", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Plan", "AC-binding-reapply-3")]
    [Trait("Plan", "AC-binding-reapply-4")]
    [Trait("Plan", "EDGE-binding-reapply-16")]
    public void Only_named_paths_write_the_binding_store()
    {
        var src = Service();
        string[] writers =
        [
            "private void Capture(Guid designId)", "public bool UpdateActiveBindingFromCurrentState(",
            "public void RemoveBinding(", "public void MoveMod(",
        ];
        var allowed = writers.Select(sig =>
        {
            var body = Method(src, sig);
            int start = src.IndexOf(body, src.IndexOf(sig, StringComparison.Ordinal), StringComparison.Ordinal);
            return (start, end: start + body.Length);
        }).ToArray();

        // Every store write (Save, the deferred save, an entry set or removed) sits inside one of the writers.
        var writes = Regex.Matches(src, @"(?<![\w.])(Save|SaveDeferred)\(\);|store\.Bindings\[[^\]]+\]\s*=|store\.Bindings\.Remove\(");
        Assert.NotEmpty(writes);
        foreach (Match w in writes)
            Assert.True(allowed.Any(a => w.Index >= a.start && w.Index < a.end),
                $"store write outside the named paths at offset {w.Index}: {w.Value}");

        var clear = Method(src, "public bool ClearOptionOverride(");
        Assert.DoesNotContain("store", clear);
        Assert.DoesNotContain("ClearScope", clear);

        // [EDGE-binding-reapply-45] Every other partial of the service writes no store at all (the harness one included).
        var partials = Directory.GetFiles(Path.Combine(RepoDir(), "Proteus", "Services"), "DesignBindingService*.cs")
                                .Where(f => !f.EndsWith("DesignBindingService.cs", StringComparison.Ordinal)).ToList();
        Assert.Contains(partials, f => f.EndsWith("DesignBindingService.Harness.cs", StringComparison.Ordinal));
        foreach (var file in partials)
            Assert.Empty(Regex.Matches(File.ReadAllText(file), @"(?<![\w.])(Save|SaveDeferred)\(\);|store\.Bindings\[[^\]]+\]\s*=|store\.Bindings\.Remove\("));
    }

    [Fact]
    [Trait("Plan", "EDGE-binding-reapply-40")]
    public void Unequip_lambda_checks_the_epoch()
    {
        // A harness put-back cancels a queued unequip; only the harness partial bumps the epoch.
        var unequip = Method(Service(), "private void UnequipUnsetSlots(");
        var lambda = Method(unequip, "framework.RunOnTick(() =>");
        var first = lambda.TrimStart('{').TrimStart().Split('\n')[0].Trim();
        Assert.Equal("if (epoch != unequipEpoch) return;", first);
        Assert.Contains("var epoch = unequipEpoch;", unequip);
        Assert.DoesNotContain("unequipEpoch++", Service());
        Assert.Contains("unequipEpoch++", File.ReadAllText(Path.Combine(RepoDir(), "Proteus", "Services", "DesignBindingService.Harness.cs")));
    }

    [Fact]
    [Trait("Plan", "AC-binding-reapply-4")]
    [Trait("Plan", "EDGE-binding-reapply-14")]
    [Trait("Plan", "EDGE-binding-reapply-15")]
    public void Reset_to_defaults_clears_only_the_live_override()
    {
        var clear = Method(Service(), "public bool ClearOptionOverride(");
        Assert.Single(Calls(clear, "overrides.ClearOption"));
        Assert.DoesNotContain("Save", clear);

        // Expression-bodied: the member runs to its semicolon.
        var routerSrc = Source("Services/OverlayEditRouter.cs");
        int at = routerSrc.IndexOf("public bool ClearOptionOverride(", StringComparison.Ordinal);
        Assert.True(at >= 0, "no router ClearOptionOverride");
        var router = routerSrc[at..(routerSrc.IndexOf(';', at) + 1)];
        Assert.Contains("BagFor(modDir) is { } bag", router);
        Assert.Contains("bag.ClearOption(modDir, group, option)", router);

        // The mod's own metadata is still reset and staged.
        var window = Source("Gui/StatusWindow.cs");
        Assert.Contains("if (!overrideActive || resetSimple) discovery.StageMetadata(entry);", window);
        Assert.Contains("if (!overrideActive || resetOpt) discovery.StageMetadata(entry);", window);
    }

    [Fact]
    [Trait("Plan", "AC-binding-reapply-5")]
    public void Update_still_replaces_store_and_bag()
    {
        var update = Method(Service(), "public bool UpdateActiveBindingFromCurrentState(");
        Assert.Contains("store.Bindings[id] = new DesignBinding", update);
        Assert.Single(Calls(update, "Save"));
        Assert.Contains("CloneOverrides(mods)", update);
    }

    [Fact]
    [Trait("Plan", "EDGE-binding-reapply-19")]
    public void Capture_still_requires_the_worn_design()
    {
        var capture = Method(Service(), "private void Capture(Guid designId)");
        int mismatch = capture.IndexOf("if (!StateMatches(", StringComparison.Ordinal);
        Assert.True(mismatch >= 0, "no worn-design check");
        Assert.True(mismatch < capture.IndexOf("store.Bindings[designId] = binding", StringComparison.Ordinal));

        // [AC-binding-reapply-3] A rewrite that is no edit (write protection, a folder move) is never captured.
        int notEdit = capture.IndexOf("if (!IsDesignEdit(cachedDesign, design))", StringComparison.Ordinal);
        Assert.True(notEdit >= 0 && notEdit < capture.IndexOf("store.Bindings[designId] = binding", StringComparison.Ordinal));
        Assert.True(capture.IndexOf("designCache.TryGetValue(designId, out cachedDesign)", StringComparison.Ordinal)
                  < capture.IndexOf("designCache.Remove(designId)", StringComparison.Ordinal), "the earlier copy is read before it is dropped");

        // The skip branch only logs and returns: no record of the skipped design is kept.
        var skip = Method(capture[mismatch..], "if (!StateMatches(");
        Assert.DoesNotContain("=", skip.Replace("=>", "").Replace("??=", ""));
        Assert.Contains("return;", skip);
    }

    [Fact]
    [Trait("Plan", "AC-binding-reapply-8")]
    public void Readme_bindings_section_pins_reapply_and_scope_clauses()
    {
        var readme = File.ReadAllText(Path.Combine(RepoDir(), "README.md"));
        int start = readme.IndexOf("#### Bindings", StringComparison.Ordinal);
        Assert.True(start >= 0, "no Bindings section");
        int end = readme.IndexOf("\n#### ", start + 1, StringComparison.Ordinal);
        var bindings = readme[start..(end < 0 ? readme.Length : end)];

        Assert.Contains("press **Update** first to keep them", bindings);
        Assert.Contains("while you weren't wearing that version", bindings);
        Assert.Contains("doesn't cover mod-wide or global settings", bindings);
        Assert.Contains("unless the active design binding captured that mod", readme);
    }
}
