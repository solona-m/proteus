using System;
using System.IO;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// The size set says why it refused a size — in its notes and in its log — whatever refused the pair. Its one real-data
/// case (TBSE's SFW legs onto TBSE-X's) is made now that nothing is refused for folds (cross-sex-tops-yas), and no pair
/// the size set can target still refuses, so the wiring is pinned here until a synthetic size-set case can drive a
/// coverage refusal (a follow-up).
/// </summary>
public class SizeSetRefusalLogTests
{
    [Fact]
    [Trait("Plan", "AC-gen2-legs-refit-7")]
    public void A_refused_pair_is_said_in_the_notes_and_the_log()
    {
        string source = File.ReadAllText(SourceFile("Proteus/Services/SizeSet/SizeSetRefit.cs"));
        int pair = source.IndexOf("if (BodyRetarget.BuildPair(piece.Slot, chosen.SourcePath, targetPath", StringComparison.Ordinal);
        Assert.True(pair > 0, "the size set no longer pairs its bodies where it did");
        string refused = source.Substring(pair, 700);
        Assert.Contains("notes.Add(string.Format(Strings.SizeSet.RefusedFmt, piece.Label, option, refusal));", refused);
        Assert.Contains("log.Information(\"[Proteus] size set: {0} {1}: refused: {2}\", piece.Label, option, refusal);", refused);
        Assert.Contains("{2}", Localization.Strings.SizeSet.RefusedFmt);
    }

    private static string SourceFile(string file)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, file);
            if (File.Exists(p)) return p;
        }
        throw new FileNotFoundException(file);
    }
}
