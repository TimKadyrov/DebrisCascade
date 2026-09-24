using System;
using System.Collections.Generic;

namespace DebrisCascade.Core;

/// <summary>
/// CPU reference implementation of the time-averaged spatial-density histogram: for each
/// object, sample its propagated position at every time and bin by geocentric altitude.
/// The CUDA kernel <c>sw_sample_density</c> must reproduce this exactly (integer counts).
/// </summary>
public static class DensitySampler
{
    /// <summary>
    /// Accumulate altitude-shell hit counts over all objects × sample times.
    /// <paramref name="epochRel"/>[i] is object i's epoch minus the reference epoch [s];
    /// each sample time is relative to that same reference.
    /// </summary>
    public static ulong[] Histogram(
        IReadOnlyList<OrbitalElements> objs, double[] epochRel, double[] times,
        double minAltKm, double binKm, int nBins)
    {
        var histo = new ulong[nBins];
        for (int i = 0; i < objs.Count; i++)
        {
            var el = objs[i];
            double eRel = epochRel[i];
            for (int s = 0; s < times.Length; s++)
            {
                double alt = el.PositionAt(times[s] - eRel).Length - Constants.EarthRadiusKm;
                int b = (int)Math.Floor((alt - minAltKm) / binKm);
                if (b >= 0 && b < nBins) histo[b]++;
            }
        }
        return histo;
    }
}
