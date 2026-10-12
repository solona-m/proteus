using Xunit;

namespace Proteus.Tests;

/// <summary>
/// Tests that read the game's body through <see cref="Proteus.Services.VanillaBodyCatalog"/>, run one at a time. Its
/// extracts are one folder for the whole process: <c>VanillaRefitTests</c> deletes it and writes synthetic bodies into
/// it, which beside a test reading the real game's body failed it with a missing file, or could hand it a fake body.
/// </summary>
[CollectionDefinition(Name)]
public sealed class GameBodyCollection
{
    public const string Name = "The game's body";
}
