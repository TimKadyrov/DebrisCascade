// DebrisCascade CUDA engine: batch J2-secular propagation + time-averaged spatial-density
// accumulation over the full nail + catalog population. The device propagator mirrors
// DebrisCascade.Core.OrbitalElements.PositionAt so GPU results validate against the C# ref.
//
// Build (from an MSVC x64 dev shell):
//   nvcc -O3 -arch=sm_120 --shared -o debriscascade_cuda.dll debriscascade_cuda.cu

#include <cuda_runtime.h>
#include <math.h>
#include <string.h>
#include <stdio.h>

#define SW_MU   398600.8       // km^3/s^2 (WGS-72, matches Core.Constants)
#define SW_RE   6378.135       // km
#define SW_PI   3.14159265358979323846
#define SW_2PI  6.28318530717958647692

// Mean elements + secular rates for one object. MUST match the C# [StructLayout]
// DebrisCascade.Interop.ObjElem field order exactly (10 sequential doubles).
struct ObjElem {
    double a;        // semi-major axis [km]
    double e;        // eccentricity
    double inc;      // inclination [rad]
    double raan0;    // RAAN at epoch [rad]
    double argp0;    // arg perigee at epoch [rad]
    double m0;       // mean anomaly at epoch [rad]
    double mDot;     // secular mean-anomaly rate [rad/s]
    double raanDot;  // nodal regression rate [rad/s]
    double argpDot;  // apsidal precession rate [rad/s]
    double epochRel; // object epoch minus reference epoch [s]
};

__device__ __forceinline__ double solveKepler(double M, double e) {
    double m = fmod(M, SW_2PI);
    if (m > SW_PI) m -= SW_2PI; else if (m < -SW_PI) m += SW_2PI;
    double E = e < 0.8 ? m : SW_PI;
#pragma unroll 8
    for (int it = 0; it < 30; ++it) {
        double f = E - e * sin(E) - m;
        double fp = 1.0 - e * cos(E);
        double d = f / fp;
        E -= d;
        if (fabs(d) < 1e-12) break;
    }
    return E;
}

// Position [km] of one object at t seconds since ITS OWN epoch.
__device__ __forceinline__ void posAt(const ObjElem& o, double t,
                                       double& X, double& Y, double& Z) {
    double raan = o.raan0 + o.raanDot * t;
    double argp = o.argp0 + o.argpDot * t;
    double M    = o.m0    + o.mDot    * t;

    double ecc = solveKepler(M, o.e);
    double sinNu = sqrt(1.0 - o.e * o.e) * sin(ecc);
    double cosNu = cos(ecc) - o.e;
    double nu = atan2(sinNu, cosNu);
    double r  = o.a * (1.0 - o.e * cos(ecc));

    double xp = r * cos(nu), yp = r * sin(nu);
    double cO = cos(raan), sO = sin(raan);
    double cI = cos(o.inc), sI = sin(o.inc);
    double cW = cos(argp), sW = sin(argp);

    double r11 =  cO * cW - sO * sW * cI;
    double r12 = -cO * sW - sO * cW * cI;
    double r21 =  sO * cW + cO * sW * cI;
    double r22 = -sO * sW + cO * cW * cI;
    double r31 =  sW * sI;
    double r32 =  cW * sI;

    X = r11 * xp + r12 * yp;
    Y = r21 * xp + r22 * yp;
    Z = r31 * xp + r32 * yp;
}

// Full ECI state (position [km] + velocity [km/s]) at t seconds since the object's epoch.
__device__ __forceinline__ void stateAt(const ObjElem& o, double t,
                                         double* P, double* V) {
    double raan = o.raan0 + o.raanDot * t;
    double argp = o.argp0 + o.argpDot * t;
    double M    = o.m0    + o.mDot    * t;

    double ecc = solveKepler(M, o.e);
    double sinNu = sqrt(1.0 - o.e * o.e) * sin(ecc);
    double cosNu = cos(ecc) - o.e;
    double nu = atan2(sinNu, cosNu);
    double r  = o.a * (1.0 - o.e * cos(ecc));

    double p = o.a * (1.0 - o.e * o.e);
    double h = sqrt(SW_MU * p);

    double xp = r * cos(nu), yp = r * sin(nu);
    double vxp = -(SW_MU / h) * sin(nu);
    double vyp =  (SW_MU / h) * (o.e + cos(nu));

    double cO = cos(raan), sO = sin(raan);
    double cI = cos(o.inc), sI = sin(o.inc);
    double cW = cos(argp), sW = sin(argp);
    double r11 =  cO * cW - sO * sW * cI, r12 = -cO * sW - sO * cW * cI;
    double r21 =  sO * cW + cO * sW * cI, r22 = -sO * sW + cO * cW * cI;
    double r31 =  sW * sI,               r32 =  cW * sI;

    P[0] = r11 * xp + r12 * yp; P[1] = r21 * xp + r22 * yp; P[2] = r31 * xp + r32 * yp;
    V[0] = r11 * vxp + r12 * vyp; V[1] = r21 * vxp + r22 * vyp; V[2] = r31 * vxp + r32 * vyp;
}

// One thread per object: full state (x,y,z,vx,vy,vz) at a single reference time.
__global__ void kPropState(const ObjElem* o, int n, double sampleTimeRel, double* out6) {
    int i = blockIdx.x * blockDim.x + threadIdx.x;
    if (i >= n) return;
    double P[3], V[3];
    stateAt(o[i], sampleTimeRel - o[i].epochRel, P, V);
    out6[6 * i + 0] = P[0]; out6[6 * i + 1] = P[1]; out6[6 * i + 2] = P[2];
    out6[6 * i + 3] = V[0]; out6[6 * i + 4] = V[1]; out6[6 * i + 5] = V[2];
}

// One thread per object: position at a single reference time.
__global__ void kProp(const ObjElem* o, int n, double sampleTimeRel, double* outXYZ) {
    int i = blockIdx.x * blockDim.x + threadIdx.x;
    if (i >= n) return;
    double X, Y, Z;
    posAt(o[i], sampleTimeRel - o[i].epochRel, X, Y, Z);
    outXYZ[3 * i + 0] = X;
    outXYZ[3 * i + 1] = Y;
    outXYZ[3 * i + 2] = Z;
}

// One thread per object: accumulate an altitude histogram over nt sample times.
__global__ void kDensity(const ObjElem* o, int n, const double* times, int nt,
                         double minAltKm, double binKm, int nBins,
                         unsigned long long* histo) {
    int i = blockIdx.x * blockDim.x + threadIdx.x;
    if (i >= n) return;
    ObjElem e = o[i];
    for (int s = 0; s < nt; ++s) {
        double X, Y, Z;
        posAt(e, times[s] - e.epochRel, X, Y, Z);
        double alt = sqrt(X * X + Y * Y + Z * Z) - SW_RE;
        int b = (int)floor((alt - minAltKm) / binKm);
        if (b >= 0 && b < nBins) atomicAdd(&histo[b], 1ULL);
    }
}

static int launchBlocks(int n, int tpb) { return (n + tpb - 1) / tpb; }

extern "C" {

__declspec(dllexport) int sw_device_info(char* buf, int buflen) {
    int dev = 0, count = 0;
    if (cudaGetDeviceCount(&count) != cudaSuccess || count == 0) return -1;
    cudaGetDevice(&dev);
    cudaDeviceProp p;
    if (cudaGetDeviceProperties(&p, dev) != cudaSuccess) return -2;
    snprintf(buf, buflen, "%s | SM %d.%d | %.1f GB | %d SMs",
             p.name, p.major, p.minor,
             p.totalGlobalMem / 1073741824.0, p.multiProcessorCount);
    return 0;
}

__declspec(dllexport) int sw_propagate(const ObjElem* host_o, int n,
                                       double sampleTimeRel, double* host_xyz) {
    ObjElem* d_o = nullptr; double* d_xyz = nullptr;
    cudaError_t st;
    st = cudaMalloc(&d_o, (size_t)n * sizeof(ObjElem));      if (st) goto fail;
    st = cudaMalloc(&d_xyz, (size_t)n * 3 * sizeof(double)); if (st) goto fail;
    st = cudaMemcpy(d_o, host_o, (size_t)n * sizeof(ObjElem), cudaMemcpyHostToDevice); if (st) goto fail;
    kProp<<<launchBlocks(n, 256), 256>>>(d_o, n, sampleTimeRel, d_xyz);
    st = cudaGetLastError();       if (st) goto fail;
    st = cudaDeviceSynchronize();  if (st) goto fail;
    st = cudaMemcpy(host_xyz, d_xyz, (size_t)n * 3 * sizeof(double), cudaMemcpyDeviceToHost); if (st) goto fail;
fail:
    if (d_o) cudaFree(d_o);
    if (d_xyz) cudaFree(d_xyz);
    return (int)st;
}

__declspec(dllexport) int sw_propagate_state(const ObjElem* host_o, int n,
                                             double sampleTimeRel, double* host_state6) {
    ObjElem* d_o = nullptr; double* d_s = nullptr;
    cudaError_t st;
    st = cudaMalloc(&d_o, (size_t)n * sizeof(ObjElem));       if (st) goto fail;
    st = cudaMalloc(&d_s, (size_t)n * 6 * sizeof(double));    if (st) goto fail;
    st = cudaMemcpy(d_o, host_o, (size_t)n * sizeof(ObjElem), cudaMemcpyHostToDevice); if (st) goto fail;
    kPropState<<<launchBlocks(n, 256), 256>>>(d_o, n, sampleTimeRel, d_s);
    st = cudaGetLastError();      if (st) goto fail;
    st = cudaDeviceSynchronize(); if (st) goto fail;
    st = cudaMemcpy(host_state6, d_s, (size_t)n * 6 * sizeof(double), cudaMemcpyDeviceToHost); if (st) goto fail;
fail:
    if (d_o) cudaFree(d_o);
    if (d_s) cudaFree(d_s);
    return (int)st;
}

__declspec(dllexport) int sw_sample_density(const ObjElem* host_o, int n,
                                            const double* host_times, int nt,
                                            double minAltKm, double binKm, int nBins,
                                            unsigned long long* host_histo) {
    ObjElem* d_o = nullptr; double* d_t = nullptr; unsigned long long* d_h = nullptr;
    cudaError_t st;
    st = cudaMalloc(&d_o, (size_t)n * sizeof(ObjElem));            if (st) goto fail;
    st = cudaMalloc(&d_t, (size_t)nt * sizeof(double));            if (st) goto fail;
    st = cudaMalloc(&d_h, (size_t)nBins * sizeof(unsigned long long)); if (st) goto fail;
    st = cudaMemcpy(d_o, host_o, (size_t)n * sizeof(ObjElem), cudaMemcpyHostToDevice); if (st) goto fail;
    st = cudaMemcpy(d_t, host_times, (size_t)nt * sizeof(double), cudaMemcpyHostToDevice); if (st) goto fail;
    st = cudaMemset(d_h, 0, (size_t)nBins * sizeof(unsigned long long)); if (st) goto fail;
    kDensity<<<launchBlocks(n, 256), 256>>>(d_o, n, d_t, nt, minAltKm, binKm, nBins, d_h);
    st = cudaGetLastError();       if (st) goto fail;
    st = cudaDeviceSynchronize();  if (st) goto fail;
    st = cudaMemcpy(host_histo, d_h, (size_t)nBins * sizeof(unsigned long long), cudaMemcpyDeviceToHost); if (st) goto fail;
fail:
    if (d_o) cudaFree(d_o);
    if (d_t) cudaFree(d_t);
    if (d_h) cudaFree(d_h);
    return (int)st;
}

} // extern "C"
