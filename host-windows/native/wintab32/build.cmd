@echo off
rem SPDX-License-Identifier: GPL-3.0-only
rem Divit wintab32.dll: x64 and x86 (for 32-bit apps). Requires Visual Studio "Desktop development with C++".
rem Output: out\x64\wintab32.dll, out\x86\wintab32.dll
setlocal
set HERE=%~dp0
for /f "usebackq delims=" %%i in (`"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set VS=%%i
if not defined VS (
  echo ERROR: Visual Studio C++ tools not found. Add the "Desktop development with C++" workload in the Visual Studio Installer.
  exit /b 1
)
call :build x64 || exit /b 1
call :build x86 || exit /b 1
exit /b 0

:build
rem Each architecture in its own environment (so vcvarsall does not accumulate). Note: do not use the name "LINK"; link.exe reads it as options.
setlocal
call "%VS%\VC\Auxiliary\Build\vcvarsall.bat" %1 >nul || exit /b 1
if not exist "%HERE%out\%1" mkdir "%HERE%out\%1"
pushd "%HERE%out\%1"
cl /nologo /O2 /W4 /WX /GS /guard:cf /MT /LD /utf-8 /D_CRT_SECURE_NO_WARNINGS /DUNICODE /D_UNICODE "%HERE%wintab32.c" /Fe:wintab32.dll ^
   /link /DEF:"%HERE%wintab32.def" /DYNAMICBASE /NXCOMPAT /guard:cf user32.lib kernel32.lib || (popd & exit /b 1)
del /q *.obj *.exp 2>nul
popd
echo %1: %HERE%out\%1\wintab32.dll
endlocal
exit /b 0
