; Inno Setup script for the Claude Buddy Windows installer.
;
; Build it with (from the repo root):
;   dotnet publish ClaudeBuddy.csproj -c Release -r win-x64 -p:DebugType=none
;   iscc tools\ClaudeBuddy.iss /DVersion=0.1.0-beta
;
; tools\build-windows-installer.ps1 wraps both steps and reads the version out
; of the csproj, which is what CI calls.
;
; Deliberately a per-user install: it goes under %LOCALAPPDATA%, needs no
; administrator rights, and therefore raises no UAC prompt. This is a menu-bar
; style utility that only ever touches the current user's Claude Code config, so
; a machine-wide install would buy nothing and cost an elevation dialog.

#ifndef Version
  #define Version "0.0.0-dev"
#endif

#define AppName "Claude Buddy"
#define AppPublisher "Kawika Miller and Repo Owner"
#define AppUrl "https://github.com/wtvamp/Claude-Buddy"
#define AppExe "ClaudeBuddy.exe"

; CFBundleVersion's Windows equivalent: VersionInfoVersion must be a plain
; dotted number, so strip any prerelease label for it while the user-visible
; AppVersion keeps the full label. Cutting at the first hyphen handles -rc.1 and
; anything else semver allows, not just -beta.
#if Pos("-", Version) > 0
  #define NumericVersion Copy(Version, 1, Pos("-", Version) - 1)
#else
  #define NumericVersion Version
#endif

[Setup]
; Never change AppId — it is how Windows recognises an existing install and
; upgrades it in place instead of stacking a second copy in Apps & Features.
AppId={{4046CFD2-79A9-4270-8302-21B87A92C0A5}
AppName={#AppName}
AppVersion={#Version}
AppVerName={#AppName} {#Version}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
VersionInfoVersion={#NumericVersion}

; Per-user, no elevation. Note the install directory is \Programs\ClaudeBuddy,
; deliberately distinct from the %LOCALAPPDATA%\ClaudeBuddy that
; install-windows-hooks.ps1 copies the hook script into. If they were the same
; folder, that script would try to copy the hook onto itself and fail.
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\ClaudeBuddy
DisableProgramGroupPage=yes
DefaultGroupName={#AppName}

; win-x64 self-contained publish; there is no 32-bit build to fall back to.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; A .txt copy, made by build-windows-installer.ps1: Inno decides between plain
; text and RTF by extension, and the repo's LICENSE has none.
LicenseFile=..\dist\LICENSE.txt
OutputDir=..\dist
OutputBaseFilename=ClaudeBuddy-{#Version}-win-x64-setup
SetupIconFile=..\Assets\ClaudeBuddy.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern

; The app has no main window — it lives in the notification area — so a plain
; "close the app" request has nothing to close. Restart Manager detects the file
; lock on ClaudeBuddy.exe and terminates it, which is what makes upgrading over
; a running copy work instead of failing on a locked file.
CloseApplications=force
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
; Checked by default and called out in its own wizard page, because an install
; without it produces an app that runs correctly and displays nothing, which
; reads as broken software rather than an unfinished setup.
Name: "wirehooks"; Description: "Wire up Claude Code hooks (required for orbs to appear)"; GroupDescription: "Setup:"
Name: "startup"; Description: "Start {#AppName} automatically when I sign in"; GroupDescription: "Setup:"

[Files]
Source: "..\bin\Release\net8.0\win-x64\publish\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
; The hook script sits at {app}\ and its installer at {app}\tools\, mirroring the
; repo layout. install-windows-hooks.ps1 resolves the hook as ..\ClaudeBuddyHook.ps1
; relative to itself, so this layout is what makes it work unmodified.
Source: "..\ClaudeBuddyHook.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\tools\install-windows-hooks.ps1"; DestDir: "{app}\tools"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
; Full path to powershell.exe here and everywhere below. Claude Code itself
; invokes Windows PowerShell 5.1, which is what install-windows-hooks.ps1 is
; written against, and naming it explicitly avoids resolving to a pwsh 7 that
; happens to shadow it on PATH.
Name: "{group}\Wire up Claude Code hooks"; \
  Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\tools\install-windows-hooks.ps1"""; \
  Comment: "Re-run hook setup, or repair it after a Claude Code reinstall"
Name: "{userstartup}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: startup

[Run]
Filename: "{app}\{#AppExe}"; Description: "Start {#AppName} now"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Take the hook entries back out of settings.json, or Claude Code keeps invoking
; a script that is about to be deleted and logs a hook error on every event.
; runhidden because an uninstall should not flash a console window.
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\tools\install-windows-hooks.ps1"" -Uninstall"; \
  Flags: runhidden; RunOnceId: "unwirehooks"
; Restart Manager only runs during install, so stop a running instance here too.
; Full {sys} path rather than bare "taskkill.exe" — skipifdoesntexist tests the
; filename as given, and an unqualified name would not resolve.
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM {#AppExe}"; Flags: runhidden skipifdoesntexist; RunOnceId: "stopapp"

[Code]
{ Hook wiring runs from code rather than a [Run] entry so its exit code can be
  checked. A [Run] line would fail silently, and "hooks quietly not installed"
  is precisely the confusing failure this project already goes out of its way to
  avoid — the user would be left with an app that shows nothing and no clue why. }
procedure WireUpHooks();
var
  ResultCode: Integer;
  Script: String;
begin
  Script := ExpandConstant('{app}\tools\install-windows-hooks.ps1');

  if not Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
              '-NoProfile -ExecutionPolicy Bypass -File "' + Script + '"',
              '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    MsgBox('Could not start PowerShell to wire up the Claude Code hooks.' + #13#10#13#10 +
           'Claude Buddy is installed and will run, but no orbs will appear until' + #13#10 +
           'the hooks are set up. Run this from a PowerShell prompt to finish:' + #13#10#13#10 +
           '  & "' + Script + '"',
           mbError, MB_OK);
    Exit;
  end;

  if ResultCode <> 0 then
    MsgBox('Hook setup failed (exit code ' + IntToStr(ResultCode) + ').' + #13#10#13#10 +
           'Claude Buddy is installed and will run, but no orbs will appear until' + #13#10 +
           'the hooks are set up. Run this from a PowerShell prompt to see the error:' + #13#10#13#10 +
           '  & "' + Script + '"',
           mbError, MB_OK);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  { ssPostInstall, not ssInstall: the script has to be on disk before it runs. }
  if (CurStep = ssPostInstall) and WizardIsTaskSelected('wirehooks') then
    WireUpHooks();
end;
