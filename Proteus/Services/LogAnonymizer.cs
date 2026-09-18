using System;
using System.IO;
using System.Text.RegularExpressions;

namespace Proteus.Services;

/// <summary>
/// Strips the Windows user name out of a log before Copy Logs writes it, so the file can be posted publicly.
/// The name reaches the log inside paths: the profile folder, AppData, a OneDrive desktop.
/// </summary>
internal static class LogAnonymizer
{
    public const string Placeholder = "<USER>";

    // A Users folder on any drive, either slash, and the doubled backslashes of escaped JSON. The name runs to the next
    // separator; spaces are allowed, as in "C:\Users\Jane Doe\AppData".
    private static readonly Regex UsersFolder = new(
        @"\b([A-Za-z]:[\\/]+Users[\\/]+)([^\\/:*?""<>|\r\n]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string Scrub(string text) =>
        Scrub(text, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    /// <param name="profileDir">
    /// The user's profile folder. Covered separately because it need not live under \Users (a redirected profile such as
    /// D:\Profiles\jane).
    /// </param>
    public static string Scrub(string text, string? profileDir)
    {
        if (string.IsNullOrEmpty(text)) return text;

        text = UsersFolder.Replace(text, m =>
            // "Public" and "Default" are shared folders, not anyone's name.
            m.Groups[2].Value.Equals("Public", StringComparison.OrdinalIgnoreCase)
            || m.Groups[2].Value.Equals("Default", StringComparison.OrdinalIgnoreCase)
                ? m.Value
                : m.Groups[1].Value + Placeholder);

        var trimmed = profileDir?.TrimEnd('\\', '/');
        var parent = string.IsNullOrEmpty(trimmed) ? null : Path.GetDirectoryName(trimmed);
        if (!string.IsNullOrEmpty(parent))
        {
            var anonymous = Path.Combine(parent, Placeholder);
            foreach (var (from, to) in new[]
                     {
                         (trimmed!, anonymous),
                         (trimmed!.Replace('\\', '/'), anonymous.Replace('\\', '/')),
                         (trimmed!.Replace("\\", "\\\\"), anonymous.Replace("\\", "\\\\")),
                     })
                text = text.Replace(from, to, StringComparison.OrdinalIgnoreCase);
        }
        return text;
    }
}
