; EasyIntercept Windows installer (Inno Setup 6)
; Build via ..\build-installer.ps1, or: ISCC.exe /DAppVersion=1.2.3 EasyIntercept.iss
; Expects the published app in ..\dist\publish (see build-installer.ps1).

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#define AppName "EasyIntercept"
#define AppPublisher "Martijn Muurman"
#define AppExe "EasyIntercept.exe"
#define PublishDir "..\dist\publish"
#define DefaultUiPort "1337"
#define ProxyPort "9999"

[Setup]
AppId={{9B1F3C0E-6D2A-4C8B-8E5F-2A7D4E1C9B30}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppCopyright=Copyright (C) {#AppPublisher}. MIT License.
AppPublisherURL=https://github.com/gluip/easy-intercept
AppSupportURL=https://github.com/gluip/easy-intercept/issues
AppUpdatesURL=https://github.com/gluip/easy-intercept/releases
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\dist
OutputBaseFilename={#AppName}-Setup-{#AppVersion}
SetupIconFile=..\EasyIntercept\icon.ico
UninstallDisplayIcon={app}\{#AppExe}
; Same lightning bolt as the web UI's favicon; regenerate with installer\make-icons.ps1
WizardSmallImageFile=wizard-small.bmp,wizard-small-150.bmp,wizard-small-200.bmp
WizardImageFile=wizard-large.bmp,wizard-large-150.bmp,wizard-large-200.bmp
LicenseFile=..\LICENSE
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
VersionInfoVersion={#AppVersion}
VersionInfoCompany={#AppPublisher}
VersionInfoProductName={#AppName}
VersionInfoDescription={#AppName} Setup
VersionInfoCopyright=Copyright (C) {#AppPublisher}. MIT License.

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "startup"; Description: "Start {#AppName} automatically when Windows starts (runs quietly with a tray icon)"; GroupDescription: "Startup:"
Name: "installca"; Description: "Install the {#AppName} root CA certificate (required for HTTPS interception)"; GroupDescription: "HTTPS:"
Name: "firewall"; Description: "Add a Windows Firewall rule for {#AppName} (proxy port {#ProxyPort} and the web UI)"; GroupDescription: "Network:"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; Machine-wide autostart (per-machine install). --autostart = no browser window, tray icon only.
Root: HKLM; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#AppName}"; ValueData: """{app}\{#AppExe}"" --autostart"; Flags: uninsdeletevalue; Tasks: startup
Root: HKLM; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "{#AppName}"; Flags: deletevalue; Tasks: not startup

[Run]
; Runs elevated: generates the CA (if missing) in %LOCALAPPDATA%\EasyIntercept\certs and trusts it machine-wide.
Filename: "{app}\{#AppExe}"; Parameters: "--install-ca"; StatusMsg: "Installing root CA certificate..."; Flags: runhidden waituntilterminated; Tasks: installca
Filename: "netsh"; Parameters: "advfirewall firewall delete rule name=""{#AppName}"""; Flags: runhidden waituntilterminated; Tasks: firewall
Filename: "netsh"; Parameters: "advfirewall firewall add rule name=""{#AppName}"" dir=in action=allow program=""{app}\{#AppExe}"" enable=yes"; StatusMsg: "Adding firewall rule..."; Flags: runhidden waituntilterminated; Tasks: firewall
; postinstall entries run as the original (non-elevated) user by default.
Filename: "{app}\{#AppExe}"; Description: "Start {#AppName} now"; Flags: postinstall nowait skipifsilent

[UninstallRun]
Filename: "taskkill"; Parameters: "/IM {#AppExe} /F"; Flags: runhidden; RunOnceId: "KillApp"
Filename: "netsh"; Parameters: "advfirewall firewall delete rule name=""{#AppName}"""; Flags: runhidden; RunOnceId: "DelFirewall"

[UninstallDelete]
Type: files; Name: "{app}\appsettings.Production.json"
; Note: %LOCALAPPDATA%\EasyIntercept (sessions, mock rules, CA) is intentionally kept.

[Code]
var
  PortPage: TInputQueryWizardPage;
  PortPrefilled: Boolean;

function SettingsFile(): String;
begin
  Result := ExpandConstant('{app}\appsettings.Production.json');
end;

{ Minimal extraction of "UiPort": <digits> from the JSON written by a previous install. }
function ExtractPort(const Json: String): String;
var
  P, I: Integer;
  S: String;
begin
  Result := '';
  P := Pos('"UiPort"', Json);
  if P = 0 then Exit;
  S := Copy(Json, P + Length('"UiPort"'), Length(Json));
  P := Pos(':', S);
  if P = 0 then Exit;
  S := Trim(Copy(S, P + 1, Length(S)));
  I := 1;
  while (I <= Length(S)) and (S[I] >= '0') and (S[I] <= '9') do
  begin
    Result := Result + S[I];
    I := I + 1;
  end;
end;

function ExistingPortOrDefault(): String;
var
  Content: AnsiString;
  Found: String;
begin
  Result := '{#DefaultUiPort}';
  if LoadStringFromFile(SettingsFile(), Content) then
  begin
    Found := ExtractPort(String(Content));
    if Found <> '' then Result := Found;
  end;
end;

procedure InitializeWizard();
begin
  PortPage := CreateInputQueryPage(wpSelectDir,
    'Web UI port',
    'Which port should the {#AppName} web UI listen on?',
    'The web UI is served on http://localhost:<port>. The default is {#DefaultUiPort}; change it if another ' +
    'application (for example Strapi) already uses that port. The proxy itself always listens on port {#ProxyPort}.' + #13#10#13#10 +
    'You can change this later in %LOCALAPPDATA%\{#AppName}\appsettings.json ("UiPort").');
  PortPage.Add('Port:', False);
  PortPage.Values[0] := '{#DefaultUiPort}';
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  // The {app} constant is only known once the directory page has been passed.
  if (CurPageID = PortPage.ID) and not PortPrefilled then
  begin
    PortPage.Values[0] := ExistingPortOrDefault();
    PortPrefilled := True;
  end;

  if CurPageID = wpFinished then
    WizardForm.FinishedLabel.Caption := WizardForm.FinishedLabel.Caption + #13#10#13#10 +
      'Web UI:  http://localhost:' + Trim(PortPage.Values[0]) + #13#10 +
      'Proxy:   127.0.0.1:{#ProxyPort}' + #13#10 +
      'Data:    %LOCALAPPDATA%\{#AppName}';
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Port: Integer;
begin
  Result := True;
  if CurPageID = PortPage.ID then
  begin
    Port := StrToIntDef(Trim(PortPage.Values[0]), -1);
    if (Port < 1) or (Port > 65535) or (Port = {#ProxyPort}) then
    begin
      MsgBox('Please enter a port between 1 and 65535 (port {#ProxyPort} is reserved for the proxy).', mbError, MB_OK);
      Result := False;
    end;
  end;
end;

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo,
  MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
begin
  Result := MemoDirInfo + NewLine + NewLine + 'Web UI port:' + NewLine + Space + Trim(PortPage.Values[0]);
  if MemoTasksInfo <> '' then
    Result := Result + NewLine + NewLine + MemoTasksInfo;
end;

{ Stop a running instance (the tray app has no window, so Restart Manager can't close it gracefully). }
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Result := '';
  Exec('taskkill', '/IM {#AppExe} /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    SaveStringToFile(SettingsFile(),
      '{' + #13#10 + '  "UiPort": ' + Trim(PortPage.Values[0]) + #13#10 + '}' + #13#10, False);
end;
