#ifndef SourceDir
  #error SourceDir must point to the verified artifacts\m4-package directory.
#endif
#ifndef OutputDir
  #error OutputDir must point to artifacts\m4-installer.
#endif
#ifndef ProductVersion
  #error ProductVersion must be supplied by scripts\build-m4.ps1.
#endif
#ifndef InformationalVersion
  #define InformationalVersion ProductVersion
#endif
#ifndef AndroidApkFileName
  #define AndroidApkFileName "AgentBell-debug.apk"
#endif
#ifndef ApplicationId
  #define ApplicationId "{{A17863B4-7E64-4D74-A0B4-004000000001}"
#endif

#define AppName "AgentBell"
#define AppPublisher "AgentBell"
#define TrayExe "AgentBell.Tray.exe"
#define IntegrationExe "AgentBell.Integration.exe"

[Setup]
AppId={#ApplicationId}
AppName={#AppName}
AppVersion={#ProductVersion}
AppVerName={#AppName} {#InformationalVersion}
AppPublisher={#AppPublisher}
DefaultDirName={localappdata}\Programs\AgentBell
DefaultGroupName=AgentBell
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
MinVersion=10.0.17763
OutputDir={#OutputDir}
OutputBaseFilename=AgentBell-Setup-{#InformationalVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
Uninstallable=yes
UninstallDisplayIcon={app}\{#TrayExe}
CloseApplications=yes
CloseApplicationsFilter=AgentBell.Tray.exe
RestartApplications=no
UsePreviousAppDir=yes
UsePreviousTasks=yes
SetupLogging=yes
UninstallLogging=yes
VersionInfoVersion={#ProductVersion}
VersionInfoProductName={#AppName}
VersionInfoProductVersion={#ProductVersion}
VersionInfoDescription=AgentBell per-user installer
VersionInfoCompany=AgentBell contributors
VersionInfoCopyright=Licensed under Apache-2.0
#ifdef LicenseFile
LicenseFile={#LicenseFile}
#endif

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "startup"; Description: "登录 Windows 后启动 AgentBell"; GroupDescription: "启动选项:"; Flags: checkedonce
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "快捷方式:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\AgentBell.Tray.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\AgentBell.Hook.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\AgentBell.Integration.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\android\{#AndroidApkFileName}"; DestDir: "{app}\android"; Flags: ignoreversion

[Icons]
Name: "{group}\AgentBell"; Filename: "{app}\{#TrayExe}"
Name: "{group}\Android APK 文件夹"; Filename: "{sys}\explorer.exe"; Parameters: """{app}\android"""
Name: "{autodesktop}\AgentBell"; Filename: "{app}\{#TrayExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#TrayExe}"; Description: "启动 AgentBell"; Flags: nowait postinstall skipifsilent; Check: CodexIntegrationSucceeded

[UninstallDelete]
Type: filesandordirs; Name: "{localappdata}\AgentBell"; Check: ShouldDeleteUserData

[Code]
var
  DeleteDataCheckBox: TNewCheckBox;
  IntegrationFailureExitCode: Integer;
  IntegrationFailureStage: String;
  UninstallCodexHome: String;
  UninstallHooksPath: String;
  UninstallBackupCandidateCount: Integer;
  UninstallInitializationFailed: Boolean;
  UninstallHooksFileExists: Boolean;

function TrayPath(): String;
begin
  Result := ExpandConstant('{app}\{#TrayExe}');
end;

function IntegrationPath(): String;
begin
  Result := ExpandConstant('{app}\{#IntegrationExe}');
end;

function ResolveCodexHome(): String;
var
  CodexHome: String;
  UserProfile: String;
begin
  CodexHome := Trim(GetEnv('CODEX_HOME'));
  if CodexHome <> '' then
  begin
    Result := ExpandFileName(PathNormalizeSlashes(CodexHome));
    Exit;
  end;

  UserProfile := Trim(GetEnv('USERPROFILE'));
  if UserProfile = '' then
    RaiseException(
      'Unable to resolve Codex home: USERPROFILE is not available.');

  Result := ExpandFileName(PathCombine(UserProfile, '.codex'));
end;

function IntegrationParameters(const Operation: String;
  const CodexHome: String): String;
begin
  Result := Operation + ' --json --codex-home ' + AddQuotes(CodexHome);
end;

procedure RequestTrayShutdown();
var
  ResultCode: Integer;
begin
  if not FileExists(TrayPath()) then
  begin
    if IsUninstaller() then
      Log('AgentBell uninstall tray shutdown: skipped (executable missing).');
    Exit;
  end;

  if IsUninstaller() then
    Log('AgentBell uninstall tray shutdown: starting.');
  if not Exec(TrayPath(), '--shutdown', ExpandConstant('{app}'), SW_HIDE,
    ewWaitUntilTerminated, ResultCode) then
  begin
    if IsUninstaller() then
      Log('AgentBell uninstall tray shutdown: process start failed; continuing.');
    Exit;
  end;

  if IsUninstaller() then
    Log('AgentBell uninstall tray shutdown exit code: ' + IntToStr(ResultCode));
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  RequestTrayShutdown();
  Result := '';
end;

procedure ConfigureStartup();
var
  Command: String;
begin
  Command := AddQuotes(TrayPath()) + ' --startup';
  if WizardIsTaskSelected('startup') then
    RegWriteStringValue(HKCU,
      'Software\Microsoft\Windows\CurrentVersion\Run',
      'AgentBell', Command)
  else
    RegDeleteValue(HKCU,
      'Software\Microsoft\Windows\CurrentVersion\Run',
      'AgentBell');
end;

procedure LogCapturedLines(const StreamName: String; const Lines: TArrayOfString);
var
  Index: Integer;
begin
  if GetArrayLength(Lines) = 0 then
  begin
    Log('AgentBell Integration ' + StreamName + ': <empty>');
    Exit;
  end;

  for Index := 0 to GetArrayLength(Lines) - 1 do
    Log('AgentBell Integration ' + StreamName + ': ' + Lines[Index]);
end;

function ExecuteIntegration(
  const StageName: String;
  const Parameters: String;
  var ResultCode: Integer): Boolean;
var
  Output: TExecOutput;
begin
  Log('AgentBell Integration stage: ' + StageName);
  Log('AgentBell Integration executable: ' + IntegrationPath());
  Log(
    'AgentBell Integration parameter structure: operation, machine-readable-json, explicit-codex-home');
  try
    Result := ExecAndCaptureOutput(
      IntegrationPath(), Parameters, ExpandConstant('{app}'),
      SW_SHOWNORMAL, ewWaitUntilTerminated, ResultCode, Output);
  except
    Log('AgentBell Integration process start/capture failed: ' + GetExceptionMessage());
    Result := False;
  end;

  if not Result then
  begin
    Log('AgentBell Integration child started: no');
    Exit;
  end;

  Log('AgentBell Integration child started: yes');
  Log('AgentBell Integration exit code: ' + IntToStr(ResultCode));
  LogCapturedLines('stdout', Output.StdOut);
  LogCapturedLines('stderr', Output.StdErr);
  if Output.Error then
    Log('AgentBell Integration output capture status: incomplete')
  else
    Log('AgentBell Integration output capture status: complete');
end;

function InstallCodexIntegration(): Boolean;
var
  ResultCode: Integer;
  CodexHome: String;
begin
  Result := False;
  IntegrationFailureExitCode := 0;
  IntegrationFailureStage := '';

  try
    CodexHome := ResolveCodexHome();
    Log('AgentBell Integration resolved CODEX_HOME: ' + CodexHome);
    Log('AgentBell Integration hooks.json path: ' +
      PathCombine(CodexHome, 'hooks.json'));
  except
    IntegrationFailureExitCode := 12;
    IntegrationFailureStage := 'codex_home_resolve';
    Log('AgentBell Integration exception type: PascalScriptException');
    Log('AgentBell Integration Codex home resolution failed: ' +
      GetExceptionMessage());
    Exit;
  end;

  if not ExecuteIntegration(
    'repair', IntegrationParameters('repair', CodexHome), ResultCode) then
  begin
    IntegrationFailureExitCode := 12;
    IntegrationFailureStage := 'process_start';
    Exit;
  end;
  if ResultCode <> 0 then
  begin
    IntegrationFailureExitCode := ResultCode;
    IntegrationFailureStage := 'repair';
    Exit;
  end;

  if not ExecuteIntegration(
    'verify', IntegrationParameters('verify', CodexHome), ResultCode) then
  begin
    IntegrationFailureExitCode := 12;
    IntegrationFailureStage := 'verification_start';
    Exit;
  end;
  if ResultCode <> 0 then
  begin
    IntegrationFailureExitCode := ResultCode;
    IntegrationFailureStage := 'verification';
    Exit;
  end;

  Log('AgentBell Integration completed: repair and verification succeeded.');
  Result := True;
end;

function CodexIntegrationSucceeded(): Boolean;
begin
  Result := IntegrationFailureExitCode = 0;
end;

function GetCustomSetupExitCode(): Integer;
begin
  Result := IntegrationFailureExitCode;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    if not InstallCodexIntegration() then
    begin
      Log(
        'AgentBell installation failed during Codex Integration stage ' +
        IntegrationFailureStage + ' with exit code ' +
        IntToStr(IntegrationFailureExitCode) + '.');
      SuppressibleMsgBox(
        'Codex 集成失败。阶段：' + IntegrationFailureStage +
        '；退出码：' + IntToStr(IntegrationFailureExitCode) + '。' + #13#10 +
        '安装日志包含已解析的 CODEX_HOME、hooks.json 路径和脱敏子进程诊断。',
        mbError, MB_OK, IDOK);
    end
    else
    begin
      ConfigureStartup();
    end;

    if CodexIntegrationSucceeded() and (not WizardSilent) then
      MsgBox(
        'Codex 将要求审核新的稳定 Hook 路径。请确认路径属于 AgentBell 后选择信任。' + #13#10 +
        'AgentBell 不会自动绕过或点击 Codex 的 Hook 信任确认。',
        mbInformation, MB_OK);
  end;
end;

function InitializeUninstall(): Boolean;
var
  FindRec: TFindRec;
begin
  Result := True;
  DeleteDataCheckBox := nil;
  UninstallBackupCandidateCount := 0;
  UninstallInitializationFailed := False;
  UninstallHooksFileExists := False;

  try
    UninstallCodexHome := ResolveCodexHome();
    UninstallHooksPath := PathCombine(UninstallCodexHome, 'hooks.json');
    UninstallHooksFileExists := FileExists(UninstallHooksPath);

    if FindFirst(UninstallHooksPath + '.*backup-*', FindRec) then
    begin
      try
        repeat
          if FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY = 0 then
            UninstallBackupCandidateCount := UninstallBackupCandidateCount + 1;
        until not FindNext(FindRec);
      finally
        FindClose(FindRec);
      end;
    end;

    Log('AgentBell uninstall stage: initialize.');
    Log('AgentBell uninstall user data directory: ' +
      ExpandConstant('{localappdata}\AgentBell'));
    Log('AgentBell uninstall resolved CODEX_HOME: ' + UninstallCodexHome);
    Log('AgentBell uninstall hooks.json path: ' + UninstallHooksPath);
    if UninstallHooksFileExists then
      Log('AgentBell uninstall hooks.json exists: yes')
    else
      Log('AgentBell uninstall hooks.json exists: no');
    Log('AgentBell uninstall backup candidate count: ' +
      IntToStr(UninstallBackupCandidateCount));
  except
    Log('AgentBell uninstall exception type: PascalScriptException');
    Log('AgentBell uninstall initialize exception: ' + GetExceptionMessage());
    SuppressibleMsgBox(
      'AgentBell 卸载初始化失败。请查看卸载日志中的 initialize 阶段。',
      mbError, MB_OK, IDOK);
    Abort;
  end;
end;

procedure InitializeUninstallProgressForm;
begin
  Log('AgentBell uninstall stage: initialize_progress_form.');
  if UninstallSilent() then
  begin
    Log('AgentBell uninstall data checkbox: skipped for silent mode.');
    Exit;
  end;

  try
    DeleteDataCheckBox := TNewCheckBox.Create(UninstallProgressForm);
    DeleteDataCheckBox.Parent := UninstallProgressForm;
    DeleteDataCheckBox.Left := ScaleX(24);
    DeleteDataCheckBox.Top := UninstallProgressForm.StatusLabel.Top + ScaleY(56);
    DeleteDataCheckBox.Width := UninstallProgressForm.ClientWidth - ScaleX(48);
    DeleteDataCheckBox.Caption := '同时删除 AgentBell 配置、配对和事件历史';
    DeleteDataCheckBox.Checked :=
      CompareText(ExpandConstant('{param:DELETEUSERDATA|0}'), '1') = 0;
    Log('AgentBell uninstall data checkbox: initialized.');
  except
    DeleteDataCheckBox := nil;
    UninstallInitializationFailed := True;
    Log('AgentBell uninstall exception type: PascalScriptException');
    Log('AgentBell uninstall progress form exception: ' + GetExceptionMessage());
    Log('AgentBell uninstall data checkbox: initialization failed.');
  end;
end;

function ShouldDeleteUserData(): Boolean;
begin
  Result :=
    CompareText(ExpandConstant('{param:DELETEUSERDATA|0}'), '1') = 0;
  if Assigned(DeleteDataCheckBox) then
    Result := DeleteDataCheckBox.Checked;
end;

procedure LogOptionalIntegrationFailure(const StageName: String;
  const ResultCode: Integer);
begin
  Log('AgentBell uninstall optional integration cleanup failed.');
  Log('AgentBell uninstall failed stage: ' + StageName);
  Log('AgentBell uninstall child exit code: ' + IntToStr(ResultCode));
  SuppressibleMsgBox(
    'AgentBell 未能安全移除 Codex Hook。阶段：' + StageName +
    '；退出码：' + IntToStr(ResultCode) + '。' + #13#10 +
    'hooks.json 与备份均已保留，程序文件卸载将继续。',
    mbError, MB_OK, IDOK);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
begin
  if CurUninstallStep = usUninstall then
  begin
    try
      if UninstallInitializationFailed then
      begin
        Log('AgentBell uninstall critical stage: initialize_progress_form.');
        Abort;
      end;
#ifdef UninstallFailureTest
      if CompareText(
        ExpandConstant('{param:FORCECRITICALUNINSTALLFAILURE|0}'), '1') = 0 then
      begin
        Log('AgentBell uninstall test stage: forced_critical_failure.');
        Abort;
      end;
#endif
      Log('AgentBell uninstall stage: stop_processes.');
      RequestTrayShutdown();

      Log('AgentBell uninstall stage: remove_startup.');
      if RegDeleteValue(HKCU,
        'Software\Microsoft\Windows\CurrentVersion\Run',
        'AgentBell') then
        Log('AgentBell uninstall startup entry: removed.')
      else
        Log('AgentBell uninstall startup entry: skipped (not present).');

      Log('AgentBell uninstall stage: codex_hook_cleanup.');
      if not UninstallHooksFileExists then
      begin
        Log('AgentBell uninstall Codex cleanup: skipped (hooks.json missing).');
      end
      else if not FileExists(IntegrationPath()) then
      begin
        Log('AgentBell uninstall Codex cleanup: skipped (Integration executable missing).');
      end
      else if not ExecuteIntegration(
        'uninstall',
        IntegrationParameters('uninstall', UninstallCodexHome),
        ResultCode) then
      begin
        LogOptionalIntegrationFailure('process_start', 12);
      end
      else if ResultCode <> 0 then
      begin
        LogOptionalIntegrationFailure('integration', ResultCode);
      end
      else
      begin
        Log('AgentBell uninstall Codex cleanup: completed or safely skipped.');
      end;

      Log('AgentBell uninstall stage: remove_files.');
    except
      Log('AgentBell uninstall exception type: PascalScriptException');
      Log('AgentBell uninstall critical exception: ' + GetExceptionMessage());
      SuppressibleMsgBox(
        'AgentBell 卸载遇到关键错误，尚未开始删除程序文件。请查看卸载日志。',
        mbError, MB_OK, IDOK);
      Abort;
    end;
  end;

  if CurUninstallStep = usPostUninstall then
  begin
    Log('AgentBell uninstall stage: post_uninstall.');
    if ShouldDeleteUserData() then
      Log('AgentBell uninstall user data action: delete requested.')
    else
      Log('AgentBell uninstall user data action: retained.');
  end;
end;

procedure DeinitializeUninstall;
begin
  Log('AgentBell uninstall stage: deinitialize.');
  DeleteDataCheckBox := nil;
end;
