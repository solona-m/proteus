using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Proteus.Interop;
using Proteus.Services.Matcap;
using Xunit;

namespace Proteus.Tests;

/// <summary>A Penumbra stand-in: one collection, mod settings by folder, writes recorded.</summary>
internal sealed class FakeMatcapPenumbra : IMatcapPenumbra
{
    public string CollectionName = "Mine";
    public readonly Dictionary<string, PenumbraBridge.ModSettingsSnapshot> Mods = new(StringComparer.OrdinalIgnoreCase);
    public readonly Dictionary<string, string> Holds = new(StringComparer.OrdinalIgnoreCase);
    public readonly List<string> Writes = [];
    public Func<string, string?> Resolver = _ => null;
    public string Root = @"C:\mods";
    public bool Available = true;

    public (Guid Id, string Name)? PlayerCollection() => Available ? (Guid.Empty, CollectionName) : null;
    public PenumbraBridge.ModSettingsSnapshot? Settings(Guid c, string mod) => Mods.TryGetValue(mod, out var s) ? s : null;
    public PenumbraBridge.TemporaryHold? Hold(Guid c, string mod) =>
        Holds.TryGetValue(mod, out var src) ? new PenumbraBridge.TemporaryHold(true, false, src, true, 0) : new PenumbraBridge.TemporaryHold(false, false, "", false, 0);
    public bool SetEnabled(Guid c, string mod, bool enabled)
    {
        Writes.Add($"enable {mod} {enabled}");
        var s = Mods.TryGetValue(mod, out var x) ? x : new PenumbraBridge.ModSettingsSnapshot(false, 0, new(), false, false);
        Mods[mod] = s with { Enabled = enabled };
        return true;
    }
    public bool SetOption(Guid c, string mod, string group, string option)
    {
        Writes.Add($"option {mod} {group}={option}");
        var s = Mods.TryGetValue(mod, out var x) ? x : new PenumbraBridge.ModSettingsSnapshot(false, 0, new(), false, false);
        var opts = new Dictionary<string, List<string>>(s.Options) { [group] = [option] };
        Mods[mod] = s with { Options = opts };
        return true;
    }
    public IReadOnlyDictionary<string, string?>? Resolve(IReadOnlyList<string> paths) => paths.ToDictionary(p => p, Resolver);
    public string? ModsRoot() => Root;
}

public class MatcapSelectionTests
{
    private static MatcapLibrary Lib(params string[] also) => new("Lib", "Test Library", "https://x", "Texture Slot A",
        "chara/common/texture/testlib_a.tex", "mat0.mtrl",
        [new MatcapOption("Gem Red", @"common\0\0.tex"), new MatcapOption("Pearl", @"common\0\1.tex")], also);

    private static PenumbraBridge.ModSettingsSnapshot On(string option = "Gem Red", bool inherited = false) =>
        new(true, 0, new Dictionary<string, List<string>> { ["Texture Slot A"] = [option] }, inherited, false);

    [Fact]
    [Trait("Plan", "AC-matcaps-5")]
    public void Sets_option_by_name_and_checks_winner()
    {
        var p = new FakeMatcapPenumbra();
        p.Mods["Lib"] = On();
        p.Resolver = _ => Path.Combine(p.Root, "Lib", "common", "0", "1.tex");
        var r = MatcapSelection.Select(p, Lib(), "Pearl", confirmEnable: false);
        Assert.True(r.Ok, r.Message);
        Assert.Equal(["option Lib Texture Slot A=Pearl"], p.Writes);
        Assert.True(r.Winning);
        Assert.Equal("Gem Red", r.OptionBefore);
        Assert.True(r.EnabledBefore);
    }

    [Fact]
    [Trait("Plan", "EDGE-matcaps-14")]
    public void Not_winning_warned()
    {
        var p = new FakeMatcapPenumbra();
        p.Mods["Lib"] = On();
        p.Resolver = _ => Path.Combine(p.Root, "Other", "x.tex");
        var r = MatcapSelection.Select(p, Lib(), "Pearl", false);
        Assert.True(r.Ok);
        Assert.False(r.Winning);
        Assert.EndsWith(Path.Combine("Other", "x.tex"), r.WinningFile);
    }

    [Fact]
    [Trait("Plan", "EDGE-matcaps-12")]
    public void Disabled_library_needs_confirmation_lists_other_redirects()
    {
        var p = new FakeMatcapPenumbra();
        var lib = Lib("chara/monster/m8058/obj/body/b0001/model/m8058b0001.mdl", "chara/common/texture/modded_mask.tex",
                      "a", "b", "c", "d");
        var r = MatcapSelection.Select(p, lib, "Pearl", confirmEnable: false);
        Assert.Equal(MatcapSelection.CodeNeedsConfirm, r.Code);
        Assert.Contains("m8058b0001.mdl", r.Message);
        Assert.Contains("+1", r.Message);
        Assert.Empty(p.Writes);

        var yes = MatcapSelection.Select(p, lib, "Pearl", confirmEnable: true);
        Assert.True(yes.Ok);
        Assert.Equal(["enable Lib True", "option Lib Texture Slot A=Pearl"], p.Writes);
        Assert.False(yes.EnabledBefore);
    }

    [Fact]
    [Trait("Plan", "EDGE-matcaps-12")]
    public void Cancel_writes_nothing()
    {
        var p = new FakeMatcapPenumbra();
        MatcapSelection.Select(p, Lib(), "Pearl", confirmEnable: false);
        Assert.Empty(p.Writes);
    }

    [Fact]
    [Trait("Plan", "EDGE-matcaps-13")]
    public void Inherited_and_base_collection_warned()
    {
        var p = new FakeMatcapPenumbra { CollectionName = "Default" };
        p.Mods["Lib"] = On(inherited: true);
        var st = MatcapSelection.Read(p, Lib());
        Assert.True(st.Inherited);
        Assert.True(st.IsBase);
        Assert.False(MatcapSelection.IsBaseName("Mine"));
    }

    [Fact]
    [Trait("Plan", "EDGE-matcaps-15")]
    public void Held_library_reported()
    {
        var p = new FakeMatcapPenumbra();
        p.Mods["Lib"] = On();
        p.Holds["Lib"] = "Glamourer";
        Assert.Equal("Glamourer", MatcapSelection.Read(p, Lib()).HeldBy);
        Assert.True(MatcapSelection.Select(p, Lib(), "Pearl", false).Ok);   // still written
    }

    [Fact]
    [Trait("Plan", "EDGE-matcaps-10")]
    public void Apply_enables_off_library()
    {
        var p = new FakeMatcapPenumbra();
        p.Mods["Lib"] = On() with { Enabled = false };
        Assert.True(MatcapSelection.Select(p, Lib(), "Pearl", true).Ok);
        Assert.True(p.Mods["Lib"].Enabled);
    }

    [Fact]
    [Trait("Plan", "EDGE-matcaps-30")]
    public void No_penumbra_no_write()
    {
        var p = new FakeMatcapPenumbra { Available = false };
        Assert.Equal(MatcapSelection.CodeNoPenumbra, MatcapSelection.Select(p, Lib(), "Pearl", true).Code);
        Assert.Empty(p.Writes);
    }
}
