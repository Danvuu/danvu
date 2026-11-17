#ifndef SourceDir
  #error SourceDir must point to the tested native publish directory.
#endif
#ifndef OutputDir
  #error OutputDir must point to the build artifacts directory.
#endif
#ifndef AppVersion
  #define AppVersion "0.1.1-preview.1"
#endif
#ifndef AppFileVersion
  #define AppFileVersion "0.1.1.0"
#endif

[Setup]
AppId={{4D1B0428-0FE0-44F5-8A31-9E39D4F1431A}
AppName=Music
AppVersion={#AppVersion}
AppPublisher=Danvu
AppPublisherURL=https://github.com/Danvuu/danvu
AppSupportURL=https://github.com/Danvuu/danvu/issues
AppUpdatesURL=https://github.com/Danvuu/danvu/releases
DefaultDirName={localappdata}\Programs\Music
DefaultGroupName=Music
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputDir={#OutputDir}
OutputBaseFilename=Music-Setup
SetupIconFile={#SourceDir}\Assets\Music.ico
UninstallDisplayName=Music
UninstallDisplayIcon={app}\Music.exe
VersionInfoCompany=Danvu
VersionInfoProductName=Music
VersionInfoVersion={#AppFileVersion}
VersionInfoTextVersion={#AppVersion}
VersionInfoProductVersion={#AppFileVersion}
VersionInfoProductTextVersion={#AppVersion}
WizardStyle=modern
Compression=lzma2/normal
SolidCompression=yes
CloseApplications=yes
RestartApplications=no
DisableProgramGroupPage=yes
Uninstallable=yes
UninstallLogMode=append
SetupLogging=yes

[Tasks]
Name: desktopicon; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "install.ps1,uninstall.ps1"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Music\Music"; Filename: "{app}\Music.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\Music"; Filename: "{app}\Music.exe"; WorkingDir: "{app}"; Tasks: desktopicon

; No [Run] section: installation never launches Music automatically.
; Publisher/company strings are product metadata, not a Windows trust signature.
