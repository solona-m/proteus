using System.Collections.Generic;
using System.Linq;

namespace Proteus.Services;

/// <summary>
/// What became of every size a run considered, for a caller that sorts mods by it (the headless size set's report, see
/// <see cref="SizeSetBatch"/>). Kept beside the notes, never instead of them: the panel shows the notes as before.
/// </summary>
internal static partial class SizeSetRefit
{
    internal enum SizeStatus { Made, Refused, Skipped, Failed }

    /// <summary>How a made size came out, measured on the plan that was saved.</summary>
    /// <param name="Folded">Cloth faces the refit turned over (<see cref="CrossSexRefit.ClothFolded(byte[], BodyRetarget.Planned, System.Action{string}?)"/>),
    /// the same count for every kind of size; 0 for a shoe or glove whose shape is the author's.</param>
    /// <param name="SkinOnly">A shoe or glove whose shape is the author's, only its skin redrawn: no plan, so its moves
    /// are 0 and the rest unknown.</param>
    internal sealed record Measures(int Folded, int? Unmatched, float? WorstMoveMm, float? WorstPushMm, int? Pushed,
                                    float? SnapRate, int? ShapeFallbacks, double? Coverage = null, bool SkinOnly = false)
    {
        internal static Measures Of(BodyRetarget.Planned plan, int folded, double? coverage = null)
            => new(folded, plan.Report.Missed, plan.Report.WorstMove * 1000f, plan.Report.WorstPush * 1000f,
                   plan.Report.Pushed, plan.Report.SnapRate, plan.Report.ShapeFallbacks, coverage);

        internal static readonly Measures SkinOnlyEnd = new(0, null, 0f, 0f, null, null, null, SkinOnly: true);
    }

    /// <summary>One size of one piece, and what became of it.</summary>
    /// <param name="Body">The size table's key ("rue", "tbse").</param>
    /// <param name="BodyMod">The body mod's folder it was refitted onto.</param>
    /// <param name="ReasonCode">Why it was not made, for a script; <see cref="Reason"/> says it for a person.</param>
    /// <param name="From">What it was cut from.</param>
    internal sealed record SizeOutcome(string Piece, string GamePath, string Slot, string? Group, string? Option,
                                       string? Body, string? BodyMod, bool Yas, bool Men, SizeStatus Status,
                                       string? ReasonCode, string? Reason, string? From = null, Measures? Measures = null,
                                       double? Seconds = null);

    /// <summary>
    /// Everything a run says: its notes and warnings, as the panel shows them, and every size's outcome. Made by the
    /// caller and filled as the run goes, so a run stopped partway still tells what it saved.
    /// </summary>
    internal sealed class RunLog
    {
        internal List<string> Notes { get; } = [];
        internal List<string> Warnings { get; } = [];
        internal List<SizeOutcome> Sizes { get; } = [];

        /// <summary>Sizes made: a legs or shoe size saved into its chest's option counts on its own.</summary>
        internal int Made => Sizes.Count(s => s.Status == SizeStatus.Made);

        internal void Add(Piece piece, SizeStatus status, string? code, string? reason, string? group = null,
                          string? option = null, string? body = null, string? bodyMod = null, bool yas = false,
                          bool men = false, string? from = null, Measures? measures = null, double? seconds = null,
                          string? gamePath = null)
            => Sizes.Add(new SizeOutcome(piece.Label, gamePath ?? piece.GamePath, piece.Slot, group, option, body, bodyMod,
                                         yas, men, status, code, reason, from, measures, seconds));

        /// <summary>A note the author reads, kept as the size's reason too.</summary>
        internal void Note(string note, Piece piece, SizeStatus status, string code, string? group = null,
                           string? option = null, string? body = null, string? bodyMod = null, bool yas = false,
                           bool men = false, string? gamePath = null)
        {
            Notes.Add(note);
            Add(piece, status, code, note, group, option, body, bodyMod, yas, men, gamePath: gamePath);
        }
    }

    /// <summary>A mod with no chest, legs, shoe or glove of its own: said once, for the whole mod.</summary>
    internal static void NoPieces(RunLog runLog)
    {
        runLog.Notes.Add(Localization.Strings.SizeSet.NoPieces);
        runLog.Sizes.Add(new SizeOutcome("", "", "", null, null, null, null, false, false, SizeStatus.Skipped,
                                         Codes.NoPieces, Localization.Strings.SizeSet.NoPieces));
    }

    /// <summary>The <see cref="SizeOutcome.ReasonCode"/>s.</summary>
    internal static class Codes
    {
        internal const string RefusedPair = "refused-pair";
        internal const string RefusedMen = "refused-men";
        internal const string NotInstalled = "not-installed";
        internal const string NoSuchSize = "no-such-size";
        internal const string AlreadyThere = "already-there";
        internal const string AuthorsOwn = "authors-own";
        internal const string MadeOnIt = "made-on-it";
        internal const string NoChestOption = "no-chest-option";
        internal const string NoOwnLegs = "no-own-legs";
        internal const string NoSource = "no-source";
        internal const string NoSourceMen = "no-source-men";
        internal const string Unreadable = "unreadable";
        internal const string EndsUnreadable = "ends-unreadable";
        internal const string Unpaired = "unpaired";
        internal const string EndNoLead = "end-no-lead";
        internal const string NoPieces = "no-pieces";
        internal const string NoBodies = "no-bodies";
        internal const string NoBodyForRace = "no-body-for-race";
        internal const string MenGroupTaken = "men-group-taken";
        internal const string MenMapsMissing = "men-maps-missing";
        internal const string NoGameData = "no-game-data";
        internal const string EndsNotForMen = "ends-not-for-men";
        internal const string SameSkin = "same-skin";
        internal const string NoEnd = "no-end";
        internal const string SaveFailed = "save-failed";
        internal const string PieceFailed = "piece-failed";
    }
}
