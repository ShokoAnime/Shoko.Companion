; Shoko Companion — Windows installer
; Built with Inno Setup
; Requires: iscc (available on windows-latest via GHA)
; Usage: iscc /DSourceDir="publish" installer.iss
;   SourceDir — directory containing the published binary (relative or absolute)

#define MyAppName "Shoko Companion"
#define MyAppVersion "0.1.0"
#define MyAppPublisher "Shoko"
#define MyAppURL "https://shokoanime.com"
#define MyAppExeName "shoko-companion.exe"
#ifndef SourceDir
#define SourceDir "..\..\publish"
#endif

[Setup]
AppId={{CC7A7C8E-3C8A-4E8A-9B8E-7C8A4E8A9B8E}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputBaseFilename=shoko-companion.Installer
OutputDir={#SourceDir}\..
Compression=lzma
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\{#MyAppExeName}
PrivilegesRequired=admin

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "{#SourceDir}\shoko-companion.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\*.dll"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Tasks]
Name: desktopicon; Description: "Create a &desktop shortcut"; GroupDescription: "Additional icons:"

[Registry]
; Register shoko:// URL scheme
Root: HKLM; Subkey: "Software\Classes\shoko"; ValueType: string; ValueName: ""; ValueData: "URL:Shoko Protocol"; Flags: uninsdeletekey
Root: HKLM; Subkey: "Software\Classes\shoko"; ValueType: string; ValueName: "URL Protocol"; ValueData: ""; Flags: uninsdeletekey
Root: HKLM; Subkey: "Software\Classes\shoko\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#MyAppExeName},1"; Flags: uninsdeletekey
Root: HKLM; Subkey: "Software\Classes\shoko\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""; Flags: uninsdeletekey

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent
