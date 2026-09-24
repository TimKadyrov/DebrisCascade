using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using DebrisCascade.Core;

namespace DebrisCascade.Interop;

/// <summary>
/// Native object layout passed to the CUDA kernels. Field order MUST match
/// <c>struct ObjElem</c> in debriscascade_cuda.cu exactly (10 sequential doubles).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct ObjElem
{
    public double A, E, Inc, Raan0, Argp0, M0, MDot, RaanDot, ArgpDot, EpochRel;

    public static ObjElem From(in OrbitalElements el, double epochRelSeconds) => new()
    {
        A = el.SemiMajorAxis, E = el.Eccentricity, Inc = el.Inclination,
        Raan0 = el.Raan, Argp0 = el.ArgPerigee, M0 = el.MeanAnomaly,
        MDot = el.MeanAnomalyDot, RaanDot = el.RaanDot, ArgpDot = el.ArgPerigeeDot,
        EpochRel = epochRelSeconds,
    };
}

internal static class NativeMethods
{
    private const string Lib = "debriscascade_cuda";

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int sw_device_info(byte[] buf, int buflen);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int sw_propagate(ObjElem[] o, int n, double sampleTimeRel, double[] outXyz);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int sw_propagate_state(ObjElem[] o, int n, double sampleTimeRel, double[] outState6);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int sw_sample_density(ObjElem[] o, int n, double[] times, int nt,
        double minAltKm, double binKm, int nBins, ulong[] outHisto);
}

/// <summary>
/// Managed entry point to the CUDA engine. Gracefully reports unavailability when the
/// DLL, driver, or a CUDA device is missing, so callers/tests can fall back to the CPU.
/// </summary>
public static class Cuda
{
    private static bool? _available;
    private static string _device = "";

    /// <summary>True if the native DLL loaded and a CUDA device is present.</summary>
    public static bool Available
    {
        get
        {
            if (_available is null) Probe();
            return _available!.Value;
        }
    }

    /// <summary>Human-readable device description, or the reason it is unavailable.</summary>
    public static string DeviceInfo
    {
        get { _ = Available; return _device; }
    }

    private static void Probe()
    {
        try
        {
            var buf = new byte[256];
            int rc = NativeMethods.sw_device_info(buf, buf.Length);
            if (rc == 0)
            {
                int len = Array.IndexOf(buf, (byte)0);
                _device = Encoding.ASCII.GetString(buf, 0, len < 0 ? buf.Length : len);
                _available = true;
            }
            else { _device = $"no CUDA device (rc={rc})"; _available = false; }
        }
        catch (DllNotFoundException) { _device = "debriscascade_cuda.dll not found"; _available = false; }
        catch (BadImageFormatException) { _device = "debriscascade_cuda.dll architecture mismatch"; _available = false; }
        catch (Exception ex) { _device = $"CUDA init failed: {ex.Message}"; _available = false; }
    }

    private static void Check(int rc, string op)
    {
        if (rc != 0) throw new InvalidOperationException($"CUDA {op} failed (error {rc}).");
    }

    /// <summary>Batch-propagate ECI positions [km] at one reference time. Returns a flat x,y,z array.</summary>
    public static double[] Propagate(IReadOnlyList<OrbitalElements> objs, double[] epochRel, double sampleTimeRel)
    {
        var arr = ToNative(objs, epochRel);
        var xyz = new double[objs.Count * 3];
        Check(NativeMethods.sw_propagate(arr, arr.Length, sampleTimeRel, xyz), "propagate");
        return xyz;
    }

    /// <summary>
    /// Batch-propagate full ECI state at one reference time. Returns a flat array of
    /// [x,y,z,vx,vy,vz] per object (km, km/s) — used by the conjunction (cube) cascade.
    /// </summary>
    public static double[] PropagateState(IReadOnlyList<OrbitalElements> objs, double[] epochRel, double sampleTimeRel)
    {
        var arr = ToNative(objs, epochRel);
        var state = new double[objs.Count * 6];
        Check(NativeMethods.sw_propagate_state(arr, arr.Length, sampleTimeRel, state), "propagate_state");
        return state;
    }

    /// <summary>
    /// Time-averaged altitude histogram over the given sample times, accumulated on the GPU.
    /// Returns the per-shell hit counts (Σ over objects × samples).
    /// </summary>
    public static ulong[] SampleDensity(IReadOnlyList<OrbitalElements> objs, double[] epochRel,
        double[] times, double minAltKm, double binKm, int nBins)
    {
        var arr = ToNative(objs, epochRel);
        var histo = new ulong[nBins];
        Check(NativeMethods.sw_sample_density(arr, arr.Length, times, times.Length,
            minAltKm, binKm, nBins, histo), "sample_density");
        return histo;
    }

    private static ObjElem[] ToNative(IReadOnlyList<OrbitalElements> objs, double[] epochRel)
    {
        if (epochRel.Length != objs.Count)
            throw new ArgumentException("epochRel length must match objs count.");
        var arr = new ObjElem[objs.Count];
        for (int i = 0; i < objs.Count; i++) arr[i] = ObjElem.From(objs[i], epochRel[i]);
        return arr;
    }
}
