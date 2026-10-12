using Xunit;

namespace Proteus.Tests;

/// <summary>
/// Tests that read the game's body through <see cref="Proteus.Services.VanillaBodyCatalog"/>, run one at a time. Its
/// extracts are one folder for the whole process: <c>VanillaRefitTests</c> deletes it and writes synthetic bodies into
/// it, which beside a test reading the real game's body failed it with a missing file, or could hand it a fake body.
/// Nor beside anything else: its catalog is one cache for the whole process too.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GameBodyCollection
{
    public const string Name = "The game's body";
}
