#if PROTEUS_TEST_HARNESS
namespace Proteus.Services;

/// <summary>
/// The test harness's view into the colour editor's write-behind (ADR 0002): whether a mod has an edit held, so the
/// harness never saves over, discards or races a user's unsaved edit. Compiled out of every CI build. Any thread.
/// </summary>
public partial class SidecarDiscoveryService
{
    internal bool HarnessHasStaged(string modDir) => staged.Peek(modDir) != null;
}
#endif
