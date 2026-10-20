; Inno Setup script for the BetterVNC guest agent on Windows (runs inside the VM).
;
;   ISCC.exe /DVersion=0.0.2 /DSource=path\to\published\bettervnc-agent-win-x64 /DOutDir=dist installers\windows\BetterVncAgent.iss
;
; The agent runs in the signed-in user's session (tray icon, clipboard, screen capture), so it lives in that user's
; per-user Programs folder, exactly where "bettervnc-agent install" puts it. After copying the file the installer runs
; that same "install" command as the signed-in user: it writes the config (keeping an existing one with its pairings
; and token), adds the per-user Run entry that starts the agent at sign-in, and starts it now. The installer asks
; for administrator rights by default only to add the firewall rule; "for me only" skips that and Windows asks once
; when the agent first listens.

#ifndef Version
  #define Version "0.0.0"
#endif
#ifndef Source
  #define Source "..\..\publish\bettervnc-agent-win-x64"
#endif
#ifndef OutDir
  #define OutDir "."
#endif

[Setup]
AppId={{D4A8E2F1-7B3C-4E9A-9C6D-5F1A8B2C3D44}
AppName=BetterVNC Guest Agent
AppVersion={#Version}
AppVerName=BetterVNC Guest Agent {#Version}
AppPublisher=BetterVNC
AppPublisherURL=https://github.com/tschnepf/BetterVNC
AppSupportURL=https://github.com/tschnepf/BetterVNC/issues
AppUpdatesURL=https://github.com/tschnepf/BetterVNC/releases
; Must match Installer.Layout in tools/BetterVnc.Agent/Installer.cs, so the tray's "Start at login" switch, the
; config and "uninstall" all refer to the same place.
DefaultDirName={localappdata}\Programs\BetterVNC Agent
DisableDirPage=yes
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\bettervnc-agent.exe
UninstallDisplayName=BetterVNC Guest Agent
OutputDir={#OutDir}
OutputBaseFilename=bettervnc-agent-{#Version}-setup
SetupIconFile=..\..\src\BetterVnc.App\Assets\BetterVNC.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog
CloseApplications=no
MinVersion=10.0.17763

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "{#Source}\bettervnc-agent.exe"; DestDir: "{app}"; Flags: ignoreversion

[Run]
; Elevated installs add the inbound rule here; the agent's own attempt in "install" needs elevation too and only
; prints a note when it cannot.
; profile=any because VMs on a bridged network often sit on a "Public" network profile; the delete keeps a reinstall
; from stacking up duplicate rules.
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""BetterVNC agent"" program=""{app}\bettervnc-agent.exe"""; Flags: runhidden waituntilterminated; Check: IsAdminInstallMode
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""BetterVNC agent"" dir=in action=allow protocol=TCP program=""{app}\bettervnc-agent.exe"" profile=any enable=yes"; Flags: runhidden waituntilterminated; Check: IsAdminInstallMode; StatusMsg: "Adding the firewall rule…"
Filename: "{app}\bettervnc-agent.exe"; Parameters: "install"; Flags: runhidden waituntilterminated runasoriginaluser; StatusMsg: "Registering the agent to start at sign-in and starting it…"

[UninstallRun]
; Stops the agent, removes the Run entry, firewall rule, config and log; Inno then removes the program file.
Filename: "{app}\bettervnc-agent.exe"; Parameters: "uninstall"; Flags: runhidden waituntilterminated; RunOnceId: "AgentUninstall"

[Code]
procedure StopAgent();
var
  R: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /im bettervnc-agent.exe', '', SW_HIDE, ewWaitUntilTerminated, R);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  // A running agent (tray icon) holds the executable open; stop it before the file is replaced.
  StopAgent();
  Result := '';
end;

function InitializeUninstall(): Boolean;
begin
  StopAgent();
  Result := True;
end;
