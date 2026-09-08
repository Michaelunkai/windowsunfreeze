#define AppName "Thaw"
#define AppVersion "1.4.9"
#define AppPublisher "Michaelunkai"
#define AppExeName "Thaw.exe"

[Setup]
AppId={{B2ABF7BF-5B0D-4C54-B332-3A0186C95B50}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL=https://github.com/Michaelunkai/windowsunfreeze
AppSupportURL=https://github.com/Michaelunkai/windowsunfreeze/issues
AppUpdatesURL=https://github.com/Michaelunkai/windowsunfreeze/releases
DefaultDirName={autopf}\Thaw
DefaultGroupName=Thaw
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#AppExeName}
OutputDir=..\artifacts\release
OutputBaseFilename=Thaw-Setup
SetupIconFile=..\Assets\Thaw.ico
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
CloseApplications=yes
RestartApplications=no
VersionInfoVersion={#AppVersion}
VersionInfoCompany={#AppPublisher}
VersionInfoDescription=Thaw installer
VersionInfoProductName={#AppName}
VersionInfoProductVersion={#AppVersion}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "..\artifacts\publish\Thaw.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\UNFREEZE_METHODS.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\Thaw"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"
Name: "{group}\Uninstall Thaw"; Filename: "{uninstallexe}"
Name: "{autodesktop}\Thaw"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Launch Thaw"; Flags: nowait postinstall skipifsilent
