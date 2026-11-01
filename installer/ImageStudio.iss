; Instalator per-użytkownik (bez uprawnień administratora). Wersję podaje pipeline: ISCC /DAppVersion=1.2.0
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

[Setup]
AppId={{7B1D3E52-4C0A-4E7B-9A55-1F3C8D2A6E10}
AppName=ImageStudio
AppVersion={#AppVersion}
AppPublisher=prachwal
DefaultDirName={autopf}\ImageStudio
DefaultGroupName=ImageStudio
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir=..\dist
OutputBaseFilename=ImageStudio-{#AppVersion}-Setup
SetupIconFile=..\ImageStudio\Assets\AppIcon.ico
UninstallDisplayIcon={app}\ImageStudio.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "polish"; MessagesFile: "compiler:Languages\Polish.isl"

[Tasks]
Name: "desktopicon"; Description: "Utwórz skrót na pulpicie"; Flags: unchecked

[Files]
Source: "..\publish\selfhosted\ImageStudio.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\ImageStudio"; Filename: "{app}\ImageStudio.exe"
Name: "{autodesktop}\ImageStudio"; Filename: "{app}\ImageStudio.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\ImageStudio.exe"; Description: "Uruchom ImageStudio"; Flags: nowait postinstall skipifsilent
