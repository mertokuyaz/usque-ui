; Usque Desktop installer — Inno Setup script.
; The desktop package must already be built into ..\dist\desktop-1.3.2.
; Build:  iscc.exe desktop\installer.iss /DPackageDir=..\dist\desktop-1.3.2
; Default PackageDir matches the CI build output (dist\desktop).

#ifndef MyAppVersion
  #define MyAppVersion "1.3.2"
#endif
#ifndef PackageDir
  #define PackageDir "..\dist\desktop"
#endif

#define MyAppName "Usque Desktop"
#define MyAppExeName "Usque.Desktop.exe"
#define MyAppId "{{5B2D2A66-B408-4572-94C1-2F5B4A5E09D1}"

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher=Usque
AppPublisherURL=https://github.com/Diniboy1123/usque
AppComments=Unofficial Cloudflare WARP client for Windows (MASQUE)
DefaultDirName={autopf}\UsqueDesktop
DefaultGroupName=Usque
DisableProgramGroupPage=yes
PrivilegesRequired=admin
AppMutex=Global\UsqueDesktop-57d78a76
OutputDir=..\dist
OutputBaseFilename=UsqueDesktop-Setup-{#MyAppVersion}
SetupIconFile=Usque.Desktop\UsqueDesktop.ico
UninstallDisplayIcon={app}\UsqueDesktop.ico
WizardStyle=modern
WizardSizePercent=110,100
Compression=lzma2/ultra
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
UninstallDisplayName={#MyAppName} {#MyAppVersion}
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "turkish"; MessagesFile: "compiler:Languages\Turkish.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Never ship debug symbols, local profiles, recovery journals or logs in a
; public installer. Runtime .deps.json/.runtimeconfig.json are still included.
Source: "{#PackageDir}\*"; DestDir: "{app}"; Excludes: "*.pdb,config.json,settings.json,network-session.json,profiles\*,*.log"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Usque WARP"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\UsqueDesktop.ico"
Name: "{autodesktop}\Usque WARP"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\UsqueDesktop.ico"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Code]
function InitializeSetup(): Boolean;
begin
  // The kill-switch WFP filters and user profiles are created by the
  // application inside CommonApplicationData, never by this installer.
  // Uninstalling this package does not remove them: use the app's
  // "Restore internet access" button (or --restore-network) first.
  Result := True;
end;
