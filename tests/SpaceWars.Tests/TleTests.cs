using System;
using SpaceWars.Core;
using Xunit;

namespace SpaceWars.Tests;

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
}
