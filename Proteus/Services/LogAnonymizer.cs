using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

    public const string CharacterPlaceholder = "<CHARACTER>";
    public const string CollectionPlaceholder = "<COLLECTION>";

    /// <summary>Names never scrubbed: the log's own tags ("[Proteus]") would go with them. Body names are not here —
    /// a collection named after one is still the user's, and privacy wins over readability.</summary>
    internal static readonly string[] LogTagStoplist = ["Proteus", "Penumbra", "Glamourer", "Dalamud"];

    /// <summary>Penumbra's built-in collection: not personal, and scrubbing it would garble every "default".</summary>
    private const string BuiltInDefault = "Default";
    private const int MinNameLength = 3;

    /// <summary>
    /// <see cref="Scrub(string, string?)"/>, then the logged-in character's name and every Penumbra collection name, so a
    /// posted log names neither (a collection is often named after a character). The rules are the test harness's
    /// (<c>TestHarness/LogRing.cs</c> SessionNames — keep the two alike): whole tokens only ("Rue" goes, "true" and "Rue_S"
    /// stay), any case, longest first; names under 3 characters and the built-in "Default" collection are left; a name
    /// that is both is a collection. Plus <see cref="LogTagStoplist"/>.
    /// </summary>
    public static string Scrub(string text, string? profileDir, IEnumerable<string>? collections, string? character)
    {
        text = Scrub(text, profileDir);
        if (string.IsNullOrEmpty(text)) return text;

        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? name, string placeholder)
        {
            name = name?.Trim();
            if (name is not { Length: >= MinNameLength } || names.ContainsKey(name)) return;
            if (LogTagStoplist.Contains(name, StringComparer.OrdinalIgnoreCase)) return;
            if (placeholder == CollectionPlaceholder && name.Equals(BuiltInDefault, StringComparison.OrdinalIgnoreCase)) return;
            names[name] = placeholder;
        }
        foreach (var c in collections ?? []) Add(c, CollectionPlaceholder);
        Add(character, CharacterPlaceholder);

        foreach (var (name, placeholder) in names.OrderByDescending(n => n.Key.Length))
            text = Regex.Replace(text, @"(?<![A-Za-z0-9_])" + Regex.Escape(name) + @"(?![A-Za-z0-9_])", placeholder,
                                 RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return text;
    }
}
