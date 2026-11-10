[Setup]
AppId=BlueClipboard.Desktop
AppName=BlueClipboard
AppVersion={#AppVersion}
AppPublisher=ChimitovZ
AppPublisherURL=https://github.com/ChimitovZ/BlueClipboard
DefaultDirName={localappdata}\Programs\BlueClipboard
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir=..\dist
OutputBaseFilename=BlueClipboard-Setup-x64
UninstallDisplayIcon={app}\BlueClipboard.exe
CloseApplications=no
RestartApplications=no
WizardStyle=modern

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "startup"; Description: "Запускать BlueClipboard при входе в Windows"

[Files]
Source: "..\dist\BlueClipboard.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\dist\BlueClipboard.exe.config"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\dist\BlueClipboard.Update.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "installed.flag"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{userprograms}\BlueClipboard"; Filename: "{app}\BlueClipboard.exe"

[Run]
Filename: "{app}\BlueClipboard.exe"; Description: "Открыть BlueClipboard"; Flags: nowait postinstall skipifsilent

[Code]
const
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';

function CloseBlueClipboard: Boolean;
var
  H: HWND;
  I: Integer;
begin
  H := FindWindowByWindowName('BlueClipboard');
  if H <> 0 then
    PostMessage(H, $8002, 0, 0);
  for I := 1 to 100 do
  begin
    if not CheckForMutexes('Local\BlueClipboard.Desktop') then
    begin
      Result := True;
      exit;
    end;
    Sleep(100);
  end;
  Result := False;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Release: Cardinal;
begin
  Result := '';
  if not RegQueryDWordValue(HKLM, 'SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full', 'Release', Release) or (Release < 528040) then
    Result := 'Для BlueClipboard требуется .NET Framework 4.8 или новее.'
  else if not CloseBlueClipboard then
    Result := 'Выйдите из BlueClipboard через меню значка в трее и повторите установку.';
end;

function IsUpdate: Boolean;
var
  I: Integer;
begin
  Result := True;
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), '/UPDATE') = 0 then exit;
  Result := False;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep <> ssPostInstall then exit;
  if IsUpdate then
  begin
    if RegValueExists(HKCU, RunKey, 'BlueClipboard') then
      RegWriteStringValue(HKCU, RunKey, 'BlueClipboard', '"' + ExpandConstant('{app}\BlueClipboard.exe') + '" --background');
  end
  else if WizardIsTaskSelected('startup') then
    RegWriteStringValue(HKCU, RunKey, 'BlueClipboard', '"' + ExpandConstant('{app}\BlueClipboard.exe') + '" --background')
  else
    RegDeleteValue(HKCU, RunKey, 'BlueClipboard');
end;

function InitializeUninstall: Boolean;
begin
  Result := CloseBlueClipboard;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    RegDeleteValue(HKCU, RunKey, 'BlueClipboard');
end;
