using Xunit;

namespace Proteus.Tests.TestHarness;

/// <summary>
/// Tests that run a live loopback harness, a fake framework dispatcher or a thread hop against short timeouts, run
/// alone. Their timeouts (a 500 ms read timeout, 30–600 ms job timings, a start window) are tuned for an idle machine:
/// beside ~6,000 other tests running in parallel the thread pool starved them and a different two or three failed on
/// every quick-suite run (#49). xunit runs a collection that disables parallelisation after every parallel one has
/// finished, so nothing else is alive while these run. <c>HarnessTimingCollectionTests</c> holds the membership: every
/// class using <c>HarnessFixture</c> or <c>FakeFrameworkDispatcher</c>, plus <c>OffThreadTests</c>.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class HarnessTimingCollection
{
    public const string Name = "Harness timing, alone";
}
