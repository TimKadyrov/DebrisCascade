using System;
using System.Linq;
using DebrisCascade.Core;
using DebrisCascade.Interop;
using Xunit;

namespace DebrisCascade.Tests;

/// <summary>
/// Validates the CUDA engine against the CPU reference. These run only when a CUDA
/// device is present; on a machine without one they early-return (treated as passing).
/// </summary>
public class CudaTests
{
    private static OrbitalElements[] SampleCloud(int n)
    {
        double a = Constants.EarthRadiusKm + 550;
        double revPerDay = Math.Sqrt(Constants.Mu / (a * a * a)) * Constants.SecondsPerDay / Constants.TwoPi;
        var parent = OrbitalElements.FromMeanMotionRevPerDay(revPerDay, 0.0, 53 * Constants.DegToRad, 0, 0, 0);
        return new NailBarrel { Count = n }.Deploy(parent, 0, 60, seed: 7);
    }

    [Fact]
    public void DeviceIsDetected()
    {
        if (!Cuda.Available) return;
        Assert.False(string.IsNullOrWhiteSpace(Cuda.DeviceInfo));
        Assert.Contains("SM", Cuda.DeviceInfo);
    }

    [Fact]
    public void GpuPropagation_MatchesCpuReference()
    {
        if (!Cuda.Available) return;

        var cloud = SampleCloud(200);
        var epochRel = new double[cloud.Length]; // all share the release epoch
        double t = 3600.0;                        // one hour out

        double[] gpu = Cuda.Propagate(cloud, epochRel, t);

        double maxErr = 0;
        for (int i = 0; i < cloud.Length; i++)
        {
            var c = cloud[i].PositionAt(t);
            maxErr = Math.Max(maxErr, Math.Abs(gpu[3 * i] - c.X));
            maxErr = Math.Max(maxErr, Math.Abs(gpu[3 * i + 1] - c.Y));
            maxErr = Math.Max(maxErr, Math.Abs(gpu[3 * i + 2] - c.Z));
        }
        Assert.True(maxErr < 1e-3, $"max GPU/CPU position error {maxErr} km"); // < 1 metre
    }

    [Fact]
    public void GpuDensityHistogram_MatchesCpuReference()
    {
        if (!Cuda.Available) return;

        var cloud = SampleCloud(2000);
        var epochRel = new double[cloud.Length];
        // 96 samples spread across ~1 day to average orbital phase.
        var times = Enumerable.Range(0, 96).Select(k => k * 900.0).ToArray();
        double minAlt = 200, binKm = 25; int nBins = 72;

        ulong[] gpu = Cuda.SampleDensity(cloud, epochRel, times, minAlt, binKm, nBins);
        ulong[] cpu = DensitySampler.Histogram(cloud, epochRel, times, minAlt, binKm, nBins);

        double totalCpu = cpu.Sum(x => (double)x);
        double l1 = 0;
        for (int b = 0; b < nBins; b++) l1 += Math.Abs((double)gpu[b] - cpu[b]);

        Assert.True(totalCpu > 0);
        // Allow only tiny boundary-rounding differences between the two transcendental impls.
        Assert.True(l1 / totalCpu < 0.004, $"histogram L1 mismatch {l1 / totalCpu:P3}");
    }
}
