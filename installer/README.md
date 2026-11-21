# Windows installer

`divit.iss` is an [Inno Setup 6](https://jrsoftware.org/isinfo.php) script (free). `build.ps1` builds everything in order:

1. the WinTab DLLs (x64 + x86) with `host-windows/native/wintab32/build.cmd`;
2. a **self-contained** publish of the host app (users do not need to install .NET) into `installer/staging/`;
3. the installer `installer/Output/DivitSetup-<version>.exe` plus its SHA-256 checksum.

```bash
powershell -ExecutionPolicy Bypass -File installer/build.ps1 -Version 0.9.3
```

Requirements: .NET 10 SDK, Visual Studio with "Desktop development with C++", Inno Setup 6.

## What the installer does

| Step | Details |
|---|---|
| Language | English or Turkish (follows Windows; the user can choose) |
| Location | `Program Files\Divit` (per machine, one UAC prompt) |
| Options | **WinTab support** (on by default), **USB connection support** (on by default), **start with Windows** (on by default), desktop icon (off by default) |
| WinTab | Copies `wintab32.dll` to `System32` (64-bit) and `SysWOW64` (32-bit) — only if no other tablet driver's WinTab file is there; otherwise it leaves that file alone and says so |
| USB (adb) | Downloads Google's Android Platform-Tools (`platform-tools-latest-windows.zip`, about 8 MB, HTTPS) and unpacks it to `Program Files\Divit\platform-tools`. The option is hidden when an `adb` is already installed (Android SDK, `ANDROID_HOME`/`ANDROID_SDK_ROOT` or `PATH`): Divit uses that one, and two different adb versions would fight over the adb server |
| Firewall | Adds an inbound Windows Firewall rule for `Divit.exe` so Wi-Fi works without Windows' "allow access" prompt. Any older rule for the same program is replaced first (cancelling that prompt leaves a Block rule that silently stops Wi-Fi). |
| Finish | Starts Divit as the normal user (not elevated) |
| Uninstall | Stops Divit, removes the app, its Start menu/desktop entries, the autostart value, the firewall rule and Divit's own WinTab files (never another driver's) and the downloaded `platform-tools`. User settings in `%APPDATA%\Divit` are kept. |

`adb` is not bundled inside the installer: Google's SDK license does not allow redistributing it, so setup downloads it from Google. Divit looks for `adb` in this order: Android SDK, next to `Divit.exe` in `platform-tools\`, `PATH`.

Testing the download option on a PC that already has the Android SDK: build with `ISCC.exe /DForceAdbTask installer\divit.iss`.
