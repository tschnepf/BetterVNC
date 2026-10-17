; Inno Setup script for the BetterVNC viewer.
;
;   ISCC.exe /DVersion=0.0.2 /DSource=path\to\published\BetterVNC-win-x64 /DOutDir=dist installers\windows\BetterVNC.iss
;
; Installs into Program Files (or, if the person picks "for me only" in the privileges dialog, into the per-user
; Programs folder), adds a Start menu entry, an optional desktop shortcut and the vnc:// URL handler, and registers
; the uninstaller. Running the installer over an older version upgrades it in place.

#ifndef Version
  #define Version "0.0.0"
#endif
#ifndef Source
  #define Source "..\..\publish\BetterVNC-win-x64"
#endif
#ifndef OutDir
  #define OutDir "."
#endif

[Setup]
AppId={{6C1F3B7A-9E42-4D8B-8F0A-2B5D3C9E7A11}
AppName=BetterVNC
AppVersion={#Version}
AppVerName=BetterVNC {#Version}
AppPublisher=BetterVNC
AppPublisherURL=https://github.com/tschnepf/BetterVNC
AppSupportURL=https://github.com/tschnepf/BetterVNC/issues
AppUpdatesURL=https://github.com/tschnepf/BetterVNC/releases
DefaultDirName={autopf}\BetterVNC
DefaultGroupName=BetterVNC
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\BetterVNC.exe
UninstallDisplayName=BetterVNC
OutputDir={#OutDir}
OutputBaseFilename=BetterVNC-{#Version}-setup
SetupIconFile=..\..\src\BetterVnc.App\Assets\BetterVNC.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog
CloseApplications=yes
RestartApplications=no
MinVersion=10.0.17763

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; Flags: unchecked
Name: "vncprotocol"; Description: "Open &vnc:// links with BetterVNC"

[Files]
Source: "{#Source}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\BetterVNC"; Filename: "{app}\BetterVNC.exe"; Comment: "Connect to Unraid VMs and Macs over VNC"
Name: "{autodesktop}\BetterVNC"; Filename: "{app}\BetterVNC.exe"; Tasks: desktopicon

[Registry]
; vnc://host:port opens a session straight away (App.xaml.cs understands the scheme). HKA is HKLM for an admin
; install and HKCU for a per-user one, so the handler is registered where the install is.
Root: HKA; Subkey: "Software\Classes\vnc"; ValueType: string; ValueName: ""; ValueData: "URL:VNC session"; Flags: uninsdeletekey; Tasks: vncprotocol
Root: HKA; Subkey: "Software\Classes\vnc"; ValueType: string; ValueName: "URL Protocol"; ValueData: ""; Tasks: vncprotocol
Root: HKA; Subkey: "Software\Classes\vnc\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: """{app}\BetterVNC.exe"",0"; Tasks: vncprotocol
Root: HKA; Subkey: "Software\Classes\vnc\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\BetterVNC.exe"" ""%1"""; Tasks: vncprotocol

[Run]
Filename: "{app}\BetterVNC.exe"; Description: "Launch BetterVNC"; Flags: nowait postinstall skipifsilent
