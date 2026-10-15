; Inno Setup script for LG Network Cast.
; Build with:  scripts\build.ps1   (or: ISCC.exe installer\LGNetworkCast.iss after a Release build)

#define AppName "LG Network Cast"
#define AppExe "LGNetworkCast.exe"
#define BuildDir "..\src\LGNetworkCast\bin\Release\net48"
#define AppVersion GetVersionNumbersString(BuildDir + "\" + AppExe)

[Setup]
AppId={{7C3B2E58-4F1A-4B8E-9A55-3C1D2B7E6F10}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=LG Network Cast contributors
AppPublisherURL=https://github.com/
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=..\dist
OutputBaseFilename=LGNetworkCast-Setup-{#AppVersion}
SetupIconFile=..\src\LGNetworkCast\Resources\app.ico
UninstallDisplayIcon={app}\{#AppExe}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
CloseApplications=force
LicenseFile=..\LICENSE

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"
Name: "autostart"; Description: "Start {#AppName} with Windows (runs in the tray)"; GroupDescription: "Startup:"

[Files]
Source: "{#BuildDir}\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#BuildDir}\{#AppExe}.config"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#BuildDir}\ffmpeg.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{userdesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "LGNetworkCast"; \
  ValueData: """{app}\{#AppExe}"" --tray"; Flags: uninsdeletevalue; Tasks: autostart

[Run]
; Allow the TV to reach the app's local web server, so Windows never shows the "allow access" prompt.
; Runs once, elevated (one UAC approval), and replaces any earlier rules for this exe.
Filename: "{sys}\cmd.exe"; \
  Parameters: "/c netsh advfirewall firewall delete rule name=all program=""{app}\{#AppExe}"" & netsh advfirewall firewall add rule name=""{#AppName}"" dir=in action=allow program=""{app}\{#AppExe}"" enable=yes profile=any"; \
  Verb: runas; Flags: shellexec runhidden waituntilterminated; StatusMsg: "Allowing {#AppName} through Windows Firewall..."
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\cmd.exe"; Parameters: "/c netsh advfirewall firewall delete rule name=all program=""{app}\{#AppExe}"""; \
  Verb: runas; Flags: shellexec runhidden waituntilterminated; RunOnceId: "RemoveFirewallRule"
Filename: "{cmd}"; Parameters: "/C taskkill /IM {#AppExe} /F"; Flags: runhidden; RunOnceId: "KillApp"


[UninstallDelete]
Type: filesandordirs; Name: "{userappdata}\LGNetworkCast"
