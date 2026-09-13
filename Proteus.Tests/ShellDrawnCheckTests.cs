using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// The post-redraw "is the second skin drawn?" check judges each shell material only while its OWN host is
/// drawn. Anchored on any host, a shell over a bracelet and a ring warned "never appeared" for the bracelet's
/// materials the moment the bracelet was taken off, because the ring was still there to anchor on.
/// </summary>
public class ShellDrawnCheckTests
{
    [Fact]
    public void Material_belongs_to_the_host_with_its_item_folder_and_slot()
    {
        Assert.True(CompositorService.SameShellHost(
            "chara/accessory/a0095/material/v0005/mt_c0201a0095_wrs_b.mtrl",
            "chara/accessory/a0095/model/c0201a0095_wrs.mdl"));
        Assert.True(CompositorService.SameShellHost(
            "chara/equipment/e5501/material/v0001/mt_c0201e5501_met_a.mtrl",
            "chara/equipment/e5501/model/c0201e5501_met.mdl"));
    }

    [Fact]
    public void Another_hosts_material_does_not_match()
    {
        // The 10:13 case: the ring is still drawn, the bracelet's materials must not be judged against it.
        Assert.False(CompositorService.SameShellHost(
            "chara/accessory/a0095/material/v0005/mt_c0201a0095_wrs_b.mtrl",
            "chara/accessory/a0053/model/c0201a0053_rir.mdl"));
        // Same set, different slot: the Emperor's ring and bracelet share a0053.
        Assert.False(CompositorService.SameShellHost(
            "chara/accessory/a0053/material/v0001/mt_c0201a0053_wrs_a.mtrl",
            "chara/accessory/a0053/model/c0201a0053_rir.mdl"));
        // Neighbouring slots that share a prefix: rir must not claim ril.
        Assert.False(CompositorService.SameShellHost(
            "chara/accessory/a0053/material/v0001/mt_c0201a0053_ril_a.mtrl",
            "chara/accessory/a0053/model/c0201a0053_rir.mdl"));
    }

    [Fact]
    public void Race_code_is_ignored_because_a_carrier_can_load_under_another()
    {
        // EQDP pulls a c0801 wearer's Emperor ring through to the c0201 model; the material keeps c0201.
        Assert.True(CompositorService.SameShellHost(
            "chara/accessory/a0053/material/v0001/mt_c0201a0053_rir_a.mtrl",
            "chara/accessory/a0053/model/c0801a0053_rir.mdl"));
    }
}
