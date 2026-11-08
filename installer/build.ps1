# SPDX-License-Identifier: GPL-3.0-only
# Copyright (C) 2026 Divit contributors
#
# Builds the Windows installer: WinTab DLLs (x64 + x86) → self-contained app publish → Inno Setup.
# Requirements: .NET 10 SDK, Visual Studio "Desktop development with C++", Inno Setup 6.
# Usage (from the repository root):  powershell -ExecutionPolicy Bypass -File installer\build.ps1 [-Version 0.9.2]

param([string]$Version = "0.9.2")
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$staging = Join-Path $PSScriptRoot "staging"

# 1. WinTab DLLs
& (Join-Path $root "host-windows\native\wintab32\build.cmd")
if ($LASTEXITCODE -ne 0) { throw "WinTab DLL build failed" }

# 2. App: self-contained, so users do not need to install the .NET runtime
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
dotnet publish (Join-Path $root "host-windows\src\Divit.Host\Divit.Host.csproj") -c Release -r win-x64 --self-contained true `
    -p:Version=$Version -o (Join-Path $staging "app")
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
New-Item -ItemType Directory -Force (Join-Path $staging "wintab\x64"), (Join-Path $staging "wintab\x86") | Out-Null
Copy-Item (Join-Path $root "host-windows\native\wintab32\out\x64\wintab32.dll") (Join-Path $staging "wintab\x64\")
Copy-Item (Join-Path $root "host-windows\native\wintab32\out\x86\wintab32.dll") (Join-Path $staging "wintab\x86\")

# 3. Installer
$iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe", "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 (ISCC.exe) not found" }
& $iscc "/DAppVersion=$Version" (Join-Path $PSScriptRoot "divit.iss")
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed" }

$setup = Join-Path $PSScriptRoot "Output\DivitSetup-$Version.exe"
$hash = (Get-FileHash $setup -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  DivitSetup-$Version.exe" | Set-Content -Encoding ascii (Join-Path $PSScriptRoot "Output\DivitSetup-$Version.exe.sha256")
Write-Host "Installer: $setup"
Write-Host "SHA-256:   $hash"
