; UPS Tools installer (Inno Setup 6). package.ps1 compiles it:
;
;   ISCC.exe /DVersion=X.Y.Z /DAppExe=<published UpsTools.exe> /O<output dir> installer\UpsTools.iss
;
; It installs per user, without administrator rights, into %LOCALAPPDATA%\Programs\UpsTools and adds a
; "UPS Tools" Start menu shortcut and an entry in Settings > Apps > Installed apps. Running a newer setup
; updates that folder in place, so the shortcut, a taskbar pin and Start with Windows keep working.
;
; A running UPS Tools holds its exe open, so the setup asks it to exit first ("UpsTools.exe --exit": it
; disconnects from the UPS cleanly) and starts it again in the tray afterwards.
;
; The uninstaller runs "UpsTools.exe --uninstall" before deleting the files. That exits the app and removes
; what the app created outside its folder: the Start with Windows entry, the notification registration and
; images, and the scheduled tasks of scripts set to run as administrator (one administrator prompt, if there
; are any). The settings and the log in %APPDATA%\UpsTools are the user's and stay.
;
; Keep this file ASCII.

#ifndef Version
  #error Pass the version: /DVersion=X.Y.Z
#endif
#ifndef AppExe
  #error Pass the published exe: /DAppExe=<path to UpsTools.exe>
#endif

#define RepoUrl "https://github.com/dkozlov-ua/ups-tools"

[Setup]
; Identifies the installation across versions: never change it.
AppId={{14560B05-D220-4DCA-BCA0-08FC5F726AAD}
AppName=UPS Tools
AppVersion={#Version}
AppVerName=UPS Tools {#Version}
AppPublisher=Dmytro Kozlov
AppCopyright=Copyright (c) Dmytro Kozlov
AppPublisherURL={#RepoUrl}
AppSupportURL={#RepoUrl}/issues
AppUpdatesURL={#RepoUrl}/releases
VersionInfoVersion={#Version}
VersionInfoProductName=UPS Tools

PrivilegesRequired=lowest
DefaultDirName={autopf}\UpsTools
DisableDirPage=yes
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Windows 10 2004: the Bluetooth LE APIs the app uses.
MinVersion=10.0.19041

; The app is exited by [Code] below, with its own signal, rather than by Restart Manager.
CloseApplications=no
RestartApplications=no

WizardStyle=modern
SetupIconFile=..\src\UpsTools\Assets\app.ico
UninstallDisplayIcon={app}\UpsTools.exe
UninstallDisplayName=UPS Tools
OutputBaseFilename=UpsTools-{#Version}-setup
Compression=lzma2/max
SolidCompression=yes

[Files]
Source: "{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\UPS Tools"; Filename: "{app}\UpsTools.exe"; Comment: "Watches your UPS and runs scripts when its state changes"

[Run]
; An update: start it again where it was, in the tray - or with its window open when the app's own update button
; ran this setup (/OPENWINDOW=yes).
Filename: "{app}\UpsTools.exe"; Parameters: "--background"; Check: WasRunningBefore and not OpenWindowAfter; Flags: nowait
Filename: "{app}\UpsTools.exe"; Check: WasRunningBefore and OpenWindowAfter; Flags: nowait
Filename: "{app}\UpsTools.exe"; Description: "Open UPS Tools"; Check: not WasRunningBefore; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{app}\UpsTools.exe"; Parameters: "--uninstall"; RunOnceId: "Cleanup"; Flags: waituntilterminated

[Code]
const
  InstanceMutex = 'Local\UpsTools.SingleInstance';
  RuntimeUrl = 'https://dotnet.microsoft.com/download/dotnet/10.0';

var
  WasRunning: Boolean;

function WasRunningBefore: Boolean;
begin
  Result := WasRunning;
end;

function OpenWindowAfter: Boolean;
begin
  Result := CompareText(ExpandConstant('{param:OPENWINDOW|no}'), 'yes') = 0;
end;

{ The exe is framework-dependent: it needs the .NET 10 Desktop Runtime (x64). }
function HasDesktopRuntime: Boolean;
var
  Find: TFindRec;
begin
  Result := FindFirst(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App\10.*'), Find);
  if Result then FindClose(Find);
end;

function InitializeSetup: Boolean;
var
  Code: Integer;
begin
  Result := True;
  if not HasDesktopRuntime then
    if SuppressibleMsgBox('UPS Tools needs the .NET 10 Desktop Runtime (x64), which is not installed.' + #13#10#13#10 +
         'Open its download page? You can install it before or after UPS Tools.',
         mbConfirmation, MB_YESNO, IDNO) = IDYES then
      ShellExec('open', RuntimeUrl, '', '', SW_SHOWNORMAL, ewNoWait, Code);
end;

{ Asks a running UPS Tools to exit before its exe is replaced, and waits for it (up to 30 s). }
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Exe: String;
  Code, I: Integer;
begin
  Result := '';
  WasRunning := CheckForMutexes(InstanceMutex);
  if not WasRunning then Exit;

  Exe := ExpandConstant('{app}\UpsTools.exe');
  if FileExists(Exe) then
    Exec(Exe, '--exit', '', SW_HIDE, ewWaitUntilTerminated, Code);
  { Before 0.3.0, --exit returned without waiting: wait here as well. }
  for I := 1 to 120 do
  begin
    if not CheckForMutexes(InstanceMutex) then Exit;
    Sleep(250);
  end;
  Result := 'UPS Tools is still running. Exit it from its tray icon, then run the setup again.';
end;
