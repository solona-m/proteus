@echo off
rem Rebuild proteus_bcn.dll (native SIMD BC7/BC5 encoder + BC1/3/5/7 decoder). Run on Windows with any Visual Studio 2019/2022
rem (Community/Pro/Enterprise/BuildTools) that has the C++ workload. Auto-locates VS via vswhere, so it is
rem not tied to any one machine and also works on GitHub's windows-latest runners.
rem   /MT = static CRT so the DLL depends only on KERNEL32 (no vcruntime140/msvcp140 at load time).
rem
rem DO NOT ADD /arch:AVX2 (or /arch:AVX). It was here until it crashed someone's game on a CPU without
rem AVX2. These sources contain no SIMD intrinsics at all, so the flag only lets MSVC autovectorise --
rem but with no runtime dispatch it also sprinkles AVX/BMI/LZCNT through ordinary scalar code. The DLL
rem then LOADS fine anywhere and dies of STATUS_ILLEGAL_INSTRUCTION on the first call, which no catch
rem can see: the CLR fail-fasts the game. Measured cost of leaving it off, 2048x2048, single-threaded:
rem BC7 encode 1.05x, BC7 decode 1.18x, BC5 either way -- and output is byte-identical, which matters
rem because Proteus names each baked texture by a hash of its content. A few percent is not worth
rem shipping a binary that cannot run on the machines it is shipped to.
setlocal
cd /d "%~dp0"

set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%VSWHERE%" (
  echo ERROR: vswhere.exe not found -- is Visual Studio installed?
  exit /b 1
)
for /f "usebackq tokens=*" %%i in (`"%VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VSPATH=%%i"
if not defined VSPATH (
  echo ERROR: no Visual Studio install with the C++ tools found.
  exit /b 1
)

call "%VSPATH%\VC\Auxiliary\Build\vcvars64.bat" >nul
cl /nologo /LD /O2 /MT /EHsc /DNDEBUG proteus_bcn.cpp bc7enc.c bc7decomp.cpp /Fe:proteus_bcn.dll /link /OPT:REF /OPT:ICF
if errorlevel 1 ( echo BUILD FAILED & exit /b 1 )

rem Ship it: copy up to native\ (the folder the csproj packages next to the plugin).
copy /y proteus_bcn.dll "..\proteus_bcn.dll" >nul
echo OK: built proteus_bcn.dll and copied to native\
