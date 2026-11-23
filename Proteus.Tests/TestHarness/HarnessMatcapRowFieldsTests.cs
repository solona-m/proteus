using System.Text.Json;
using Proteus.TestHarness;
using Xunit;

namespace Proteus.Tests.TestHarness;

/// <summary>Row matcaps, the harness route's fields: a colour row with its sub-row or material, refused when malformed.</summary>
public class HarnessMatcapRowFieldsTests
{
    [Theory]
    [Trait("Plan", "AC-matcap-row-10")]
    [InlineData("{\"row\":15,\"sub\":\"B\"}", 15, true)]
    [InlineData("{\"row\":17}", 0, false)]
    [InlineData("{\"row\":\"x\"}", 0, false)]
    [InlineData("{\"sub\":\"A\"}", 0, false)]
    [InlineData("{\"material\":\"/mt_a.mtrl\"}", 0, false)]
    [InlineData("{\"row\":3,\"sub\":\"C\"}", 0, false)]
    [InlineData("{\"row\":3,\"material\":\"/mt_a.mtrl\"}", 3, true)]
    public void Matcap_route_validates_row_sub_material(string json, int row, bool ok)
    {
        using var doc = JsonDocument.Parse(json);
        var (r, _, _, bad) = HarnessRouter.RowFields(doc.RootElement);
        Assert.Equal(ok, bad == null);
        if (ok) Assert.Equal(row, r);
    }
}
