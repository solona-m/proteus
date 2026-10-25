using System.Reflection;

namespace Proteus;

/// <summary>
/// The one way Proteus asks CheapLoc for a string: the plugin's own assembly is passed, never worked out.
/// <para/>
/// CheapLoc's two-argument <c>Loc.Localize</c> finds the table by <c>Assembly.GetCallingAssembly()</c>, and the
/// "calling assembly" is whatever frame the JIT left above it. A lambda handed to LINQ, a small getter read inside
/// one, an async state machine: once inlined into System.Linq or CoreLib, the caller is theirs, and CheapLoc returns
/// <c>"#" + key</c> — players saw "#HairPort.Refused.NotInMod" while every test passed, because the inlining only
/// happens after the code warms up. Passing the assembly makes the lookup the same wherever it is called from, and
/// skips the stack walk too.
/// <para/>
/// Both arguments must be literals, as CheapLoc's exporter needs: <c>LocalizationTests.CodeKeysMatchEnglishJson</c>
/// reads them from the IL before each call to this method, and <c>OnlyProteusLocCallsCheapLoc</c> keeps every other
/// call out.
/// </summary>
internal static class ProteusLoc
{
    private static readonly Assembly Self = typeof(ProteusLoc).Assembly;

    public static string Localize(string key, string fallBack) => CheapLoc.Loc.Localize(key, fallBack, Self);
}
