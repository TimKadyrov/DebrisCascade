using System.IO;
using System.Linq;
using DebrisCascade.Core;
using Xunit;

namespace DebrisCascade.Tests;

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
        Assert.Equal(12.0, map[1].RcsM2!.Value, 3);       // LARGE rocket body → 12 m² (~1.5 t), not the 5 m² midpoint
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

        var (debMass, debArea) = Satcat.DeriveMassArea(map[88888]);
        Assert.Equal(0.3, debArea, 3);           // type default for missing RCS
        // Debris is a breakup fragment: far lighter than an intact object of the same size.
        Assert.True(debMass < 0.25 * BreakupModel.IntactMassFromLc(BreakupModel.LcFromArea(0.3)), $"debris {debMass:F2} kg");
    }

    [Fact]
    public void LargeBelt_OnlyForCatalogsWithoutDerelicts()
    {
        var el = OrbitalElements.FromMeanMotionRevPerDay(14.2, 0.001, 1.0, 0, 0, 0);
        var active = Enumerable.Repeat(new CatalogObject(el, 300, 3, true, "PAY"), 5000).ToList();
        var full = active.Concat(Enumerable.Repeat(new CatalogObject(el, 1, 0.05, false, "DEB"), 3000)).ToList();
        Assert.Equal(DebrisEnvironment.ModelledLargeBeltTotal, DebrisEnvironment.LargeBeltFor(active));
        Assert.Equal(0, DebrisEnvironment.LargeBeltFor(full));
    }
}
