@echo off
REM Build the DebrisCascade CUDA engine into debriscascade_cuda.dll.
REM Uses the VS 18 x64 toolchain as nvcc's host compiler.
call "C:\Program Files\Microsoft Visual Studio\18\Enterprise\VC\Auxiliary\Build\vcvars64.bat" >nul
cd /d "%~dp0"
nvcc -O3 -arch=sm_120 --shared -o debriscascade_cuda.dll debriscascade_cuda.cu -allow-unsupported-compiler
echo nvcc exit code: %ERRORLEVEL%
