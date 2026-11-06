using System.Collections.Generic;
using Penumbra.Api.Enums;
using Proteus.Interop;
using Xunit;

namespace Proteus.Tests;

/// <summary>The bridge helpers the test harness's equip and hold use: pure, compiled in every build.</summary>
public class HarnessBridgeWrapperTests
{
    // [EDGE-harness-equip-17]
    [Fact]
    public void StainList_TwoElementList()
    {
        List<byte> l = GlamourerBridge.StainList(3, 7);
        Assert.Equal([3, 7], l);
        Assert.Equal([0, 0], GlamourerBridge.StainList(0, 0));
    }

    // [EDGE-harness-equip-19] [EDGE-harness-equip-23]
    [Fact]
    public void ToTemporaryHold_Mapping()
    {
        var held = PenumbraBridge.ToTemporaryHold(PenumbraApiEc.Success, (false, 530), "Glamourer");
        Assert.Equal(new PenumbraBridge.TemporaryHold(true, false, "Glamourer", false, 530), held);

        foreach (var ec in new[] { PenumbraApiEc.Success, PenumbraApiEc.ModMissing, PenumbraApiEc.CollectionMissing })
            Assert.False(PenumbraBridge.ToTemporaryHold(ec, null, null)!.Value.Present);

        var locked = PenumbraBridge.ToTemporaryHold(PenumbraApiEc.TemporarySettingDisallowed, null, "OtherPlugin")!.Value;
        Assert.True(locked.Present);
        Assert.True(locked.Locked);
        Assert.Equal("OtherPlugin", locked.Source);
        Assert.Equal("unknown", PenumbraBridge.ToTemporaryHold(PenumbraApiEc.TemporarySettingDisallowed, null, "")!.Value.Source);

        Assert.Null(PenumbraBridge.ToTemporaryHold(PenumbraApiEc.UnknownError, null, null));
        Assert.Null(PenumbraBridge.ToTemporaryHold(PenumbraApiEc.SystemDisposed, (true, 1), "x"));
    }

    // [AC-harness-equip-11] [EDGE-harness-equip-20] [EDGE-harness-equip-38]
    [Fact]
    public void ToSettingsRead_ThreeWay()
    {
        var snap = new PenumbraBridge.ModSettingsSnapshot(true, 5, new Dictionary<string, List<string>>(), false, false);
        Assert.Equal(PenumbraBridge.SettingsReadKind.Ok, PenumbraBridge.ToSettingsRead(PenumbraApiEc.Success, snap).Kind);
        Assert.Equal(snap, PenumbraBridge.ToSettingsRead(PenumbraApiEc.Success, snap).Settings);
        Assert.Equal(PenumbraBridge.SettingsReadKind.None, PenumbraBridge.ToSettingsRead(PenumbraApiEc.Success, null).Kind);
        foreach (var ec in new[] { PenumbraApiEc.ModMissing, PenumbraApiEc.CollectionMissing, PenumbraApiEc.UnknownError })
        {
            var r = PenumbraBridge.ToSettingsRead(ec, snap);
            Assert.Equal(PenumbraBridge.SettingsReadKind.Failed, r.Kind);
            Assert.Null(r.Settings);
        }
    }
}
