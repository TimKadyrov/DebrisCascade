using System;
using DebrisCascade.Core;
using Xunit;

namespace DebrisCascade.Tests;

public class TleTests
{
    [Theory]
    [InlineData("25544", 25544)]
    [InlineData(" 1234", 1234)]
    [InlineData("A0000", 100000)]   // Alpha-5: A = 10
    [InlineData("H9999", 179999)]   // H = 17
    [InlineData("J0001", 180001)]   // I is skipped, so J = 18
    [InlineData("P0000", 230000)]   // O is skipped too, so P = 23
    [InlineData("Z9999", 339999)]   // Z = 33
    public void CatalogNumber_DecodesPlainAndAlpha5(string field, int expected)
        => Assert.Equal(expected, Tle.ParseCatalogNumber(field));

    [Theory]
    [InlineData("I0000")]
    [InlineData("O1234")]
    [InlineData("12a45")]
    public void CatalogNumber_RejectsInvalidFields(string field)
        => Assert.Throws<FormatException>(() => Tle.ParseCatalogNumber(field));

    [Fact]
    public void AdvancedElements_SitWhereTheOriginalIsAtThatMoment()
    {
        // A catalog seeded at one instant must put each object where its own elements say it is then, not at its epoch.
        var el = OrbitalElements.FromMeanMotionRevPerDay(14.2, 0.002, 1.5, 0.3, 1.1, 0.0);
        double dt = 3.7 * 86400;                         // an element set 3.7 days older than the catalog instant
        var adv = el.AdvancedBy(dt);
        foreach (double t in new[] { 0.0, 600.0, 86400.0 })
        {
            var a = el.PositionAt(dt + t); var b = adv.PositionAt(t);
            Assert.True((a - b).Length < 1e-6, $"t={t}: {(a - b).Length} km apart");
        }
    }
}
