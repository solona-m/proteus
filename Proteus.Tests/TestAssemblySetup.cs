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
    /// <remarks>And thrown away when the run ends, so runs leave no folder each behind.</remarks>
    [ModuleInitializer]
    internal static void KeepTheGameBodyApart()
    {
        VanillaBodyCatalog.Dir = Path.Combine(Path.GetTempPath(), $"proteus-vanilla-body-tests-{Environment.ProcessId}");
        AppDomain.CurrentDomain.ProcessExit += (_, _) => VanillaBodyCatalog.CleanUp();
        // The bodies bent for a picked race likewise: set once here, since tests in different classes bend at once and
        // a folder swapped or deleted under one of them by another would lose its bodies halfway.
        BakedBodyCatalog.Dir = Path.Combine(Path.GetTempPath(), $"proteus-baked-body-tests-{Environment.ProcessId}");
        AppDomain.CurrentDomain.ProcessExit += (_, _) => BakedBodyCatalog.CleanUp();
    }
}
