; SPDX-License-Identifier: GPL-3.0-only
; Copyright (C) 2026 Divit contributors
;
; Divit Windows installer (Inno Setup 6). Build with installer\build.ps1, which publishes the app into
; installer\staging first. Manual build: ISCC.exe /DAppVersion=0.9.2 installer\divit.iss
;
; - Per-machine install into Program Files (administrator, one UAC prompt).
; - English / Turkish (the language of the setup wizard follows Windows; the user can pick).
; - Optional: desktop icon, start with Windows, WinTab support (System32 + SysWOW64).
; - WinTab rule: another tablet driver's wintab32.dll (Wacom etc.) is never overwritten or deleted.
; - Divit is started as the normal (non-elevated) user after setup.

#ifndef AppVersion
  #define AppVersion "0.9.2"
#endif
#define AppName "Divit"
#define AppExe "Divit.exe"
#define Staging "staging"

[Setup]
AppId={{6B1F2D53-8C4E-4E7A-9A3B-D17E1F0A5C42}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Divit contributors
AppPublisherURL=https://github.com/yagizerhan/divit
AppSupportURL=https://github.com/yagizerhan/divit/issues
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.22000
LicenseFile=..\LICENSE
SetupIconFile=..\docs\brand\divit.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
OutputDir=Output
OutputBaseFilename=DivitSetup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; Ask to close a running Divit (same mutex the app uses for its single instance).
AppMutex=Local\Divit.Host
CloseApplications=yes
RestartApplications=no
ShowLanguageDialog=auto
; The adb task downloads Google's platform-tools .zip during setup (not bundled: Google's SDK license).
ArchiveExtraction=full
; The autostart value is per user (HKCU), same as the app's own switch. With an admin account (the usual case)
; this is the signed-in user; it is only "wrong" when a different admin account approves the UAC prompt.
UsedUserAreasWarning=no

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "tr"; MessagesFile: "compiler:Languages\Turkish.isl"

[CustomMessages]
en.TaskWinTab=WinTab support (recommended: Krita, Clip Studio Paint, SAI… get pen pressure with their default settings)
tr.TaskWinTab=WinTab desteği (önerilen: Krita, Clip Studio Paint, SAI… varsayılan ayarlarıyla kalem basıncı alır)
en.TaskAutostart=Start Divit when Windows starts
tr.TaskAutostart=Windows açılınca Divit'i başlat
en.GroupOptions=Options:
tr.GroupOptions=Seçenekler:
en.WinTabForeign=Another tablet driver's WinTab file is already installed (for example Wacom). Divit left it untouched; Divit works through Windows Ink.
tr.WinTabForeign=Başka bir tablet sürücüsünün WinTab dosyası zaten kurulu (örneğin Wacom). Divit ona dokunmadı; Divit, Windows Ink üzerinden çalışır.
en.TaskAdb=USB connection support: download Android Platform-Tools (adb) from Google (about 8 MB)
tr.TaskAdb=USB bağlantı desteği: Android Platform-Tools'u (adb) Google'dan indir (yaklaşık 8 MB)
en.LaunchDivit=Start Divit
tr.LaunchDivit=Divit'i başlat

[Tasks]
Name: "wintab"; Description: "{cm:TaskWinTab}"; GroupDescription: "{cm:GroupOptions}"
Name: "adb"; Description: "{cm:TaskAdb}"; GroupDescription: "{cm:GroupOptions}"; Check: NeedAdb
Name: "autostart"; Description: "{cm:TaskAutostart}"; GroupDescription: "{cm:GroupOptions}"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#Staging}\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#Staging}\wintab\x64\wintab32.dll"; DestDir: "{sys}"; Tasks: wintab; Check: WinTabFree(ExpandConstant('{sys}\wintab32.dll')); Flags: restartreplace uninsrestartdelete 64bit
Source: "{#Staging}\wintab\x86\wintab32.dll"; DestDir: "{syswow64}"; Tasks: wintab; Check: WinTabFree(ExpandConstant('{syswow64}\wintab32.dll')); Flags: restartreplace uninsrestartdelete 32bit
; adb for USB mode, downloaded from Google over HTTPS and unpacked to {app}\platform-tools (Divit looks there).
; Pinned version + SHA-256, so every install gets the same, verified adb (size and SHA-1 match Google's
; repository2-3.xml listing). To update: change the version, size and hash together.
Source: "https://dl.google.com/android/repository/platform-tools_r37.0.2-win.zip"; DestName: "platform-tools.zip"; DestDir: "{app}"; ExternalSize: 8239125; Hash: "6d93a5e63bc1654b70b7b94bec5c6ab46db113df0265beb314055dd253772b58"; Tasks: adb; Flags: external download extractarchive recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; Same value the app's own "Start with Windows" switch writes (HKCU, so it stays per user).
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Divit"; ValueData: """{app}\{#AppExe}"" --tray"; Tasks: autostart; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchDivit}"; Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/IM {#AppExe} /F"; Flags: runhidden; RunOnceId: "StopDivit"
Filename: "{app}\platform-tools\adb.exe"; Parameters: "kill-server"; Flags: runhidden skipifdoesntexist; RunOnceId: "StopAdb"

[UninstallDelete]
; Log files the app may leave next to Divit.exe.
Type: files; Name: "{app}\*.log"
Type: filesandordirs; Name: "{app}\platform-tools"

[Code]
{ True if the target has no wintab32.dll or it is Divit's own (it exports the marker "DivitWinTabVersion"). }
function WinTabFree(Path: String): Boolean;
var
  Data: AnsiString;
begin
  Result := True;
  if FileExists(Path) then
  begin
    Result := LoadStringFromFile(Path, Data) and (Pos('DivitWinTabVersion', Data) > 0);
  end;
end;

{ adb found where Divit looks before its own copy (SDK, ANDROID_HOME) or on PATH. Then Divit uses that one and a
  second adb of another version would only fight it over the adb server, so the download option is hidden. }
function OtherAdb: Boolean;
var
  Root: String;
begin
  Result := FileExists(ExpandConstant('{localappdata}\Android\Sdk\platform-tools\adb.exe'));
  Root := GetEnv('ANDROID_HOME');
  if (not Result) and (Root <> '') then Result := FileExists(AddBackslash(Root) + 'platform-tools\adb.exe');
  Root := GetEnv('ANDROID_SDK_ROOT');
  if (not Result) and (Root <> '') then Result := FileExists(AddBackslash(Root) + 'platform-tools\adb.exe');
  if not Result then Result := FileSearch('adb.exe', GetEnv('PATH')) <> '';
end;

function NeedAdb: Boolean;
begin
#ifdef ForceAdbTask
  Result := True;
#else
  Result := not OtherAdb;
#endif
end;

{ On an update, stop the adb server of our own copy so its files can be replaced. }
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Code: Integer;
  Adb: String;
begin
  Result := '';
  Adb := ExpandConstant('{app}\platform-tools\adb.exe');
  if FileExists(Adb) then
    Exec(Adb, 'kill-server', '', SW_HIDE, ewWaitUntilTerminated, Code);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and WizardIsTaskSelected('wintab') and
     (not WinTabFree(ExpandConstant('{sys}\wintab32.dll')) or not WinTabFree(ExpandConstant('{syswow64}\wintab32.dll'))) then
  begin
    if not WizardSilent then
      MsgBox(CustomMessage('WinTabForeign'), mbInformation, MB_OK);
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  { Remove the per-user autostart value if the settings window created it. }
  if CurUninstallStep = usUninstall then
    RegDeleteValue(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Run', 'Divit');
end;
