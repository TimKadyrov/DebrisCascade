using System.IO;
using SpaceWars.Core;
using Xunit;

namespace SpaceWars.Tests;

public class SatcatTests
{
    private const string Csv =
        "OBJECT_NAME,OBJECT_ID,NORAD_CAT_ID,OBJECT_TYPE,OPS_STATUS_CODE,OWNER,LAUNCH_DATE,LAUNCH_SITE,DECAY_DATE,PERIOD,INCLINATION,APOGEE,PERIGEE,RCS,DATA_STATUS_CODE,ORBIT_CENTER,ORBIT_TYPE\n" +
        "SL-1 R/B,1957-001A,1,R/B,D,CIS,1957-10-04,TYMSC,1957-12-01,96.19,65.10,938,214,20.4200,,EA,IMP\n" +
        "TESTSAT,2020-999A,99999,PAY,,US,2020-01-01,X,,90,50,500,480,0.1000,,EA,ORB\n" +
        "NORCS DEBRIS,2020-999B,88888,DEB,,US,2020-01-01,X,,90,50,500,480,,,EA,ORB\n";

    [Fact]
    public void Load_ParsesTypeAndRcs()
    {
        var map = Satcat.Load(new StringReader(Csv));
        Assert.Equal(3, map.Count);
        Assert.Equal("R/B", map[1].ObjectType);
        Assert.Equal(20.42, map[1].RcsM2!.Value, 2);
        Assert.Null(map[88888].RcsM2);          // blank RCS
        Assert.True(Satcat.IsIntact(map[99999].ObjectType));
        Assert.False(Satcat.IsIntact(map[88888].ObjectType));
    }

    private const string SpaceTrackCsv =
        "NORAD_CAT_ID,OBJECT_TYPE,RCS_SIZE\n" +
        "1,ROCKET BODY,LARGE\n" +
        "99999,PAYLOAD,SMALL\n" +
        "88888,DEBRIS,\n" +
        "77777,PAYLOAD,MEDIUM\n";

    [Fact]
    public void SpaceTrack_ParsesTypesAndRcsSizeCategories()
    {
        var map = SpaceTrackClient.ParseSatcatCsv(new StringReader(SpaceTrackCsv));
        Assert.Equal(4, map.Count);
        Assert.Equal("R/B", map[1].ObjectType);          // "ROCKET BODY" → R/B
        Assert.Equal(5.0, map[1].RcsM2!.Value, 3);        // LARGE
        Assert.Equal("PAY", map[99999].ObjectType);
        Assert.Equal(0.05, map[99999].RcsM2!.Value, 3);   // SMALL
        Assert.Equal(0.5, map[77777].RcsM2!.Value, 3);    // MEDIUM
        Assert.Null(map[88888].RcsM2);                    // blank category
    }

    [Fact]
    public void DeriveMassArea_RocketBodyIsHeavy_DebrisIsLight()
    {
        var map = Satcat.Load(new StringReader(Csv));
        var (rbMass, rbArea) = Satcat.DeriveMassArea(map[1]);
        Assert.Equal(20.42, rbArea, 2);
        Assert.InRange(rbMass, 1000, 6000);      // ~2.8 t rocket body

        var (payMass, _) = Satcat.DeriveMassArea(map[99999]);
        Assert.InRange(payMass, 1, 40);          // small ~0.1 m^2 payload

        var (_, debArea) = Satcat.DeriveMassArea(map[88888]);
        Assert.Equal(0.3, debArea, 3);           // type default for missing RCS
    }
}
