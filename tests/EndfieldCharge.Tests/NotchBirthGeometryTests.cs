using EndfieldCharge.Services;
using Xunit;
namespace EndfieldCharge.Tests;

public sealed class NotchBirthGeometryTests
{
    [Theory]
    [InlineData(0.4)]
    [InlineData(0.8)]
    [InlineData(1.0)]
    [InlineData(1.2)]
    public void BirthStripIsEntirelyInsideNotchHeight(double scale)
    {
        var g = NotchBirthGeometry.FromLayout(1512, 32, scale, 756, 92);
        Assert.True(g.IsActive);
        Assert.True(g.BirthTop >= 0);
        Assert.True(g.BirthTop + 60 * g.BirthScaleY * scale < 32);
        Assert.True(560 * g.BirthScaleX * scale <= 120.001);
    }

    [Theory]
    [InlineData(-1, 0.8)]
    [InlineData(0, 0.8)]
    [InlineData(1, 0.8)]
    [InlineData(-1, 1.2)]
    [InlineData(1, 1.2)]
    public void AllPositionsStartAtNotchAndEndInsideScreen(int side, double scale)
    {
        double center = NotchBirthGeometry.FinalCenter(1512, scale, side);
        var g = NotchBirthGeometry.FromLayout(1512, 32, scale, center, 92);
        // BirthHost is outside GlobalScale: translation is applied after scaling.
        Assert.Equal(756, center + g.OffX, 6);
        Assert.Equal(g.BirthTop, 92 + g.OffY, 6);
        Assert.True(center - 280 * scale >= 20);
        Assert.True(center + 280 * scale <= 1492);
        Assert.Equal(-side, System.Math.Sign(g.OffX));
    }

    [Fact]
    public void NoNotchDisablesBirth()
    {
        var g = NotchBirthGeometry.FromLayout(1920, 0, 0.8, 960, 90);
        Assert.False(g.IsActive);
        Assert.Equal(0, g.OffX);
        Assert.Equal(0, g.OffY);
    }
}
