using Xunit;

namespace Proteus.Tests;

/// <summary>
/// Tests that clear or fake the game's body in <see cref="Proteus.Services.VanillaBodyCatalog"/>, run alone. Its
/// extracts are one folder and one cache for the whole process: <c>VanillaRefitTests</c> deletes them and reads synthetic
/// bodies into them, which beside a test reading the real game's body failed it with a missing file, or could hand it a
/// fake body. xunit runs a collection that disables parallelisation after every parallel one has finished, so nothing
/// else is alive while these run.
/// <para/>
/// Tests that only read the real game's body do not belong here: <c>Read</c> takes the catalog's lock, so they run in
/// parallel with each other. Putting them here cost 20 minutes of a 25-minute suite. <c>GameBodyCollectionTests</c>
/// holds the membership to <c>VanillaRefitTests</c>.
/// <para/>
/// Nor do stubs that find nothing — a reader that returns null or throws: the catalog shares a body any reader found,
/// but believes "no body" only of the reader that said so. What still has to run here is a test that clears, deletes or
/// moves the catalog's folder, or that serves synthetic body bytes through a reader.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GameBodyCollection
{
    public const string Name = "The game's body";
}
