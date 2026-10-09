#define MyAppName "QuickNotes"
#define MyAppVersion "2.1.0"
#define MyAppPublisher "QuickNotes"
#define MyAppExeName "QuickNotes.App.exe"

[Setup]
AppId={{8C2F1A4E-9B71-4D3A-9E6C-7A1B2C3D4E5F}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\QuickNotes
DefaultGroupName=QuickNotes
DisableProgramGroupPage=yes
OutputDir=output
OutputBaseFilename=QuickNotes-Setup
Compression=lzma
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
CloseApplications=yes
RestartApplications=yes
UninstallDisplayIcon={app}\{#MyAppExeName}
ArchitecturesInstallIn64BitMode=x64compatible
UsePreviousAppDir=yes

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "desktopicon"; Description: "Создать значок на рабочем столе"; GroupDescription: "Дополнительно:"; Flags: unchecked

[Files]
Source: "..\QuickNotes.App\bin\Release\net8.0-windows10.0.19041.0\win-x64\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\QuickNotes"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\QuickNotes"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Запустить QuickNotes"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Do not delete user notes, attachments, backups or settings.
; Those live in {localappdata}\QuickNotes and must survive uninstall.

[Code]
function InitializeSetup(): Boolean;
begin
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'QuickNotes');
  end;
end;
