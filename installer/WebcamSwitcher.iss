; WebcamSwitcher (Camera Roles edition) Inno Setup installer.
; Packages the self-contained WPF app + the virtual-camera media source DLL,
; and registers the DLL (HKLM COM) on install / unregisters on uninstall.
; The DLL must live outside user profiles (the Frame Server runs as
; LOCAL SERVICE), so the app installs to Program Files.

#define MyAppName "WebcamSwitcher"
#ifndef MyAppVersion
  #define MyAppVersion "2.0"
#endif
#define MyAppPublisher "WebcamSwitcher contributors (based on TheRealAlexV/WebcamSwitcher, MIT)"
#define MyAppExeName "WebcamSwitcher.exe"

[Setup]
AppId={{A1B2C3D4-E5F6-4A7B-8C9D-0E1F2A3B4C5D}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
PrivilegesRequired=admin
OutputDir=..\artifacts\installer
OutputBaseFilename=WebcamSwitcher-Setup-{#MyAppVersion}
Compression=lzma
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#MyAppExeName}
LicenseFile=..\LICENSE
CloseApplications=yes
RestartApplications=no

[Files]
Source: "..\artifacts\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
Source: "..\THIRD_PARTY_NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\docs\*.md"; DestDir: "{app}\docs"; Flags: ignoreversion
Source: "..\docs\config.sample.json"; DestDir: "{app}\docs"; Flags: ignoreversion
Source: "..\docs\images\*"; DestDir: "{app}\docs\images"; Flags: ignoreversion skipifsourcedoesntexist

[InstallDelete]
; An older build's DLL must never stay registered under a stale path.
Type: files; Name: "{app}\WebcamSwitcher.Source.dll.old"

[Run]
Filename: "{sys}\regsvr32.exe"; Parameters: "/s ""{app}\WebcamSwitcher.Source.dll"""; Flags: runhidden waituntilterminated; StatusMsg: "Registering virtual camera media source..."
Filename: "{app}\{#MyAppExeName}"; Description: "Start {#MyAppName}"; Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallRun]
Filename: "{app}\{#MyAppExeName}"; Parameters: "--exit"; Flags: runhidden waituntilterminated; RunOnceId: "StopApp"
Filename: "{sys}\regsvr32.exe"; Parameters: "/u /s ""{app}\WebcamSwitcher.Source.dll"""; Flags: runhidden waituntilterminated; RunOnceId: "UnregisterSource"

[UninstallDelete]
; Machine-wide routing/format files read by the media source. The per-user
; config (%APPDATA%\WebcamSwitcher) and logs (%LOCALAPPDATA%\WebcamSwitcher)
; are kept so a reinstall restores the same Camera Roles.
Type: files; Name: "{commonappdata}\WebcamSwitcher\vcams.ini"
Type: files; Name: "{commonappdata}\WebcamSwitcher\format.ini"
Type: dirifempty; Name: "{commonappdata}\WebcamSwitcher"

[Tasks]
Name: "startup"; Description: "Run {#MyAppName} at Windows sign-in"; GroupDescription: "Additional tasks:"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#MyAppName}"; ValueData: """{app}\{#MyAppExeName}"""; Tasks: startup; Flags: uninsdeletevalue

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Camera Roles guide"; Filename: "{app}\docs\camera-roles.md"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
