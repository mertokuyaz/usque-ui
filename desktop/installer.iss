; Usque Desktop installer — Inno Setup 6 script.
;
; Recommended (clean package, tests, verification, SHA-256 file in one step):
;   powershell -ExecutionPolicy Bypass -File .\desktop\build-installer.ps1 [-Suffix label]
; Manual (package already built by desktop\build.ps1):
;   iscc.exe desktop\installer.iss /DPackageDir=..\dist\desktop-1.3.2
;
; Optional defines: PackageDir (default ..\dist\desktop, as built by CI),
; MyAppVersion (default: the packaged Usque.Desktop.exe version), Arch (x64 or
; arm64; must match the package runtime) and OutputBaseName.

#define MyAppName "Usque Desktop"
#define MyAppExeName "Usque.Desktop.exe"
#define MyAppId "{{5B2D2A66-B408-4572-94C1-2F5B4A5E09D1}"

#ifndef PackageDir
  #define PackageDir "..\dist\desktop"
#endif
; Relative package paths are relative to this script, for both ISPP checks and [Files].
#define PackagePath (Pos(":", PackageDir) > 0 || Copy(PackageDir, 1, 2) == "\\") ? PackageDir : AddBackslash(SourcePath) + PackageDir

; Refuse to produce an installer from an incomplete package rather than shipping
; one that fails after the user has already installed it.
#if !FileExists(AddBackslash(PackagePath) + MyAppExeName)
  #error Package not found. Build it first with desktop\build.ps1 (or use desktop\build-installer.ps1).
#endif
#if !FileExists(AddBackslash(PackagePath) + "engine\usque.exe")
  #error engine\usque.exe is missing from the package.
#endif
#if !FileExists(AddBackslash(PackagePath) + "engine\wintun.dll")
  #error engine\wintun.dll is missing. Rebuild with -DownloadWintun or -WintunDll.
#endif
#if !FileExists(AddBackslash(PackagePath) + "Scripts\Network.ps1")
  #error Scripts\Network.ps1 is missing from the package.
#endif

; The executable's version comes from Usque.Desktop.csproj, so the installer
; cannot silently disagree with the binaries it contains.
#ifndef MyAppVersion
  #define VerMajor
  #define VerMinor
  #define VerRev
  #define VerBuild
  #expr GetVersionComponents(AddBackslash(PackagePath) + MyAppExeName, VerMajor, VerMinor, VerRev, VerBuild)
  #define MyAppVersion Str(VerMajor) + "." + Str(VerMinor) + "." + Str(VerRev)
#endif

#ifndef Arch
  #define Arch "x64"
#endif
#if Arch == "x64"
  #define AllowedArchitectures "x64compatible"
#elif Arch == "arm64"
  #define AllowedArchitectures "arm64"
#else
  #error Arch must be x64 or arm64.
#endif

#ifndef OutputBaseName
  #if Arch == "x64"
    #define OutputBaseName "UsqueDesktop-Setup-" + MyAppVersion
  #else
    #define OutputBaseName "UsqueDesktop-Setup-" + MyAppVersion + "-" + Arch
  #endif
#endif

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher=Usque
AppPublisherURL=https://github.com/Diniboy1123/usque
AppComments=Unofficial Cloudflare WARP client for Windows (MASQUE)
VersionInfoVersion={#MyAppVersion}
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}
VersionInfoDescription={#MyAppName} Setup
DefaultDirName={autopf}\UsqueDesktop
DefaultGroupName=Usque
DisableProgramGroupPage=yes
PrivilegesRequired=admin
; Setup and uninstall wait while the app runs. They must never force-close it:
; ending the app mid-session would leave its kill switch blocking traffic.
AppMutex=Global\UsqueDesktop-57d78a76
CloseApplications=no
RestartApplications=no
OutputDir=..\dist
OutputBaseFilename={#OutputBaseName}
SetupIconFile=Usque.Desktop\UsqueDesktop.ico
UninstallDisplayIcon={app}\UsqueDesktop.ico
UninstallDisplayName={#MyAppName} {#MyAppVersion}
WizardStyle=modern
WizardSizePercent=110,100
Compression=lzma2/ultra
SolidCompression=yes
ArchitecturesAllowed={#AllowedArchitectures}
ArchitecturesInstallIn64BitMode={#AllowedArchitectures}
MinVersion=10.0.17763
SetupLogging=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "turkish"; MessagesFile: "compiler:Languages\Turkish.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[InstallDelete]
; Upgrades replace the self-contained runtime. Remove old top-level assemblies
; first so a DLL dropped from a newer build cannot be loaded beside it.
Type: files; Name: "{app}\*.dll"

[Files]
; Never ship debug symbols, local profiles, recovery journals or logs in a
; public installer. Runtime .deps.json/.runtimeconfig.json are still included.
Source: "{#PackagePath}\*"; DestDir: "{app}"; Excludes: "*.pdb,config.json,settings.json,network-session.json,profiles\*,*.log"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Usque WARP"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\UsqueDesktop.ico"
Name: "{autodesktop}\Usque WARP"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\UsqueDesktop.ico"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

; The kill-switch WFP filters, profiles and settings are created by the app in
; %ProgramData%\UsqueDesktop, never by this installer, and uninstalling does not
; remove them. Use the app's "Restore internet access" (or --restore-network) first.
