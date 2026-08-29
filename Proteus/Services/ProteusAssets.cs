namespace Proteus.Services;

/// <summary>
/// Where the plugin's out-of-band assets are served from.
/// <para/>
/// Nothing large ships inside the plugin zip: Dalamud re-downloads that on every update, and with a
/// testing tag going out most days that multiplies by every tester. The UV maps and the starter effect
/// library are therefore release assets fetched once into the config directory, which survives updates.
/// </summary>
public static class ProteusAssets
{
    /// <summary>
    /// Edge cache in front of the GitHub release assets, or empty while none is deployed — in which
    /// case <see cref="BaseUrls"/> is simply GitHub alone and everything behaves as it always did.
    /// The mirror is a preference, never a dependency: GitHub always stays in the list behind it.
    /// <para/>
    /// Must end in a slash, and must serve <c>&lt;tag&gt;/&lt;file&gt;</c> exactly as the GitHub base
    /// does, so the two are interchangeable — see <c>worker/src/index.js</c>, which is deliberately
    /// shaped to mirror the origin's path layout rather than invent its own.
    /// Example: <c>https://dl.example.com/</c>
    /// </summary>
    public const string MirrorBase = "https://dl.solona.info/";

    public const string GitHubBase = "https://github.com/solona-m/proteus/releases/download/";

    /// <summary>Sources for one release tag, most-preferred first.</summary>
    public static string[] BaseUrls(string tag) =>
        MirrorBase.Length > 0
            ? [MirrorBase + tag + "/", GitHubBase + tag + "/"]
            : [GitHubBase + tag + "/"];
}
