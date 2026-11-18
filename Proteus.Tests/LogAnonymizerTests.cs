using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// Tests for <see cref="LogAnonymizer"/>: Copy Logs files get posted publicly, so no Windows user name may survive in
/// a path, whichever way the path was written.
/// </summary>
public class LogAnonymizerTests
{
    [Theory]
    [InlineData(@"C:\Users\jane\AppData\Roaming\XIVLauncher\dalamud.log", @"C:\Users\<USER>\AppData\Roaming\XIVLauncher\dalamud.log")]
    [InlineData(@"c:\users\JANE\OneDrive\Desktop\x.txt", @"c:\users\<USER>\OneDrive\Desktop\x.txt")]
    [InlineData("C:/Users/jane/AppData/Local/Temp", "C:/Users/<USER>/AppData/Local/Temp")]
    [InlineData(@"{""path"":""C:\\Users\\jane\\AppData""}", @"{""path"":""C:\\Users\\<USER>\\AppData""}")]
    [InlineData(@"loaded 'D:\Users\Jane Doe\mods\a.tex' ok", @"loaded 'D:\Users\<USER>\mods\a.tex' ok")]
    [InlineData(@"file:///C:/Users/jane/Desktop", "file:///C:/Users/<USER>/Desktop")]
    public void UsersFolderNameIsReplaced(string input, string expected)
        => Assert.Equal(expected, LogAnonymizer.Scrub(input, profileDir: null));

    [Fact]
    public void EveryOccurrenceOnALineIsReplaced()
        => Assert.Equal(@"C:\Users\<USER>\a -> C:\Users\<USER>\b",
            LogAnonymizer.Scrub(@"C:\Users\jane\a -> C:\Users\jane\b", profileDir: null));

    [Theory]
    [InlineData(@"C:\Users\Public\Documents")]
    [InlineData(@"C:\Users\Default\AppData")]
    [InlineData(@"E:\Penumbradt\chara\human\c0101\obj\body\b0001\texture\c0101b0001_d.tex")]
    [InlineData("chara/equipment/e0041/material/v0001/mt_c0201e0041_top_a.mtrl")]
    public void PathsWithNoUserNameAreUntouched(string input)
        => Assert.Equal(input, LogAnonymizer.Scrub(input, profileDir: null));

    [Fact]
    public void ARedirectedProfileOutsideUsersIsReplaced()
        => Assert.Equal(@"cfg at D:\Profiles\<USER>\AppData and D:/Profiles/<USER>/x",
            LogAnonymizer.Scrub(@"cfg at D:\Profiles\jane\AppData and D:/Profiles/jane/x", @"D:\Profiles\jane"));

    // [AC-mod-path-containment-7][EDGE-mod-path-containment-26] The harness's rules: whole tokens, any case, longest
    // first; short names and the built-in collection left; a name that is both is a collection.
    [Theory]
    [InlineData("Jane Doe drew c0801", "<CHARACTER> drew c0801")]
    [InlineData("JANE DOE and jane doe", "<CHARACTER> and <CHARACTER>")]
    [InlineData("Rue is true, Rue_S and Rues stay", "<COLLECTION> is true, Rue_S and Rues stay")]
    [InlineData("Rue Mod over Rue", "<COLLECTION> over <COLLECTION>")]
    [InlineData("Ab and Default stay", "Ab and Default stay")]
    [InlineData("My (Main)+ collection", "<COLLECTION> collection")]
    public void CharacterAndCollectionNamesAreReplaced(string input, string expected)
        => Assert.Equal(expected, LogAnonymizer.Scrub(input, null, ["Rue", "Rue Mod", "Ab", "Default", "My (Main)+"], "Jane Doe"));

    [Fact]
    public void ANameThatIsBothIsACollection()
        => Assert.Equal("<COLLECTION> is both", LogAnonymizer.Scrub("Twin is both", null, ["Twin"], "Twin"));

    // [AC-mod-path-containment-7][EDGE-mod-path-containment-26] A collection named after a log tag keeps the tag; one
    // named after a body is still the user's, and goes.
    [Fact]
    public void LogTagNamedCollectionKeepsTheTag()
    {
        Assert.Equal("[Proteus] refresh", LogAnonymizer.Scrub("[Proteus] refresh", null, ["Proteus", "penumbra"], "Glamourer"));
        Assert.Equal("<COLLECTION> body", LogAnonymizer.Scrub("Rue body", null, ["Rue"], null));
    }

    // [EDGE-mod-path-containment-27]
    [Fact]
    public void UserNameIsStillScrubbedWithNames()
        => Assert.Equal(@"C:\Users\<USER>\x <CHARACTER>",
            LogAnonymizer.Scrub(@"C:\Users\jane\x Jane Doe", null, null, "Jane Doe"));
}
