using System;
using System.IO;
using System.Runtime.CompilerServices;
using Proteus.Services;

namespace Proteus.Tests;

/// <summary>What the whole test run needs before any test does.</summary>
internal static class TestAssemblySetup
{
    /// <summary>
    /// The game's body is extracted into a folder of this run's own, not the plugin's: a game running beside the tests
    /// clears the plugin's as it loads (Proteus's own reload included), and the tests would find their extracts gone.
    /// </summary>
    [ModuleInitializer]
    internal static void KeepTheGameBodyApart()
        => VanillaBodyCatalog.Dir = Path.Combine(Path.GetTempPath(), $"proteus-vanilla-body-tests-{Environment.ProcessId}");
}
