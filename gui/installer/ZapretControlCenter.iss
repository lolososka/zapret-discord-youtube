#ifndef SourceDir
  #error SourceDir is required
#endif
#ifndef OutputDir
  #error OutputDir is required
#endif
#ifndef InstallerBaseName
  #error InstallerBaseName is required
#endif
#ifndef GuiVersion
  #error GuiVersion is required
#endif
#ifndef UpstreamVersion
  #error UpstreamVersion is required
#endif
#ifndef SetupIconPath
  #error SetupIconPath is required
#endif
#ifndef LicensePath
  #error LicensePath is required
#endif
#ifndef InstallerAppId
  #define InstallerAppId "{{407AED06-6513-413B-8B56-D5576529BE4A}"
#endif
#ifndef InstallerAppName
  #define InstallerAppName "Zapret Control Center"
#endif
#ifndef InstallerPrivilegesRequired
  #define InstallerPrivilegesRequired "admin"
#endif
#ifndef InstallerApplicationMutexes
  #define InstallerApplicationMutexes "Global\ZapretGUI.SingleInstance,Global\ZapretGUI.Update.Apply"
#endif

[Setup]
AppId={#InstallerAppId}
AppName={#InstallerAppName}
AppVersion={#GuiVersion}
AppVerName={#InstallerAppName} {#GuiVersion}
AppPublisher=Zapret Control Center contributors
AppPublisherURL=https://github.com/lolososka/zapret-discord-youtube
AppSupportURL=https://github.com/lolososka/zapret-discord-youtube/issues
AppUpdatesURL=https://lolososka.github.io/zapret-discord-youtube/
AppCopyright=Copyright (c) 2026 Zapret Control Center contributors
VersionInfoCompany=Zapret Control Center contributors
VersionInfoDescription=Zapret Control Center installer
VersionInfoProductName={#InstallerAppName}
VersionInfoProductVersion={#GuiVersion}.0
VersionInfoTextVersion={#GuiVersion}
VersionInfoVersion={#GuiVersion}.0
VersionInfoOriginalFileName={#InstallerBaseName}.exe
DefaultDirName={autopf}\Zapret Control Center
DefaultGroupName=Zapret Control Center
DisableProgramGroupPage=yes
DisableDirPage=auto
UsePreviousAppDir=yes
AllowUNCPath=no
PrivilegesRequired={#InstallerPrivilegesRequired}
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
SetupIconFile={#SetupIconPath}
LicenseFile={#LicensePath}
UninstallDisplayIcon={app}\runtime\ZapretGUI.exe
UninstallDisplayName={#InstallerAppName}
UninstallFilesDir={app}\uninstall
AppMutex={#InstallerApplicationMutexes}
CloseApplications=yes
RestartApplications=no
RestartIfNeededByRun=no
OutputDir={#OutputDir}
OutputBaseFilename={#InstallerBaseName}
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes
WizardStyle=modern
SetupLogging=yes

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Создать ярлык на рабочем столе"; GroupDescription: "Дополнительные значки:"; Flags: unchecked

[Files]
; The portable updater replaces the whole runtime directory. Keeping Inno's
; uninstaller one level above it makes Apps & Features survive GUI updates.
Source: "{#SourceDir}\*"; DestDir: "{app}\runtime"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Zapret Control Center"; Filename: "{app}\runtime\ZapretGUI.exe"; WorkingDir: "{app}\runtime"
Name: "{autodesktop}\Zapret Control Center"; Filename: "{app}\runtime\ZapretGUI.exe"; WorkingDir: "{app}\runtime"; Tasks: desktopicon

[Run]
Filename: "{app}\runtime\ZapretGUI.exe"; WorkingDir: "{app}\runtime"; Description: "Запустить Zapret Control Center"; Flags: nowait postinstall skipifsilent

[Code]
type
  TInstallerServiceState = (
    issNotInstalled,
    issStopped,
    issRunning,
    issPaused,
    issStartPending,
    issStopPending,
    issOtherPending,
    issUnknown);

const
  ServiceName = 'zapret';
  ServiceRegistryKey = 'SYSTEM\CurrentControlSet\Services\zapret';
  AutoRunRegistryKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  AutoRunValueName = 'ZapretGUI';
  IpsetSentinel = '203.0.113.113/32';
  ApplicationMutexes = '{#InstallerApplicationMutexes}';

var
  HadExistingRuntime: Boolean;
  HadCheckUpdatesFlag: Boolean;
  HadGameFilterFlag: Boolean;
  IpsetMode: Integer;
  StateDir: String;
  ServiceNeedsRestart: Boolean;
  ServiceRestarted: Boolean;

function RuntimeDir: String;
begin
  Result := ExpandConstant('{app}\runtime');
end;

function StatePath(const RelativePath: String): String;
var
  SafeName: String;
begin
  SafeName := RelativePath;
  StringChangeEx(SafeName, '\', '_', True);
  Result := AddBackslash(StateDir) + SafeName;
end;

function RuntimePath(const RelativePath: String): String;
begin
  Result := AddBackslash(RuntimeDir) + RelativePath;
end;

procedure BackupOptionalFile(const RelativePath: String);
var
  SourcePath: String;
begin
  SourcePath := RuntimePath(RelativePath);
  if FileExists(SourcePath) and
     (not FileCopy(SourcePath, StatePath(RelativePath), False)) then
    RaiseException('Не удалось сохранить пользовательский файл: ' + RelativePath);
end;

procedure RestoreOptionalFile(const RelativePath: String);
var
  BackupPath: String;
begin
  BackupPath := StatePath(RelativePath);
  if FileExists(BackupPath) and
     (not FileCopy(BackupPath, RuntimePath(RelativePath), False)) then
    RaiseException('Не удалось восстановить пользовательский файл: ' + RelativePath);
end;

procedure CaptureUserState;
var
  IpsetText: AnsiString;
begin
  HadExistingRuntime := DirExists(RuntimeDir);
  if not HadExistingRuntime then
    exit;

  StateDir := ExpandConstant('{tmp}\zapret-control-center-installer-state');
  DelTree(StateDir, True, True, True);
  if not ForceDirectories(StateDir) then
    RaiseException('Не удалось подготовить резервную копию настроек.');

  HadCheckUpdatesFlag := FileExists(RuntimePath('utils\check_updates.enabled'));
  HadGameFilterFlag := FileExists(RuntimePath('utils\game_filter.enabled'));
  BackupOptionalFile('utils\check_updates.enabled');
  BackupOptionalFile('utils\game_filter.enabled');
  BackupOptionalFile('bin\ACTIVE_DISCORD_UDP.bin');
  BackupOptionalFile('bin\ACTIVE_GAME_UDP.bin');

  IpsetMode := 0;
  if LoadStringFromFile(RuntimePath('lists\ipset-all.txt'), IpsetText) then begin
    if Trim(String(IpsetText)) = '' then
      IpsetMode := 1
    else if Pos(IpsetSentinel, String(IpsetText)) > 0 then
      IpsetMode := 2
    else
      IpsetMode := 3;
  end;
end;

procedure RestoreIpsetMode;
var
  IpsetPath: String;
  BackupPath: String;
begin
  if IpsetMode = 0 then
    exit;

  IpsetPath := RuntimePath('lists\ipset-all.txt');
  BackupPath := RuntimePath('lists\ipset-all.txt.backup');
  if IpsetMode = 1 then begin
    if not SaveStringToFile(IpsetPath, '', False) then
      RaiseException('Не удалось восстановить режим IPSet «весь трафик».');
  end
  else if IpsetMode = 2 then begin
    if not SaveStringToFile(IpsetPath, IpsetSentinel + #13#10, False) then
      RaiseException('Не удалось восстановить режим IPSet «выключен».');
  end
  else if IpsetMode = 3 then begin
    if not FileExists(BackupPath) then
      RaiseException('В новой сборке отсутствует резервный IPSet.');
    if not FileCopy(BackupPath, IpsetPath, False) then
      RaiseException('Не удалось включить обновлённый IPSet.');
    DeleteFile(BackupPath);
  end;
end;

procedure RestoreUserState;
begin
  if not HadExistingRuntime then
    exit;

  RestoreOptionalFile('bin\ACTIVE_DISCORD_UDP.bin');
  RestoreOptionalFile('bin\ACTIVE_GAME_UDP.bin');

  if HadCheckUpdatesFlag then
    RestoreOptionalFile('utils\check_updates.enabled')
  else
    DeleteFile(RuntimePath('utils\check_updates.enabled'));

  if HadGameFilterFlag then
    RestoreOptionalFile('utils\game_filter.enabled')
  else
    DeleteFile(RuntimePath('utils\game_filter.enabled'));

  RestoreIpsetMode;
end;

function NormalizeCommandPath(const Value: String): String;
begin
  Result := Trim(Value);
  StringChangeEx(Result, '/', '\', True);
end;

function ExtractServiceExecutable(
  const CommandLine: String;
  var ExecutablePath: String): Boolean;
var
  Text: String;
  EndIndex: Integer;
begin
  Result := False;
  ExecutablePath := '';
  Text := Trim(CommandLine);
  if Text = '' then
    exit;

  if Text[1] = '"' then begin
    EndIndex := 2;
    while (EndIndex <= Length(Text)) and (Text[EndIndex] <> '"') do
      EndIndex := EndIndex + 1;
    if EndIndex > Length(Text) then
      exit;
    if (EndIndex < Length(Text)) and (Text[EndIndex + 1] > ' ') then
      exit;
    ExecutablePath := Copy(Text, 2, EndIndex - 2);
  end
  else begin
    EndIndex := 1;
    while (EndIndex <= Length(Text)) and (Text[EndIndex] > ' ') do
      EndIndex := EndIndex + 1;
    ExecutablePath := Copy(Text, 1, EndIndex - 1);
  end;

  ExecutablePath := NormalizeCommandPath(ExecutablePath);
  Result := ExecutablePath <> '';
end;

function OwnsZapretService: Boolean;
var
  ImagePath: String;
  ExpectedPath: String;
  ExecutablePath: String;
begin
  Result := False;
  if not RegQueryStringValue(
    HKLM64,
    ServiceRegistryKey,
    'ImagePath',
    ImagePath) then
    exit;

  ExpectedPath := NormalizeCommandPath(RuntimePath('bin\winws.exe'));
  if not ExtractServiceExecutable(ImagePath, ExecutablePath) then
    exit;
  Result := CompareText(ExecutablePath, ExpectedPath) = 0;
end;

function AppendCapturedLines(
  const Lines: TArrayOfString;
  const Existing: String): String;
var
  I: Integer;
begin
  Result := Existing;
  if GetArrayLength(Lines) > 0 then
    for I := 0 to GetArrayLength(Lines) - 1 do
      Result := Result + Uppercase(Lines[I]) + #10;
end;

function QueryServiceState: TInstallerServiceState;
var
  ResultCode: Integer;
  Output: TExecOutput;
  Text: String;
begin
  Result := issUnknown;
  try
    if not ExecAndCaptureOutput(
      ExpandConstant('{sys}\sc.exe'),
      'query "' + ServiceName + '"',
      '',
      SW_SHOWNORMAL,
      ewWaitUntilTerminated,
      ResultCode,
      Output) then
      exit;
  except
    Log('Service query failed: ' + GetExceptionMessage);
    exit;
  end;

  Text := AppendCapturedLines(Output.StdOut, '');
  Text := AppendCapturedLines(Output.StdErr, Text);
  if (ResultCode = 1060) or (Pos('1060', Text) > 0) then begin
    Result := issNotInstalled;
    exit;
  end;
  if (ResultCode <> 0) or Output.Error then
    exit;

  if Pos('START_PENDING', Text) > 0 then
    Result := issStartPending
  else if Pos('STOP_PENDING', Text) > 0 then
    Result := issStopPending
  else if (Pos('PAUSE_PENDING', Text) > 0) or
          (Pos('CONTINUE_PENDING', Text) > 0) then
    Result := issOtherPending
  else if Pos('RUNNING', Text) > 0 then
    Result := issRunning
  else if Pos('STOPPED', Text) > 0 then
    Result := issStopped
  else if Pos('PAUSED', Text) > 0 then
    Result := issPaused;
end;

function IsPendingServiceState(
  const State: TInstallerServiceState): Boolean;
begin
  Result := (State = issStartPending) or
            (State = issStopPending) or
            (State = issOtherPending);
end;

function WaitUntilServiceStable(
  var State: TInstallerServiceState): Boolean;
var
  I: Integer;
begin
  for I := 0 to 59 do begin
    State := QueryServiceState;
    if State = issUnknown then begin
      Result := False;
      exit;
    end;
    if not IsPendingServiceState(State) then begin
      Result := True;
      exit;
    end;
    Sleep(250);
  end;
  Result := False;
end;

function WaitUntilServiceStopped: Boolean;
var
  I: Integer;
  State: TInstallerServiceState;
begin
  for I := 0 to 119 do begin
    State := QueryServiceState;
    if (State = issStopped) or (State = issNotInstalled) then begin
      Result := True;
      exit;
    end;
    if (State = issUnknown) or
       ((State <> issNotInstalled) and (not OwnsZapretService)) then begin
      Result := False;
      exit;
    end;
    Sleep(250);
  end;
  Result := False;
end;

function WaitUntilServiceRunning: Boolean;
var
  I: Integer;
  State: TInstallerServiceState;
begin
  for I := 0 to 119 do begin
    State := QueryServiceState;
    if State = issRunning then begin
      Result := OwnsZapretService;
      exit;
    end;
    if (State = issUnknown) or
       (State = issNotInstalled) or
       (State = issStopped) or
       (State = issPaused) or
       (not OwnsZapretService) then begin
      Result := False;
      exit;
    end;
    Sleep(250);
  end;
  Result := False;
end;

function StopOwnedService(var WasRunning: Boolean): Boolean;
var
  ResultCode: Integer;
  State: TInstallerServiceState;
  Started: Boolean;
begin
  WasRunning := False;
  Result := True;
  if not OwnsZapretService then
    exit;

  if not WaitUntilServiceStable(State) then begin
    Result := False;
    exit;
  end;
  if State = issNotInstalled then
    exit;
  if not OwnsZapretService then begin
    Result := False;
    exit;
  end;
  if State = issStopped then
    exit;
  if (State <> issRunning) and (State <> issPaused) then begin
    Result := False;
    exit;
  end;
  WasRunning := True;

  Started := Exec(
    ExpandConstant('{sys}\sc.exe'),
    'stop "' + ServiceName + '"',
    '',
    SW_SHOWNORMAL,
    ewWaitUntilTerminated,
    ResultCode);
  if not Started then begin
    Result := False;
    exit;
  end;
  Result := WaitUntilServiceStopped;
end;

function StartOwnedService: Boolean;
var
  ResultCode: Integer;
  State: TInstallerServiceState;
  Started: Boolean;
begin
  Result := True;
  if not ServiceNeedsRestart then
    exit;
  if not OwnsZapretService then begin
    Result := False;
    exit;
  end;

  if not WaitUntilServiceStable(State) then begin
    Result := False;
    exit;
  end;
  if State = issRunning then
    exit;
  if (State <> issStopped) and (State <> issPaused) then begin
    Result := False;
    exit;
  end;

  if State = issPaused then
    Started := Exec(
      ExpandConstant('{sys}\sc.exe'),
      'continue "' + ServiceName + '"',
      '',
      SW_SHOWNORMAL,
      ewWaitUntilTerminated,
      ResultCode)
  else
    Started := Exec(
      ExpandConstant('{sys}\sc.exe'),
      'start "' + ServiceName + '"',
      '',
      SW_SHOWNORMAL,
      ewWaitUntilTerminated,
      ResultCode);
  if (not Started) or ((ResultCode <> 0) and (ResultCode <> 1056)) then begin
    Result := False;
    exit;
  end;
  Result := WaitUntilServiceRunning;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  try
    if CheckForMutexes(ApplicationMutexes) then begin
      Result := 'Закройте Zapret Control Center и дождитесь завершения встроенного обновления.';
      exit;
    end;
    CaptureUserState;
    if not StopOwnedService(ServiceNeedsRestart) then
      Result := 'Не удалось остановить принадлежащую программе службу zapret. Установка отменена.';
  except
    Result := GetExceptionMessage;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then begin
    RestoreUserState;
    if not StartOwnedService then
      RaiseException('Программа обновлена, но службу zapret не удалось снова запустить.');
    ServiceRestarted := True;
  end;
end;

procedure DeinitializeSetup;
begin
  if ServiceNeedsRestart and not ServiceRestarted then begin
    if not StartOwnedService then
      Log('Could not restore the zapret service after an interrupted setup.');
  end;
  if StateDir <> '' then
    DelTree(StateDir, True, True, True);
end;

function DeleteOwnedService: Boolean;
var
  WasRunning: Boolean;
  ResultCode: Integer;
  State: TInstallerServiceState;
begin
  Result := True;
  if not OwnsZapretService then
    exit;

  if not StopOwnedService(WasRunning) then begin
    Result := False;
    exit;
  end;

  State := QueryServiceState;
  if State = issNotInstalled then
    exit;
  if (State = issUnknown) or (not OwnsZapretService) then begin
    Result := State <> issUnknown;
    exit;
  end;

  Result := Exec(
    ExpandConstant('{sys}\sc.exe'),
    'delete "' + ServiceName + '"',
    '',
    SW_SHOWNORMAL,
    ewWaitUntilTerminated,
    ResultCode) and ((ResultCode = 0) or (ResultCode = 1072));
end;

procedure RemoveOwnedAutoRun;
var
  Value: String;
  ExpectedDash: String;
  ExpectedSlash: String;
begin
  if not RegQueryStringValue(
    HKCU,
    AutoRunRegistryKey,
    AutoRunValueName,
    Value) then
    exit;

  ExpectedDash := '"' + RuntimePath('ZapretGUI.exe') + '" --minimized';
  ExpectedSlash := '"' + RuntimePath('ZapretGUI.exe') + '" /minimized';
  Value := Trim(Value);
  if (CompareText(Value, ExpectedDash) = 0) or
     (CompareText(Value, ExpectedSlash) = 0) then
    RegDeleteValue(HKCU, AutoRunRegistryKey, AutoRunValueName);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then begin
    if not DeleteOwnedService then
      RaiseException('Не удалось удалить принадлежащую программе службу zapret. Удаление отменено.');
    RemoveOwnedAutoRun;
  end;
end;
