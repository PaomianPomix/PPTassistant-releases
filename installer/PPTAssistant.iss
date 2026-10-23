; Compile through scripts/build-release.ps1 with Inno Setup 6.3 or newer.
#ifndef AppVersion
  #error AppVersion must be supplied by the release build script.
#endif
#ifndef PublishDir
  #error PublishDir must be supplied by the release build script.
#endif
#ifndef OutputDir
  #error OutputDir must be supplied by the release build script.
#endif
#ifndef ProjectRoot
  #error ProjectRoot must be supplied by the release build script.
#endif

[Setup]
AppId={{4DCA6A2C-C6C7-47D3-BD6C-CC204071559D}
AppName=PPTAssistant
AppVersion={#AppVersion}
AppPublisher=PPTAssistant
AppMutex=Local\PPTAssistant.SingleInstance
SetupMutex=Local\PPTAssistant.Setup
DefaultDirName={localappdata}\Programs\PPTAssistant
DefaultGroupName=PPTAssistant
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
DisableProgramGroupPage=yes
WizardStyle=modern
SetupIconFile={#ProjectRoot}\src\PPTAssistant.App\Assets\PPTA.ico
UninstallDisplayIcon={app}\PPTAssistant.ico
UninstallDisplayName=PPTAssistant
OutputDir={#OutputDir}
OutputBaseFilename=PPTAssistant-{#AppVersion}-win-x64-Setup
VersionInfoVersion={#AppVersion}.0
VersionInfoDescription=PPTAssistant Setup
VersionInfoProductName=PPTAssistant
VersionInfoProductVersion={#AppVersion}
Compression=lzma2
SolidCompression=yes
CloseApplications=no
RestartApplications=no
ChangesAssociations=no
ChangesEnvironment=no

[Languages]
Name: english; MessagesFile: compiler:Default.isl

[Tasks]
Name: desktopicon; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\PPTAssistant"; Filename: "{app}\PPTAssistant.exe"; WorkingDir: "{app}"; IconFilename: "{app}\PPTAssistant.ico"
Name: "{userdesktop}\PPTAssistant"; Filename: "{app}\PPTAssistant.exe"; WorkingDir: "{app}"; IconFilename: "{app}\PPTAssistant.ico"; Tasks: desktopicon

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Command: String;
  InstalledCommand: String;
  RunKey: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    RunKey := 'Software\Microsoft\Windows\CurrentVersion\Run';
    InstalledCommand := '"' + ExpandConstant('{app}\PPTAssistant.exe') + '" --autostart';
    if RegQueryStringValue(HKCU, RunKey, 'PPTAssistant', Command) then
    begin
      { Only remove the startup entry owned by this installation. }
      if CompareText(Trim(Command), InstalledCommand) = 0 then
        RegDeleteValue(HKCU, RunKey, 'PPTAssistant');
    end;
  end;
end;
