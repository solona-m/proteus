using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NSubstitute;
using Proteus.Services;
using Xunit;
using Xunit.Abstractions;

namespace Proteus.Tests;

/// <summary>
/// Reported: a body swap from a gen3 mod onto Rue+ (bibo) does not work. Both transfer maps ship
/// (<c>gen3_to_bibo</c> and <c>bibo_to_gen3</c>), so the pair is supposed to be buildable; these find out where it
/// actually stops.
/// </summary>
public class Gen3ToRueDiagTests(ITestOutputHelper output)
{
    private const string Mods = @"E:\Penumbradt";
    /// <summary>The PLUGIN directory. UVRemapService appends "uvmaps" to it itself — passing the maps folder sends
    /// it looking in uvmaps\uvmaps, which reports "map not found" and looks exactly like an unsupported pair.</summary>
    private const string PluginDir = @"E:\repos\Proteus\Proteus";

    private static readonly string Maps = Path.Combine(PluginDir, "uvmaps");

    /// <summary>A remapper that prints what it logs, so a missing map or a dead codec cannot look like a refusal.</summary>
    private UVRemapService Remap()
    {
        var log = NSubstitute.Substitute.For<Dalamud.Plugin.Services.IPluginLog>();
        log.WhenForAnyArgs(l => l.Warning(default(string)!)).Do(c => output.WriteLine("   log warn: " + c.Arg<string>()));
        log.WhenForAnyArgs(l => l.Error(default(string)!)).Do(c => output.WriteLine("   log error: " + c.Arg<string>()));
        log.WhenForAnyArgs(l => l.Information(default(string)!)).Do(c => output.WriteLine("   log info: " + c.Arg<string>()));
        output.WriteLine($"   maps dir: {Maps} (exists: {Directory.Exists(Maps)}, " +
                         $"{(Directory.Exists(Maps) ? Directory.GetFiles(Maps, "*.tif").Length : 0)} tif)");
        return new UVRemapService(log, PluginDir);
    }

    /// <summary>Every installed body mod, the slots it sizes, and the texture layout its models are drawn with.</summary>
    [Fact]
    public void Which_bodies_are_installed_and_what_layout_each_is()
    {
        if (!Directory.Exists(Mods)) { output.WriteLine("no mods root"); return; }

        output.WriteLine($"{"layout",-8}{"sizes",7}  mod");
        foreach (string dir in Directory.GetDirectories(Mods).OrderBy(d => d))
        {
            BodySizeCatalog catalog;
            try { catalog = BodySizeCatalog.Read(dir); } catch { continue; }
            if (!catalog.IsBody) continue;

            // The layout is read off a model, so take the first size that has a readable file.
            string layout = "?";
            foreach (var option in catalog.Options)
            {
                string path = catalog.PathOf(option);
                if (!File.Exists(path)) continue;
                if (ModelPartReader.Read(File.ReadAllBytes(path)) is not { } parts) continue;
                layout = BodyCorrespondence.LayoutOf(parts) ?? "none";
                break;
            }
            output.WriteLine($"{layout,-8}{catalog.Options.Count,7}  {Path.GetFileName(dir)}");
        }
    }

    /// <summary>
    /// Build the correspondence the reported swap needs: a gen3 body to a bibo one. Prints the refusal when it will
    /// not build, and how much of the body it matched when it will — a correspondence that builds but matches almost
    /// nothing is the other way this "does not work".
    /// </summary>
    [Fact]
    public void Can_a_gen3_body_be_paired_with_a_bibo_one()
    {
        if (Pick("gen3") is not { } gen3 || Pick("bibo") is not { } bibo)
        {
            output.WriteLine("need one gen3 and one bibo body installed");
            return;
        }
        output.WriteLine($"gen3: {gen3.Mod}  /  {gen3.Option.FullLabel}");
        output.WriteLine($"bibo: {bibo.Mod}  /  {bibo.Option.FullLabel}");

        foreach (var (a, b, way) in new[] { (gen3, bibo, "gen3 -> bibo"), (bibo, gen3, "bibo -> gen3") })
        {
            byte[] sb = File.ReadAllBytes(a.Path), tb = File.ReadAllBytes(b.Path);
            var source = ModelPartReader.Read(sb)!;
            var target = ModelPartReader.Read(tb)!;

            var clock = System.Diagnostics.Stopwatch.StartNew();
            bool ok = BodyCorrespondence.TryBuild(source, BodyRetargetDiagTests.Uv(sb),
                                                  target, BodyRetargetDiagTests.Uv(tb),
                                                  "_top", out var built, out string refusal, Remap());
            clock.Stop();

            output.WriteLine("");
            output.WriteLine($"{way} in {clock.ElapsedMilliseconds} ms: {(ok ? built!.Describe() : "REFUSED — " + refusal)}");
        }
    }

    /// <summary>Straight at the converter: does a gen3-to-bibo map load at all?</summary>
    [Fact]
    public void Does_the_gen3_to_bibo_map_load()
    {
        var remap = Remap();
        foreach (var (from, to) in new[] { ("gen3", "bibo"), ("bibo", "gen3") })
        {
            var convert = remap.UvConverter(from, to, unmirror: true);
            output.WriteLine($"{from} -> {to}: {(convert == null ? "NO CONVERTER" : "built")}");
            if (convert == null) continue;

            // And does it answer for a uv in the middle of the sheet?
            var answered = convert(0.5f, 0.5f, 1);
            output.WriteLine($"     (0.5, 0.5) maps to {(answered is { } a ? $"({a.Item1:F3}, {a.Item2:F3})" : "nothing")}");
        }
    }

    /// <summary>
    /// What a gen3 garment's materials and uvs look like AFTER a refit onto a bibo body. The geometry pairs at 99.7%,
    /// so a swap that "does not work" has to be failing further down: the skin mesh drawn with a material of the wrong
    /// layout would texture it with the other body's sheet, which reads as scrambled skin rather than as a bad fit.
    /// </summary>
    [Fact]
    public void What_a_gen3_to_bibo_refit_does_to_materials_and_uvs()
    {
        if (Pick("gen3") is not { } gen3 || PickMod("hs-Rue+", "bibo") is not { } rue)
        {
            output.WriteLine("need a gen3 body and Rue+ installed");
            return;
        }
        output.WriteLine($"garment stands in as: {gen3.Mod} / {gen3.Option.FullLabel}");
        output.WriteLine($"refit onto:           {rue.Mod} / {rue.Option.FullLabel}");

        byte[] garmentBytes = File.ReadAllBytes(gen3.Path);
        byte[] sourceBytes = File.ReadAllBytes(gen3.Path);      // made for its own body, which is the honest pairing
        byte[] targetBytes = File.ReadAllBytes(rue.Path);
        var garment = ModelPartReader.Read(garmentBytes)!;
        var sourceParts = ModelPartReader.Read(sourceBytes)!;
        var targetParts = ModelPartReader.Read(targetBytes)!;

        if (!BodyCorrespondence.TryBuild(sourceParts, BodyRetargetDiagTests.Uv(sourceBytes),
                                         targetParts, BodyRetargetDiagTests.Uv(targetBytes),
                                         "_top", out var built, out string refusal, Remap()))
        {
            output.WriteLine($"REFUSED: {refusal}");
            return;
        }
        output.WriteLine($"correspondence: {built!.Describe()}");

        var pairs = new List<BodyRetarget.SlotPair>
        {
            new("_top", built, targetParts, targetBytes, sourceBytes),
        };

        Show("the garment as authored", garmentBytes);
        Show("the target body", targetBytes);
        foreach (bool swap in new[] { false, true })
        {
            var planned = BodyRetarget.Plan(garment, garmentBytes, pairs, "_top",
                                            replaceSkin: swap, acrossBodies: true);
            Show($"refit, new body's skin = {swap}", planned.Model);
        }
    }

    /// <summary>Each whole submesh: its material, the layout that material implies, and the uv range it covers.</summary>
    private void Show(string label, byte[] mdl)
    {
        if (ModelPartReader.Read(mdl) is not { } m) { output.WriteLine($"{label}: unreadable"); return; }
        float[] uv = BodyRetargetDiagTests.Uv(mdl);

        output.WriteLine("");
        output.WriteLine($"--- {label}");
        foreach (var span in m.MeshSpans)
        {
            var part = m.Parts.FirstOrDefault(p => p.Mesh == span.Mesh && p.Island < 0);
            if (part == null) continue;
            string layout = SecondSkinWriter.SkinMaterialBodyType(part.Material) ?? "-";
            float lo = 9f, hi = -9f;
            for (int i = 0; i < span.Count; i++)
            {
                int at = (span.BaseVertex + i) * 2;
                if (at + 1 >= uv.Length) break;
                lo = MathF.Min(lo, uv[at]);
                hi = MathF.Max(hi, uv[at]);
            }
            output.WriteLine($"    mesh {span.Mesh}  {span.Count,6:N0} verts  layout {layout,-5}  " +
                             $"u {lo,6:F3}..{hi,6:F3}  {part.Material}");
        }
    }

    /// <summary>A named mod's first readable chest size, checked to be the layout expected.</summary>
    private (string Mod, BodyOption Option, string Path)? PickMod(string startsWith, string layout)
    {
        foreach (string dir in Directory.GetDirectories(Mods).OrderBy(d => d))
        {
            if (!Path.GetFileName(dir).StartsWith(startsWith, StringComparison.OrdinalIgnoreCase)) continue;
            var catalog = BodySizeCatalog.Read(dir);
            foreach (var option in catalog.For("_top", "0201"))
            {
                string path = catalog.PathOf(option);
                if (!File.Exists(path)) continue;
                if (ModelPartReader.Read(File.ReadAllBytes(path)) is not { } parts) continue;
                if (!string.Equals(BodyCorrespondence.LayoutOf(parts), layout, StringComparison.OrdinalIgnoreCase))
                    continue;
                return (Path.GetFileName(dir), option, path);
            }
        }
        return null;
    }

    /// <summary>
    /// A PRE-DAWNTRAIL (v5) garment through the refit. The re-emit forces the header to v6 because it only writes v6
    /// bone tables, so everything else about the model has to survive that upgrade: the material list, which mesh is
    /// drawn with which material, the texture coordinates, and the vertex count.
    /// </summary>
    [Theory]
    [InlineData(@"E:\Penumbradt\Ginger - by Solona\Riviera Dress\Everything\chara\equipment\e9069\model\c0201e9069_top.mdl")]
    [InlineData(@"E:\Penumbradt\Christina - by Solona\Size\Medium\chara\equipment\e0118\model\c0201e0118_top.mdl")]
    public void A_pre_dawntrail_garment_through_the_refit(string garmentFile)
    {
        if (!File.Exists(garmentFile)) { output.WriteLine("not installed"); return; }
        if (Pick("gen3") is not { } gen3 || PickMod("hs-Rue+", "bibo") is not { } rue) return;

        byte[] garmentBytes = File.ReadAllBytes(garmentFile);
        output.WriteLine($"{Path.GetFileName(garmentFile)}: version 0x{BitConverter.ToUInt32(garmentBytes, 0):x8}");

        byte[] sourceBytes = File.ReadAllBytes(gen3.Path), targetBytes = File.ReadAllBytes(rue.Path);
        var sourceParts = ModelPartReader.Read(sourceBytes)!;
        var targetParts = ModelPartReader.Read(targetBytes)!;
        if (!BodyCorrespondence.TryBuild(sourceParts, BodyRetargetDiagTests.Uv(sourceBytes),
                                         targetParts, BodyRetargetDiagTests.Uv(targetBytes),
                                         "_top", out var built, out string refusal, Remap()))
        {
            output.WriteLine($"REFUSED: {refusal}");
            return;
        }

        var garment = ModelPartReader.Read(garmentBytes)!;
        var pairs = new List<BodyRetarget.SlotPair> { new("_top", built!, targetParts, targetBytes, sourceBytes) };
        var planned = BodyRetarget.Plan(garment, garmentBytes, pairs, "_top", replaceSkin: false, acrossBodies: true);

        output.WriteLine($"   out version 0x{BitConverter.ToUInt32(planned.Model, 0):x8}");
        Show("before", garmentBytes);
        Show("after", planned.Model);

        // The uvs must be untouched: the refit moves positions only, and a shifted uv is a scrambled texture.
        float[] a = BodyRetargetDiagTests.Uv(garmentBytes), b = BodyRetargetDiagTests.Uv(planned.Model);
        if (a.Length != b.Length)
        {
            output.WriteLine($"   UV COUNT CHANGED: {a.Length / 2:N0} -> {b.Length / 2:N0}");
            return;
        }
        int moved = 0;
        float worst = 0f;
        for (int i = 0; i < a.Length; i++)
        {
            float d = MathF.Abs(a[i] - b[i]);
            if (d > 1e-6f) moved++;
            worst = MathF.Max(worst, d);
        }
        output.WriteLine($"   uvs: {moved:N0} of {a.Length:N0} changed, worst {worst:F6}");
    }

    /// <summary>The first readable chest size of the first installed body whose models are drawn in that layout.</summary>
    private (string Mod, BodyOption Option, string Path)? Pick(string layout)
    {
        foreach (string dir in Directory.GetDirectories(Mods).OrderBy(d => d))
        {
            BodySizeCatalog catalog;
            try { catalog = BodySizeCatalog.Read(dir); } catch { continue; }
            if (!catalog.IsBody) continue;

            foreach (var option in catalog.For("_top", "0201"))
            {
                string path = catalog.PathOf(option);
                if (!File.Exists(path)) continue;
                if (ModelPartReader.Read(File.ReadAllBytes(path)) is not { } parts) continue;
                if (!string.Equals(BodyCorrespondence.LayoutOf(parts), layout, StringComparison.OrdinalIgnoreCase))
                    break;      // this mod is a different layout; try the next mod
                return (Path.GetFileName(dir), option, path);
            }
        }
        return null;
    }
}
