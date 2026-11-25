using System.Linq;
using Proteus.TestHarness;
using Xunit;

namespace Proteus.Tests.TestHarness;

/// <summary>The harness's pinned emote rows answer to their text commands on the game's Emote sheet (knee standoff QA).</summary>
public class HarnessEmoteRowsLocalTests
{
    [LocalDataFact(LocalData.GameData)]
    [Trait("Plan", "AC-knee-standoff-8")]
    public void Pinned_emote_rows_are_sit_and_groundsit()
    {
        var game = new Lumina.GameData(LocalData.Path(LocalData.GameData));
        var sheet = game.GetExcelSheet<Lumina.Excel.Sheets.Emote>()!;
        var rows = HarnessPose.Allowlist.Values.ToDictionary(v => v.Id, v =>
        {
            var t = sheet.GetRowOrDefault(v.Id)?.TextCommand.ValueNullable;
            return t is { } c
                ? HarnessPose.JoinCommands(c.Command.ExtractText(), c.ShortCommand.ExtractText(), c.Alias.ExtractText(), c.ShortAlias.ExtractText())
                : "";
        });
        // Row 50 "Sit" is /lounge with /sit as its alias; row 52 "Sit on Ground" is /groundsit.
        foreach (var (name, (id, command)) in HarnessPose.Allowlist)
            Assert.True(rows[id].Split('|').Contains(command), $"{name}: row {id} answers to '{rows[id]}', not {command}");
        Assert.True(HarnessPose.RowsMatch(rows));
    }
}
