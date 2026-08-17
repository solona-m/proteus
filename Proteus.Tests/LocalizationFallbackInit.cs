using System.Runtime.CompilerServices;
using CheapLoc;

namespace Proteus.Tests;

/// <summary>
/// Puts the Proteus assembly into CheapLoc's "English fallbacks" state before any test runs, exactly as
/// <c>LocSetup</c> does at plugin load.
/// <para/>
/// This is not cosmetic. <c>Loc.Localize</c> does NOT return its fallback argument when the calling
/// assembly has never been set up — it returns <c>"#" + key</c>. So without this, every service test that
/// asserts on a user-facing message would be reading "#Import.Warn.OptionGroups.Fmt" instead of the
/// English sentence, and the tests that caught this were right to fail.
/// <para/>
/// The same trap applies at runtime to anything that could localize a string BEFORE
/// <c>LocSetup</c>'s constructor runs, which is why <c>Plugin</c> builds it first, ahead of every service
/// and window.
/// </summary>
internal static class LocalizationFallbackInit
{
    [ModuleInitializer]
    internal static void Init() => Loc.SetupWithFallbacks(typeof(Proteus.Plugin).Assembly);
}
