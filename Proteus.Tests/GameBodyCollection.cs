using Xunit;

namespace Proteus.Tests;

/// <summary>
/// Tests that read the game's own body (<see cref="Services.VanillaBodyCatalog"/>). Its catalog is one cache for the whole
/// process, its models extracted to one folder, and <see cref="VanillaRefitTests"/> clears both and fills them with
/// synthetic bodies: run beside it, a test could read those, or find the files gone halfway. So these never run in
/// parallel with each other or with anything else.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GameBodyCollection
{
    public const string Name = "Game body";
}
