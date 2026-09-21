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
}
