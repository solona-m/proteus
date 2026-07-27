@echo off
rem Rebuild proteus_bcn.dll (native SIMD BC7/BC5 encoder). Run on Windows with any Visual Studio 2019/2022
rem (Community/Pro/Enterprise/BuildTools) that has the C++ workload. Auto-locates VS via vswhere, so it is
rem not tied to any one machine and also works on GitHub's windows-latest runners.
rem   /MT = static CRT so the DLL depends only on KERNEL32 (no vcruntime140/msvcp140 at load time).
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
cl /nologo /LD /O2 /MT /EHsc /DNDEBUG /arch:AVX2 proteus_bcn.cpp bc7enc.c /Fe:proteus_bcn.dll /link /OPT:REF /OPT:ICF
if errorlevel 1 ( echo BUILD FAILED & exit /b 1 )

rem Ship it: copy up to native\ (the folder the csproj packages next to the plugin).
copy /y proteus_bcn.dll "..\proteus_bcn.dll" >nul
echo OK: built proteus_bcn.dll and copied to native\
