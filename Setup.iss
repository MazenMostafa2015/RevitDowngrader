; Inno Setup script - compiled by Build-Installer.ps1 (which passes /DAppVersion=...)
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

[Setup]
AppId={{D3A6F2E1-5B7C-4C9A-8E14-6F0B2A9C7D31}
AppName=Revit Downgrader
AppVersion={#AppVersion}
AppPublisher=RevitDowngrader
DefaultDirName={autopf}\RevitDowngrader
DefaultGroupName=Revit Downgrader
DisableProgramGroupPage=yes
OutputDir=dist
OutputBaseFilename=RevitDowngrader-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
PrivilegesRequired=admin
UninstallDisplayIcon={app}\RevitDowngrader.exe

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
; WPF application and its dependencies
Source: "staging\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; One add-in build per Revit release (addin\2023, addin\2024, ...)
Source: "staging\addin\*"; DestDir: "{app}\addin"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "README.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\Revit Downgrader"; Filename: "{app}\RevitDowngrader.exe"
Name: "{group}\Uninstall Revit Downgrader"; Filename: "{uninstallexe}"
Name: "{autodesktop}\Revit Downgrader"; Filename: "{app}\RevitDowngrader.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\RevitDowngrader.exe"; Description: "Launch Revit Downgrader"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{localappdata}\RevitDowngrader"
