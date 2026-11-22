#ifndef MyAppVersion
  #define MyAppVersion "1.4.0"
#endif

[Setup]
AppId={{8E9B3B89-2D24-4A6D-9B2C-6F867D4F12A1}
AppName=vRemoter
AppVersion={#MyAppVersion}
AppVerName=vRemoter {#MyAppVersion}
AppPublisher=ccc333cn
AppPublisherURL=https://github.com/ccc333cn/vRemoter
DefaultDirName={localappdata}\Programs\vRemoter
DefaultGroupName=vRemoter
DisableProgramGroupPage=yes
LicenseFile=..\..\LICENSE
OutputDir=..\dist
OutputBaseFilename=vRemoter-Community-{#MyAppVersion}-Windows-x64-Setup
SetupIconFile=..\src\vRemoter.Windows\Assets\vRemoter.ico
UninstallDisplayIcon={app}\vRemoter.exe
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
CloseApplications=force
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\dist\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\vRemoter"; Filename: "{app}\vRemoter.exe"
Name: "{autodesktop}\vRemoter"; Filename: "{app}\vRemoter.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\vRemoter.exe"; Description: "{cm:LaunchProgram,vRemoter}"; Flags: nowait postinstall skipifsilent

