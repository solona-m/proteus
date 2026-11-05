using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Dalamud.Plugin.Services;
using NSubstitute;
using Proteus.Localization;
using Proteus.Services;
using Xunit;
using static Proteus.Services.SizeSetRefit;

namespace Proteus.Tests;

/// <summary>
/// #43: a women's piece is made men's after all when every file of the mod's own men's model of it can't be read as a
/// model at all — missing, zero bytes, or rejected by the parser — and a men's version of ours made so is said wherever
/// it still stands over the author's model (plan 42-43-tbsex-legs-cross).
/// </summary>
public sealed class AuthorsMensFallbackTests : IDisposable
{
    private readonly List<string> made = [];

    public void Dispose()
    {
        foreach (string dir in made)
            try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    private const string Top = "chara/equipment/e6000/model/c0201e6000_top.mdl";
    private const string Glove = "chara/equipment/e6000/model/c0201e6000_glv.mdl";
    private const string MensTop = "chara/equipment/e6000/model/c0101e6000_top.mdl";
    private const string MensGlove = "chara/equipment/e6000/model/c0101e6000_glv.mdl";
    private static readonly string MensGroup = Strings.SizeSet.MensGroup;

    private static readonly byte[] Model = SyntheticModel.Build([], new SyntheticModel.Mesh("mt_c0201e6000_glv_a.mtrl",
                                                                                            new SyntheticModel.Sub(0)));

    private static IPluginLog Log() => Substitute.For<IPluginLog>();

    /// <summary>
    /// A mod: a "Size" group of options S and M giving each of <paramref name="paths"/> the file "{option}/{i}.mdl" (not
    /// written: missing), plus <paramref name="extraGroups"/> (JSON) and <paramref name="defaultFiles"/>.
    /// </summary>
    private string Mod(string[] paths, string extraGroups = "", string defaultFiles = "")
    {
        string root = Path.Combine(Path.GetTempPath(), "proteus-authorsmens-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        made.Add(root);
        string Files(string option) => string.Join(", ", paths.Select((p, i) => $"\"{p}\": \"{option}\\\\{i}.mdl\""));
        File.WriteAllText(Path.Combine(root, PenumbraModMeta.MetaFile), $$"""
            { "FileVersion": 4, "Name": "Outfit",
              "DefaultData": { "Files": { {{defaultFiles}} }, "FileSwaps": {}, "Manipulations": [] },
              "Groups": [
                { "Version": 0, "Name": "Size", "Type": "Single", "Priority": 0, "DefaultSettings": 0, "Options": [
                  { "Name": "S", "Files": { {{Files("s")}} } },
                  { "Name": "M", "Files": { {{Files("m")}} } } ] }{{extraGroups}} ] }
            """);
        return root;
    }

    /// <summary>A group of ours, as the size set writes the men's versions' group: option "TBSE" giving each path the
    /// file under our subfolder, recorded as ours.</summary>
    private static string OurGroup(string name, params string[] paths)
        => Group(name, "Single", (BodyRetargetWriter.OriginalOption, []),
                 ("TBSE", paths.Select((p, i) => (p, $"{BodyRetargetWriter.Subfolder}/men{i}.mdl")).ToArray()));

    /// <summary>", { group }" as a manifest's groups list takes it.</summary>
    private static string Group(string name, string type, params (string Option, (string GamePath, string File)[] Files)[] options)
    {
        var json = new
        {
            Version = 0, Name = name, Type = type, Priority = 99, DefaultSettings = 0,
            Options = options.Select(o => new { Name = o.Option, Files = o.Files.ToDictionary(f => f.GamePath, f => f.File) }),
        };
        return ", " + JsonSerializer.Serialize(json);
    }

    private static void Record(string root, string group, params string[] paths)
    {
        Directory.CreateDirectory(Path.Combine(root, SidecarDiscoveryService.SidecarSubdir));
        var record = new BodyRetargetWriter.Record
        {
            Group = group,
            Options = paths.Select((p, i) => new BodyRetargetWriter.Entry
            {
                Group = group, Name = "TBSE", GamePath = p, File = $"{BodyRetargetWriter.Subfolder}/men{i}.mdl",
            }).ToList(),
        };
        File.WriteAllText(Path.Combine(root, SidecarDiscoveryService.SidecarSubdir, BodyRetargetWriter.RecordFile),
                          JsonSerializer.Serialize(record));
    }

    private static void Write(string root, string rel, byte[] bytes)
    {
        string path = Path.Combine(root, rel.Replace('\\', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    private static RunLog Seeded(Piece piece, params (SizeStatus Status, string? Code)[] outcomes)
    {
        var log = new RunLog();
        foreach (var (status, code) in outcomes) log.Add(piece, status, code, "x");
        return log;
    }

    private static Request Req(string root, IReadOnlyList<string> keys, bool across = true, string? madeOn = null,
                               bool ends = true)
        => new(root, keys, false, false, [], Ends: ends, Across: across ? new AcrossInputs(_ => null) : null, MadeOn: madeOn);

    private static int Across(Request request, IReadOnlyList<Piece> pieces, IReadOnlyList<Piece> all, RunLog log)
        => RunAcross(request, pieces, all, null, Log(), _ => { }, log, CancellationToken.None);

    // ── which files can't be read as a model ─────────────────────────────

    [Fact]
    [Trait("Plan", "AC-42-43-tbsex-legs-cross-1")]
    [Trait("Plan", "EDGE-42-43-tbsex-legs-cross-29")]
    [Trait("Plan", "EDGE-42-43-tbsex-legs-cross-37")]
    [Trait("Plan", "EDGE-42-43-tbsex-legs-cross-38")]
    [Trait("Plan", "EDGE-42-43-tbsex-legs-cross-39")]
    public void Author_redirects_unparsable() // [AC-42-43-tbsex-legs-cross-1] [EDGE-42-43-tbsex-legs-cross-29] [EDGE-42-43-tbsex-legs-cross-37] [EDGE-42-43-tbsex-legs-cross-38] [EDGE-42-43-tbsex-legs-cross-39]
    {
        byte[] garbage = Model[..64];
        Assert.True(MalformedModel(Assert.ThrowsAny<Exception>(() => SecondSkinWriter.Parse(garbage))));
        // Anything else the parser could raise is a bug of ours, never a damaged file of the author's.
        Assert.False(MalformedModel(new NullReferenceException()));
        Assert.False(MalformedModel(new InvalidOperationException()));
        byte[] emptied = SyntheticModel.Build([], new SyntheticModel.Mesh("mt_c0101e6000_glv_a.mtrl"));
        SecondSkinWriter.Parse(emptied);   // parses, whatever it draws

        bool Unparsable(byte[]? glove, string? mensPath = null)
        {
            string root = Mod([Glove, MensGlove]);
            if (glove != null)
            {
                Write(root, "s/1.mdl", glove);
                Write(root, "m/1.mdl", glove);
            }
            return AuthorMensRedirectsUnparsable(root, mensPath ?? MensGlove, BodyRetargetWriter.ReadRecord(root));
        }
        Assert.True(Unparsable(null));      // missing
        Assert.True(Unparsable([]));        // zero bytes
        Assert.True(Unparsable(garbage));   // the parser rejects it
        Assert.False(Unparsable(Model));
        Assert.False(Unparsable(emptied));
        Assert.True(Unparsable([], MensGlove.ToUpperInvariant()));   // a path is a path whatever its case

        // One of the author's files parses — in default files, or another group's option — so the model is there.
        string elsewhere = Mod([Glove, MensGlove], defaultFiles: $"\"{MensGlove}\": \"default\\\\glove.mdl\"");
        Write(elsewhere, "default/glove.mdl", Model);
        Assert.False(AuthorMensRedirectsUnparsable(elsewhere, MensGlove, null));
        string otherGroup = Mod([Glove, MensGlove], extraGroups: Group("Extra", "Multi", ("On", [(MensGlove, "extra/glove.mdl")])));
        Write(otherGroup, "extra/glove.mdl", Model);
        Assert.False(AuthorMensRedirectsUnparsable(otherGroup, MensGlove, null));

        // Only files of ours at the path: nothing of the author's to stand in for.
        string ours = Mod([Glove], extraGroups: OurGroup(MensGroup, MensGlove));
        Record(ours, MensGroup, MensGlove);
        Assert.False(AuthorMensRedirectsUnparsable(ours, MensGlove, BodyRetargetWriter.ReadRecord(ours)));
    }

    [Fact]
    [Trait("Plan", "AC-42-43-tbsex-legs-cross-1")]
    public void For_men_is_offered_when_the_authors_mens_model_is_unparsable() // [AC-42-43-tbsex-legs-cross-1]
    {
        // Every women's piece has the author's men's model beside it: nothing to cross as 1410 saw it. Its files missing,
        // the size set may cross them after all, so "for men" must be offered — from the files, before any run.
        string root = Mod([Top, Glove, MensTop, MensGlove]);
        var pieces = PiecesOf(root);
        Assert.DoesNotContain(Offered(pieces), c => c.ForMen);
        Assert.Contains(Offered(pieces, root), c => c.Key == "tbse" + AcrossSuffix);
        Assert.Contains(Offered(pieces, root), c => c.Key == "tbse");   // the plain body, to try the author's model
        Assert.Contains("tbse" + AcrossSuffix, SizeSetBatch.RunKeys(pieces, ["tbse", "tbse@women"], men: false, root));

        // Both men's models a model: not offered.
        foreach (string rel in new[] { "s/2.mdl", "m/2.mdl", "s/3.mdl", "m/3.mdl" }) Write(root, rel, Model);
        Assert.DoesNotContain(Offered(pieces, root), c => c.ForMen);
    }

    [Fact]
    [Trait("Plan", "AC-42-43-tbsex-legs-cross-1")]
    public void A_file_that_cannot_be_opened_keeps_the_authors_model() // [AC-42-43-tbsex-legs-cross-1]
    {
        string root = Mod([Glove, MensGlove]);
        Write(root, "s/1.mdl", []);
        Write(root, "m/1.mdl", Model);
        Assert.False(AuthorMensRedirectsUnparsable(root, MensGlove, null));
        // Held open by another program: may well be a good model, so never counted.
        using (new FileStream(Path.Combine(root, "m", "1.mdl"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.False(Unparsable(root, "m/1.mdl"));
            Assert.False(AuthorMensRedirectsUnparsable(root, MensGlove, null));
        }
    }

    [Fact]
    [Trait("Plan", "EDGE-42-43-tbsex-legs-cross-39")]
    public void Is_our_file_matches_pieces_of() // [EDGE-42-43-tbsex-legs-cross-39]
    {
        var ours = new HashSet<string>(StringComparer.Ordinal) { RefitCore.Normal("refits/a.mdl") };
        Assert.True(IsOurFile(ours, "Refits\\A.mdl"));
        Assert.True(IsOurFile(ours, $"{BodyRetargetWriter.Subfolder}/anything.mdl"));
        Assert.False(IsOurFile(ours, "s/1.mdl"));

        // PiecesOf leaves ours out as before: our group's men's glove is no piece; the author's is.
        string root = Mod([Glove], extraGroups: OurGroup(MensGroup, MensGlove));
        Record(root, MensGroup, MensGlove);
        Assert.DoesNotContain(PiecesOf(root), p => p.GamePath == MensGlove);
        string theirs = Mod([Glove, MensGlove]);
        Assert.Contains(PiecesOf(theirs), p => p.GamePath == MensGlove);
    }

    // ── when the women's piece is crossed after all ───────────────────────

    [Fact]
    [Trait("Plan", "AC-42-43-tbsex-legs-cross-1")]
    public void Unparsable_is_crossed() // [AC-42-43-tbsex-legs-cross-1]
    {
        string root = Mod([Top, Glove, MensTop, MensGlove]);
        var pieces = PiecesOf(root);
        var log = new RunLog();
        log.Add(pieces.Single(p => p.GamePath == MensTop), SizeStatus.Skipped, Codes.Unreadable, "x");
        log.Add(pieces.Single(p => p.GamePath == MensGlove), SizeStatus.Skipped, Codes.EndsUnreadable, "x");
        var set = MensModelsUnparsable(pieces, log, root, null);
        Assert.Equal(new[] { MensTop, MensGlove }.Order(), set.Order());
        Assert.DoesNotContain(AcrossPieces(pieces), p => p.GamePath is Top or Glove);
        Assert.Contains(AcrossPieces(pieces, set), p => p.GamePath == Top);
        Assert.Contains(AcrossPieces(pieces, set), p => p.GamePath == Glove);
        StateAuthorsMens(pieces, ["tbse"], log, set);
        Assert.DoesNotContain(log.Sizes, s => s.ReasonCode == Codes.AuthorsMens);
        Assert.Empty(log.Notes);
    }

    [Fact]
    [Trait("Plan", "AC-42-43-tbsex-legs-cross-3")]
    [Trait("Plan", "EDGE-42-43-tbsex-legs-cross-1")]
    [Trait("Plan", "EDGE-42-43-tbsex-legs-cross-2")]
    [Trait("Plan", "EDGE-42-43-tbsex-legs-cross-3")]
    [Trait("Plan", "EDGE-42-43-tbsex-legs-cross-4")]
    [Trait("Plan", "EDGE-42-43-tbsex-legs-cross-6")]
    [Trait("Plan", "EDGE-42-43-tbsex-legs-cross-32")]
    public void Usable_or_other_is_not_crossed() // [AC-42-43-tbsex-legs-cross-3] [EDGE-42-43-tbsex-legs-cross-1] [EDGE-42-43-tbsex-legs-cross-2] [EDGE-42-43-tbsex-legs-cross-3] [EDGE-42-43-tbsex-legs-cross-4] [EDGE-42-43-tbsex-legs-cross-6] [EDGE-42-43-tbsex-legs-cross-32]
    {
        string root = Mod([Glove, MensGlove]);   // the men's glove's files missing
        var pieces = PiecesOf(root);
        var mens = pieces.Single(p => p.GamePath == MensGlove);
        var women = pieces.Single(p => p.GamePath == Glove);
        var unread = (SizeStatus.Skipped, (string?)Codes.EndsUnreadable);
        // Something came of the men's model this run: it is kept, whatever its files.
        foreach (var outcomes in new (SizeStatus, string?)[][]
                 {
                     [(SizeStatus.Skipped, Codes.AlreadyThere), unread],
                     [(SizeStatus.Skipped, Codes.MadeOnIt), unread],
                     [(SizeStatus.Skipped, Codes.AuthorsOwn), unread],
                     [(SizeStatus.Skipped, Codes.SameSkin), unread],
                     [(SizeStatus.Skipped, Codes.NoEnd), unread],
                     [(SizeStatus.Skipped, Codes.NoOwnLegs), unread],
                     [(SizeStatus.Made, null), unread],
                 })
            Assert.Empty(MensModelsUnparsable(pieces, Seeded(mens, outcomes), root, null));
        // Nothing came of it: the files decide, tried this run or not (only "for men" ticked: no outcome, or no body).
        foreach (var outcomes in new (SizeStatus, string?)[][]
                 {
                     [unread], [], [(SizeStatus.Skipped, Codes.NoBodyForRace)], [(SizeStatus.Refused, Codes.RefusedPair)],
                     [(SizeStatus.Failed, Codes.PieceFailed)], [(SizeStatus.Failed, Codes.SaveFailed)],
                 })
            Assert.Single(MensModelsUnparsable(pieces, Seeded(mens, outcomes), root, null));
        // A "Made on" body chosen: never.
        Assert.Empty(MensModelsUnparsable(pieces, Seeded(mens, unread), root, "Bibo+"));
        // A file of the author's that parses: never, whatever became of it.
        Write(root, "s/1.mdl", Model);
        foreach (var outcomes in new (SizeStatus, string?)[][]
                 {
                     [unread], [], [(SizeStatus.Skipped, Codes.NoBodyForRace)], [(SizeStatus.Refused, Codes.RefusedPair)],
                     [(SizeStatus.Failed, Codes.PieceFailed)], [(SizeStatus.Skipped, Codes.NotMadeOn)],
                 })
            Assert.Empty(MensModelsUnparsable(pieces, Seeded(mens, outcomes), root, null));

        // The notes: "made nothing" only when nothing came of it.
        var usable = Seeded(mens, (SizeStatus.Skipped, Codes.AlreadyThere), unread);
        StateAuthorsMens(pieces, ["tbse"], usable);
        Assert.Contains(usable.Sizes, s => s.ReasonCode == Codes.AuthorsMens && s.GamePath == MensGlove);
        Assert.Empty(usable.Notes);
        var refused = Seeded(mens, (SizeStatus.Refused, Codes.RefusedPair));
        StateAuthorsMens(pieces, ["tbse"], refused);
        Assert.Equal([string.Format(Strings.SizeSet.AuthorsMensFmt, women.Label)], refused.Notes);
    }

    [Fact]
    [Trait("Plan", "AC-42-43-tbsex-legs-cross-3")]
    [Trait("Plan", "EDGE-42-43-tbsex-legs-cross-5")]
    public void Untried_mens_model_keeps_the_unticked_note() // [AC-42-43-tbsex-legs-cross-3] [EDGE-42-43-tbsex-legs-cross-5]
    {
        // The author's men's glove a model: only "for men" ticked leaves it untried, and says to tick the plain body.
        string root = Mod([Glove, MensGlove]);
        Write(root, "s/1.mdl", Model);
        Write(root, "m/1.mdl", Model);
        var pieces = PiecesOf(root);
        var women = pieces.Single(p => p.GamePath == Glove);
        var log = Seeded(pieces.Single(p => p.GamePath == MensGlove), (SizeStatus.Skipped, Codes.NoBodyForRace));
        Assert.Empty(MensModelsUnparsable(pieces, log, root, null));
        Across(Req(root, ["tbse@women"]), pieces, pieces, log);
        Assert.Contains(string.Format(Strings.SizeSet.AuthorsMensUntickedFmt, women.Label, "TBSE"), log.Notes);
        Assert.Contains(log.Sizes, s => s.ReasonCode == Codes.AuthorsMens && s.GamePath == MensGlove);

        // Its files missing: "for men" alone crosses it, the plain body never needed (here the maps are missing).
        string missing = Mod([Glove, MensGlove]);
        var there = PiecesOf(missing);
        var crossed = Seeded(there.Single(p => p.GamePath == MensGlove), (SizeStatus.Skipped, Codes.NoBodyForRace));
        Across(Req(missing, ["tbse-x@women"]), there, there, crossed);
        Assert.Contains(crossed.Sizes, s => s.GamePath == MensGlove && s.Men && s.ReasonCode == Codes.MenMapsMissing);
        Assert.DoesNotContain(crossed.Notes, n => n == string.Format(Strings.SizeSet.AuthorsMensUntickedFmt, women.Label, "TBSE-X"));
    }

    [Fact]
    [Trait("Plan", "EDGE-42-43-tbsex-legs-cross-7")]
    public void Fallback_piece_is_never_silent() // [EDGE-42-43-tbsex-legs-cross-7]
    {
        string root = Mod([Top, Glove, MensGlove]);
        var pieces = PiecesOf(root);
        var mens = pieces.Single(p => p.GamePath == MensGlove);
        var women = pieces.Single(p => p.GamePath == Glove);

        // Crossed, but the maps are missing: said per men's body, as for any women's piece.
        var log = Seeded(mens, (SizeStatus.Skipped, Codes.EndsUnreadable));
        Across(Req(root, ["tbse", "tbse@women"]), pieces, pieces, log);
        Assert.Contains(log.Sizes, s => s.GamePath == MensGlove && s.Men && s.ReasonCode == Codes.MenMapsMissing);
        Assert.DoesNotContain(log.Sizes, s => s.ReasonCode == Codes.AuthorsMens);

        // No men's versions made at all this run: the 1410 statement.
        var none = Seeded(mens, (SizeStatus.Skipped, Codes.EndsUnreadable));
        Across(Req(root, ["tbse", "tbse@women"], across: false), pieces, pieces, none);
        Assert.Contains(none.Sizes, s => s.ReasonCode == Codes.AuthorsMens && s.GamePath == MensGlove);
        Assert.Contains(string.Format(Strings.SizeSet.AuthorsMensFmt, women.Label), none.Notes);
    }

    [Fact]
    [Trait("Plan", "EDGE-42-43-tbsex-legs-cross-32")]
    public void Made_on_never_falls_back() // [EDGE-42-43-tbsex-legs-cross-32]
    {
        string root = Mod([Glove, MensGlove]);
        var pieces = PiecesOf(root);
        var mens = pieces.Single(p => p.GamePath == MensGlove);
        var women = pieces.Single(p => p.GamePath == Glove);
        var chosen = Seeded(mens, (SizeStatus.Skipped, Codes.EndsUnreadable));
        Across(Req(root, ["tbse", "tbse@women"], madeOn: "Bibo+"), pieces, pieces, chosen);
        Assert.Contains(chosen.Sizes, s => s.ReasonCode == Codes.AuthorsMens && s.GamePath == MensGlove);
        Assert.Contains(string.Format(Strings.SizeSet.AuthorsMensFmt, women.Label), chosen.Notes);
        Assert.DoesNotContain(chosen.Sizes, s => s.ReasonCode == Codes.MenMapsMissing);
        // The control: the same run with no Made on body would cross it.
        var control = Seeded(mens, (SizeStatus.Skipped, Codes.EndsUnreadable));
        Across(Req(root, ["tbse", "tbse@women"]), pieces, pieces, control);
        Assert.Contains(control.Sizes, s => s.GamePath == MensGlove && s.ReasonCode == Codes.MenMapsMissing);
    }

    [Fact]
    [Trait("Plan", "AC-42-43-tbsex-legs-cross-2")]
    [Trait("Plan", "EDGE-42-43-tbsex-legs-cross-8")]
    public void Crossed_lines() // [AC-42-43-tbsex-legs-cross-2] [EDGE-42-43-tbsex-legs-cross-8]
    {
        string root = Mod([Glove, MensGlove]);
        var pieces = PiecesOf(root);
        var women = pieces.Single(p => p.GamePath == Glove);
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { MensGlove };
        var log = new RunLog();
        log.Add(women, SizeStatus.Made, null, null, men: true, gamePath: MensGlove);
        Assert.Equal([string.Format(Strings.SizeSet.AuthorsMensCrossedFmt, women.Label, MensGroup)],
                     CrossedLines(set, pieces, log, MensGroup));
        var refused = new RunLog();
        refused.Add(women, SizeStatus.Refused, Codes.RefusedMen, "x", men: true, gamePath: MensGlove);
        Assert.Empty(CrossedLines(set, pieces, refused, MensGroup));
        Assert.Empty(CrossedLines(new HashSet<string>(), pieces, log, MensGroup));
    }

    // ── an earlier fallback still standing over the author's model ────────

    /// <summary>A mod of <paramref name="paths"/> whose men's body group holds a men's glove of ours from an earlier run;
    /// the author's men's glove, when one of <paramref name="paths"/>, a model again (or, unless
    /// <paramref name="authorsModel"/>, still missing).</summary>
    private (string Root, List<Piece> Pieces) WithEarlierFallback(string[] paths, bool authorsModel = true)
    {
        string root = Mod(paths, extraGroups: OurGroup(MensGroup, MensGlove));
        Record(root, MensGroup, MensGlove);
        int at = Array.IndexOf(paths, MensGlove);
        if (authorsModel && at >= 0)
            foreach (string option in new[] { "s", "m" }) Write(root, $"{option}/{at}.mdl", Model);
        return (root, PiecesOf(root));
    }

    private static string Stale(Piece women) => string.Format(Strings.SizeSet.AuthorsMensStillReplacedFmt, women.Label, MensGroup);

    [Fact]
    [Trait("Plan", "AC-42-43-tbsex-legs-cross-3")]
    [Trait("Plan", "EDGE-42-43-tbsex-legs-cross-10")]
    [Trait("Plan", "EDGE-42-43-tbsex-legs-cross-14")]
    [Trait("Plan", "EDGE-42-43-tbsex-legs-cross-30")]
    [Trait("Plan", "EDGE-42-43-tbsex-legs-cross-31")]
    [Trait("Plan", "EDGE-42-43-tbsex-legs-cross-33")]
    public void Still_replaced_line() // [AC-42-43-tbsex-legs-cross-3] [EDGE-42-43-tbsex-legs-cross-10] [EDGE-42-43-tbsex-legs-cross-14] [EDGE-42-43-tbsex-legs-cross-30] [EDGE-42-43-tbsex-legs-cross-31] [EDGE-42-43-tbsex-legs-cross-33]
    {
        var (root, pieces) = WithEarlierFallback([Top, Glove, MensGlove]);
        var mens = pieces.Single(p => p.GamePath == MensGlove);
        var women = pieces.Single(p => p.GamePath == Glove);
        Assert.Equal(MensGroup, MensGroupName(root, AcrossPieces(pieces), pieces.Where(Crossable).ToList(), OwnSizeGroups(pieces)));

        void Says(RunLog log, Request request, IReadOnlyList<Piece>? run = null)
        {
            Across(request, run ?? pieces, pieces, log);
            Assert.Single(log.Warnings, w => w == Stale(women));
            Assert.DoesNotContain(string.Format(Strings.SizeSet.AuthorsMensFmt, women.Label), log.Notes);
            Assert.DoesNotContain(string.Format(Strings.SizeSet.AuthorsMensUntickedFmt, women.Label, "TBSE"), log.Notes);
            Assert.All(log.Sizes.Where(s => s.ReasonCode == Codes.AuthorsMens), s => Assert.Equal(Stale(women), s.Reason));
        }
        // The author's model now usable (sized this run).
        Says(Seeded(mens, (SizeStatus.Made, null)), Req(root, ["tbse", "tbse@women"]));
        // Not tried: only "for men" ticked.
        Says(Seeded(mens, (SizeStatus.Skipped, Codes.NoBodyForRace)), Req(root, ["tbse@women"]));
        // Refused.
        Says(Seeded(mens, (SizeStatus.Refused, Codes.RefusedPair)), Req(root, ["tbse", "tbse@women"]));
        // No "for men" body ticked at all.
        Says(Seeded(mens, (SizeStatus.Made, null)), Req(root, ["tbse"]));
        // Shoes and gloves left out: the glove is not among this run's pieces, but is in the mod's.
        Says(new RunLog(), Req(root, ["tbse", "tbse@women"], ends: false), pieces.Where(p => p.Slot == "_top").ToList());
    }

    [Fact]
    [Trait("Plan", "AC-42-43-tbsex-legs-cross-3")]
    [Trait("Plan", "EDGE-42-43-tbsex-legs-cross-36")]
    public void Attempted_fallback_with_prior_entry_says_nothing_stale() // [AC-42-43-tbsex-legs-cross-3] [EDGE-42-43-tbsex-legs-cross-36]
    {
        // The author's men's glove still missing: our earlier men's version is the men's model there, not one standing over
        // the author's — so no "pick Original to show the author's model" for a model that isn't there, however the run
        // goes (here the maps are missing; or only "for men" ticked, or a "Made on" body chosen).
        var (root, pieces) = WithEarlierFallback([Glove, MensGlove], authorsModel: false);
        var mens = pieces.Single(p => p.GamePath == MensGlove);
        var women = pieces.Single(p => p.GamePath == Glove);
        foreach (var request in new[] { Req(root, ["tbse", "tbse@women"]), Req(root, ["tbse-x@women"]),
                                        Req(root, ["tbse", "tbse@women"], madeOn: "Bibo+"), Req(root, ["tbse"]) })
        {
            var log = Seeded(mens, (SizeStatus.Skipped, Codes.EndsUnreadable));
            Across(request, pieces, pieces, log);
            Assert.DoesNotContain(log.Warnings, w => w == Stale(women));
            Assert.DoesNotContain(log.Sizes, s => s.Reason == Stale(women));
            // Nor "no men's version was made from it": ours from the earlier run is the men's model there.
            Assert.DoesNotContain(string.Format(Strings.SizeSet.AuthorsMensFmt, women.Label), log.Notes);
            Assert.DoesNotContain(log.Warnings, w => w == string.Format(Strings.SizeSet.AuthorsMensCrossedFmt, women.Label, MensGroup));
        }
    }

    [Fact]
    [Trait("Plan", "EDGE-42-43-tbsex-legs-cross-11")]
    public void Renamed_mens_group_is_still_recognised() // [EDGE-42-43-tbsex-legs-cross-11]
    {
        // An earlier run in another language named the men's versions' group "Corps masculin"; this run's language
        // would call it something else. Only the plain-body tick changes: "for men" alone.
        const string renamed = "Corps masculin";
        string root = Mod([Glove, MensGlove], extraGroups: OurGroup(renamed, MensGlove));
        Record(root, renamed, MensGlove);
        Write(root, "s/1.mdl", Model);   // the author's men's glove a model again
        var pieces = PiecesOf(root);
        var women = pieces.Single(p => p.GamePath == Glove);
        int groups = PenumbraModMeta.TryReadGroups(root)!.Count;
        Assert.Equal(renamed, MensGroupName(root, [], pieces.Where(Crossable).ToList(), OwnSizeGroups(pieces)));
        var log = Seeded(pieces.Single(p => p.GamePath == MensGlove), (SizeStatus.Skipped, Codes.NoBodyForRace));
        Across(Req(root, ["tbse@women"]), pieces, pieces, log);
        Assert.Single(log.Warnings, w => w == string.Format(Strings.SizeSet.AuthorsMensStillReplacedFmt, women.Label, renamed));
        Assert.Equal(groups, PenumbraModMeta.TryReadGroups(root)!.Count);
    }

    [Fact]
    [Trait("Plan", "EDGE-42-43-tbsex-legs-cross-34")]
    public void Kept_mens_group_says_the_top_is_still_replaced() // [EDGE-42-43-tbsex-legs-cross-34]
    {
        // The author's men's top in default files, sized into its own size group of ours ("TBSE" at its path); our men's
        // body group also holds a men's version at that path, made from the women's top by an earlier run.
        string defaults = $"\"{MensTop}\": \"default\\\\top.mdl\"";
        var mensTop = PiecesOf(Mod([Top], defaultFiles: defaults)).Single(p => p.GamePath == MensTop);
        string root = Mod([Top], defaultFiles: defaults,
                          extraGroups: OurGroup(mensTop.GroupName, MensTop) + OurGroup(MensGroup, MensTop));
        Directory.CreateDirectory(Path.Combine(root, SidecarDiscoveryService.SidecarSubdir));
        var record = new BodyRetargetWriter.Record
        {
            Group = MensGroup,
            Options =
            [
                new() { Group = mensTop.GroupName, Name = "TBSE", GamePath = MensTop, File = $"{BodyRetargetWriter.Subfolder}/men0.mdl" },
                new() { Group = MensGroup, Name = "TBSE", GamePath = MensTop, File = $"{BodyRetargetWriter.Subfolder}/men0.mdl" },
            ],
        };
        File.WriteAllText(Path.Combine(root, SidecarDiscoveryService.SidecarSubdir, BodyRetargetWriter.RecordFile),
                          JsonSerializer.Serialize(record));
        Write(root, "default/top.mdl", Model);   // the author's men's top: a model
        var pieces = PiecesOf(root);
        var women = pieces.Single(p => p.GamePath == Top);
        var log = Seeded(pieces.Single(p => p.GamePath == MensTop), (SizeStatus.Made, null));
        Across(Req(root, ["tbse", "tbse@women"]), pieces, pieces, log);
        Assert.Equal([string.Format(Strings.SizeSet.AuthorsMensStillReplacedFmt, women.Label, MensGroup)],
                     log.Warnings.Where(w => w.Contains("earlier run", StringComparison.Ordinal)));
    }

    [Fact]
    [Trait("Plan", "EDGE-42-43-tbsex-legs-cross-35")]
    public void No_stale_line_without_an_authors_mens_model() // [EDGE-42-43-tbsex-legs-cross-35]
    {
        var (root, pieces) = WithEarlierFallback([Top, Glove]);   // no men's glove of the author's: ours is the only one
        foreach (var request in new[] { Req(root, ["tbse"]), Req(root, ["tbse", "tbse@women"], across: false) })
        {
            var log = new RunLog();
            Across(request, pieces, pieces, log);
            Assert.DoesNotContain(log.Warnings, w => w.Contains(MensGroup, StringComparison.Ordinal));
        }
    }

    [Fact]
    [Trait("Plan", "EDGE-42-43-tbsex-legs-cross-12")]
    public void Mens_group_name_rejects_own_size_groups() // [EDGE-42-43-tbsex-legs-cross-12]
    {
        // The author's men's glove in default files: sized into a group of ours made for it, which holds nothing but a
        // "TBSE" option at the men's path — and comes first in the record.
        string defaults = $"\"{MensGlove}\": \"default\\\\glove.mdl\"";
        string sizeGroup = PiecesOf(Mod([Glove], defaultFiles: defaults)).Single(p => p.GamePath == MensGlove).GroupName;
        string root = Mod([Glove], defaultFiles: defaults,
                          extraGroups: OurGroup(sizeGroup, MensGlove) + OurGroup(MensGroup, MensGlove));
        Directory.CreateDirectory(Path.Combine(root, SidecarDiscoveryService.SidecarSubdir));
        var record = new BodyRetargetWriter.Record
        {
            Group = MensGroup,
            Options =
            [
                new() { Group = sizeGroup, Name = "TBSE", GamePath = MensGlove, File = $"{BodyRetargetWriter.Subfolder}/men0.mdl" },
                new() { Group = MensGroup, Name = "TBSE", GamePath = MensGlove, File = $"{BodyRetargetWriter.Subfolder}/men0.mdl" },
            ],
        };
        File.WriteAllText(Path.Combine(root, SidecarDiscoveryService.SidecarSubdir, BodyRetargetWriter.RecordFile),
                          JsonSerializer.Serialize(record));
        var pieces = PiecesOf(root);
        var crossable = pieces.Where(Crossable).ToList();
        Assert.Contains(sizeGroup, OwnSizeGroups(pieces));
        // Without the own size groups the first group of ours at the path is taken; with them, the men's body group.
        Assert.Equal(sizeGroup, MensGroupName(root, [], crossable));
        Assert.Equal(MensGroup, MensGroupName(root, [], crossable, OwnSizeGroups(pieces)));

        // That size group made while Proteus spoke another language: still the men's piece's own, never ours for men.
        // As a French run named it: French's group name around French's slot name, whatever this run's language is.
        string other = string.Format(French("Parts.Retarget.Group.Fmt"), $"{French("Common.Slot.Hands")} — e6000");
        Assert.NotEqual(pieces.Single(p => p.GamePath == MensGlove).GroupName, other);
        Assert.Contains(other, OwnSizeGroups(pieces));
        string elsewhere = Mod([Glove], defaultFiles: defaults,
                               extraGroups: OurGroup(other, MensGlove) + OurGroup(MensGroup, MensGlove));
        Directory.CreateDirectory(Path.Combine(elsewhere, SidecarDiscoveryService.SidecarSubdir));
        record.Options[0].Group = other;
        File.WriteAllText(Path.Combine(elsewhere, SidecarDiscoveryService.SidecarSubdir, BodyRetargetWriter.RecordFile),
                          JsonSerializer.Serialize(record));
        var there = PiecesOf(elsewhere);
        Assert.Equal(other, MensGroupName(elsewhere, [], there.Where(Crossable).ToList()));
        Assert.Equal(MensGroup, MensGroupName(elsewhere, [], there.Where(Crossable).ToList(), OwnSizeGroups(there)));
    }

    /// <summary>A message of the French locale file the plugin ships.</summary>
    private static string French(string key)
    {
        using var stream = typeof(SizeSetRefit).Assembly.GetManifestResourceStream("Proteus.Localization.fr.json")!;
        using var doc = JsonDocument.Parse(stream);
        return doc.RootElement.GetProperty(key).GetProperty("message").GetString()!;
    }

    [Fact]
    [Trait("Plan", "EDGE-42-43-tbsex-legs-cross-15")]
    public void Game_copy_is_not_a_fallback() // [EDGE-42-43-tbsex-legs-cross-15]
    {
        // No men's piece at the path (the game's own, or a copy of it, is none): crossed as ever, never a fallback.
        string root = Mod([Glove]);
        var pieces = PiecesOf(root);
        var log = new RunLog();
        Assert.Empty(MensModelsUnparsable(pieces, log, root, null));
        Assert.Contains(AcrossPieces(pieces), p => p.GamePath == Glove);
        Assert.Empty(CrossedLines(MensModelsUnparsable(pieces, log, root, null), pieces, log, MensGroup));
    }

    [Fact]
    [Trait("Plan", "EDGE-42-43-tbsex-legs-cross-16")]
    public void Following_legs_fallback_uses_the_lead_key() // [EDGE-42-43-tbsex-legs-cross-16]
    {
        const string Legs = "chara/equipment/e6000/model/c0201e6000_dwn.mdl";
        const string MensLegs = "chara/equipment/e6000/model/c0101e6000_dwn.mdl";
        string root = Mod([Top, Legs, MensLegs]);
        var pieces = PiecesOf(root);
        var log = Seeded(pieces.Single(p => p.GamePath == MensLegs), (SizeStatus.Skipped, Codes.Unreadable));
        var set = MensModelsUnparsable(pieces, log, root, null);
        var legs = AcrossPieces(pieces, set).Single(p => p.GamePath == Legs);
        Assert.True(legs.FollowsChest);
        Assert.Equal(LeadKey(legs.Group!, "0101", "TBSE"), AcrossLeadKey(legs, MensGroup, "TBSE"));
    }

    [Fact]
    [Trait("Plan", "EDGE-42-43-tbsex-legs-cross-40")]
    public void File_swaps_are_not_read() // [EDGE-42-43-tbsex-legs-cross-40]
    {
        // A known limit, pinned: an author FileSwap of the men's path to a working model is not seen.
        string root = Mod([Glove, MensGlove]);
        string meta = File.ReadAllText(Path.Combine(root, PenumbraModMeta.MetaFile))
                          .Replace("\"FileSwaps\": {}", $"\"FileSwaps\": {{ \"{MensGlove}\": \"chara/equipment/e0001/model/c0101e0001_glv.mdl\" }}");
        File.WriteAllText(Path.Combine(root, PenumbraModMeta.MetaFile), meta);
        Write(root, "s/1.mdl", []);
        Write(root, "m/1.mdl", []);
        Assert.True(AuthorMensRedirectsUnparsable(root, MensGlove, null));
    }

    // ── #46: a piece whose files are all missing is not told to choose "Made on" ───────────────

    [Fact]
    [Trait("Plan", "AC-issue-sweep-a-9")]
    public void A_piece_whose_files_are_all_missing_gets_no_made_on_advice()
    {
        var s = Strings.SizeSet;
        // The note: no Made-on advice once every file of the piece is unparsable; the same code either way.
        Assert.Equal((string.Format(s.UnreadableFmt, "Top"), Codes.Unreadable),
                     UnreadableNote("Top", null, null, false, [], noneParsed: true));
        Assert.Equal((string.Format(s.UnreadableMadeOnFmt, "Top"), Codes.Unreadable),
                     UnreadableNote("Top", null, null, false, [], noneParsed: false));

        // The piece's own files: missing (the Mod helper never writes them), empty, garbage.
        string missing = Mod([MensTop]);
        Assert.True(PieceFilesUnparsable(missing, PiecesOf(missing).Single(p => p.GamePath == MensTop)));
        string empty = Mod([MensTop]);
        Write(empty, "s/0.mdl", []);
        Write(empty, "m/0.mdl", [1, 2, 3, 4, 5, 6, 7, 8]);
        Assert.True(PieceFilesUnparsable(empty, PiecesOf(empty).Single(p => p.GamePath == MensTop)));
        // Both callers ask it, only without Made on.
        foreach (var file in new[] { "SizeSetRefit.cs", "SizeSetRefit.CrossSex.cs" })
            Assert.Contains("noneParsed: request.MadeOn == null && PieceFilesUnparsable(root, piece)",
                            File.ReadAllText(Path.Combine(SourceDir(), file)));
    }

    [Fact]
    [Trait("Plan", "EDGE-issue-sweep-a-22")]
    public void Made_on_advice_stays_when_a_file_reads()
    {
        // One file a model: the body may still be choosable, so the advice stays.
        string one = Mod([MensTop]);
        Write(one, "s/0.mdl", Model);
        Assert.False(PieceFilesUnparsable(one, PiecesOf(one).Single(p => p.GamePath == MensTop)));
        // A file that can't be opened (locked) is not counted.
        string locked = Mod([MensTop]);
        Write(locked, "s/0.mdl", []);
        Write(locked, "m/0.mdl", Model);
        using (new FileStream(Path.Combine(locked, "m", "0.mdl"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.False(PieceFilesUnparsable(locked, PiecesOf(locked).Single(p => p.GamePath == MensTop)));
        // A piece listing no files.
        var bare = PiecesOf(one).Single(p => p.GamePath == MensTop) with { Options = [] };
        Assert.False(PieceFilesUnparsable(one, bare));
    }

    [Fact]
    [Trait("Plan", "EDGE-issue-sweep-a-23")]
    public void Missing_mens_model_note_keeps_the_unreadable_code()
    {
        string root = Mod([Top, MensTop]);
        var pieces = PiecesOf(root);
        var mens = pieces.Single(p => p.GamePath == MensTop);
        var (_, code) = UnreadableNote(mens.Label, null, null, false, [], noneParsed: PieceFilesUnparsable(root, mens));
        Assert.Equal(Codes.Unreadable, code);
        var log = new RunLog();
        log.Add(mens, SizeStatus.Skipped, code, "x");
        Assert.Equal([MensTop], MensModelsUnparsable(pieces, log, root, null));   // still crossed (#43)
    }

    private static string SourceDir([System.Runtime.CompilerServices.CallerFilePath] string here = "")
        => Path.Combine(Path.GetDirectoryName(here)!, "..", "Proteus", "Services", "SizeSet");
}
