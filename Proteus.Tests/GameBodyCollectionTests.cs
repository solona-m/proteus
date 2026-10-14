using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// Who runs alone in <see cref="GameBodyCollection"/>: the one class that clears and fakes the game's body, and nobody
/// else. A reader added to it puts the whole real-data suite back in single file (that was 20 of 25 minutes); a writer
/// left out of it clears the game's body under a reader running beside it.
/// </summary>
public class GameBodyCollectionTests
{
    [Fact]
    [Trait("Plan", "AC-test-cleanup-6")]
    public void Only_VanillaRefitTests_runs_in_the_game_body_collection()   // [AC-test-cleanup-6]
    {
        // By the attribute's constructor argument: CollectionAttribute keeps no Name property in xunit v2.
        var members = typeof(GameBodyCollection).Assembly.GetTypes()
            .Where(t => t.CustomAttributes.Any(a => a.AttributeType == typeof(CollectionAttribute)
                                                    && a.ConstructorArguments is [{ Value: GameBodyCollection.Name }]))
            .ToList();
        Assert.Equal([typeof(VanillaRefitTests)], members);

        var definition = typeof(GameBodyCollection).GetCustomAttribute<CollectionDefinitionAttribute>();
        Assert.NotNull(definition);
        Assert.True(definition.DisableParallelization);
    }

    [Fact]
    [Trait("Plan", "AC-test-cleanup-6")]
    public void Only_VanillaRefitTests_clears_or_moves_the_game_body()   // [AC-test-cleanup-6]
    {
        // The membership above only matters if the writers are its members. TestAssemblySetup sets the folder once,
        // before any test, and clears it as the process ends; anyone else doing either belongs in the collection.
        var writes = new Regex(@"VanillaBodyCatalog\s*\.\s*(CleanUp\s*\(|Dir\s*=[^=])");
        var writers = Directory.EnumerateFiles(TestsDir(), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(f => writes.IsMatch(File.ReadAllText(f)))
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToList();
        Assert.Equal(["TestAssemblySetup.cs", "VanillaRefitTests.cs"], writers);
    }

    /// <summary>This file's own folder, as the compiler saw it: the test sources.</summary>
    private static string TestsDir([CallerFilePath] string self = "") => Path.GetDirectoryName(self)!;
}
