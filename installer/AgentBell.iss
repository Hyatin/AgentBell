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

#define AppName "AgentBell"
#define AppPublisher "AgentBell"
#define TrayExe "AgentBell.Tray.exe"
#define IntegrationExe "AgentBell.Integration.exe"

[Setup]
AppId={{A17863B4-7E64-4D74-A0B4-004000000001}
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
Filename: "{app}\{#TrayExe}"; Description: "启动 AgentBell"; Flags: nowait postinstall skipifsilent

[Code]
var
  DeleteDataCheckBox: TNewCheckBox;

function TrayPath(): String;
begin
  Result := ExpandConstant('{app}\{#TrayExe}');
end;

function IntegrationPath(): String;
begin
  Result := ExpandConstant('{app}\{#IntegrationExe}');
end;

procedure RequestTrayShutdown();
var
  ResultCode: Integer;
begin
  if FileExists(TrayPath()) then
  begin
    Exec(TrayPath(), '--shutdown', ExpandConstant('{app}'), SW_HIDE,
      ewWaitUntilTerminated, ResultCode);
    Sleep(2000);
  end;
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

procedure InstallCodexIntegration();
var
  ResultCode: Integer;
begin
  if not Exec(IntegrationPath(), 'repair --json', ExpandConstant('{app}'),
    SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    RaiseException('无法启动 AgentBell Codex 集成管理器。');
  if ResultCode <> 0 then
    RaiseException(
      'Codex 集成失败。hooks.json 未被猜测性覆盖。请检查 CODEX_HOME 下的 hooks.json 以及 agentbell-backup 时间戳备份。');
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    ConfigureStartup();
    InstallCodexIntegration();
    if not WizardSilent then
      MsgBox(
        'Codex 将要求审核新的稳定 Hook 路径。请确认路径属于 AgentBell 后选择信任。' + #13#10 +
        'AgentBell 不会自动绕过或点击 Codex 的 Hook 信任确认。',
        mbInformation, MB_OK);
  end;
end;

function InitializeUninstall(): Boolean;
begin
  Result := True;
  DeleteDataCheckBox := TNewCheckBox.Create(UninstallProgressForm);
  DeleteDataCheckBox.Parent := UninstallProgressForm;
  DeleteDataCheckBox.Left := ScaleX(24);
  DeleteDataCheckBox.Top := UninstallProgressForm.StatusLabel.Top + ScaleY(56);
  DeleteDataCheckBox.Width := UninstallProgressForm.ClientWidth - ScaleX(48);
  DeleteDataCheckBox.Caption := '同时删除 AgentBell 配置、配对和事件历史';
  DeleteDataCheckBox.Checked :=
    CompareText(ExpandConstant('{param:DELETEUSERDATA|0}'), '1') = 0;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
  RemoveData: Boolean;
begin
  if CurUninstallStep = usUninstall then
  begin
    RequestTrayShutdown();
    RegDeleteValue(HKCU,
      'Software\Microsoft\Windows\CurrentVersion\Run',
      'AgentBell');
    if FileExists(IntegrationPath()) then
    begin
      if (not Exec(IntegrationPath(), 'uninstall --json', ExpandConstant('{app}'),
        SW_HIDE, ewWaitUntilTerminated, ResultCode)) or (ResultCode <> 0) then
        MsgBox(
          'AgentBell 未能安全移除 Codex Hook。hooks.json 已保留，请手工检查。',
          mbError, MB_OK);
    end;
  end;

  if CurUninstallStep = usPostUninstall then
  begin
    RemoveData := Assigned(DeleteDataCheckBox) and DeleteDataCheckBox.Checked;
    if RemoveData then
      DelTree(ExpandConstant('{localappdata}\AgentBell'), True, True, True);
  end;
end;
