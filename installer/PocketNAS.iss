; PocketNAS PC 端安装包（Inno Setup 6）
;
; 目标（对应 docs/msi-packaging-checklist.md 的主线）：
;   - 落地到 Program Files，绝不落到网盘 / 云同步目录（P10 / P12）
;   - 安装期一次性注册 WebDAV / FTPS 两个服务，开机自启，不依赖当前登录用户（P06 / P11）
;   - 覆盖安装前先停服务，避免 exe 被占用（P17）
;   - 卸载保留共享目录与配置，由用户选择是否清除（P19）
;
; 数据落地说明（v1 的取舍，写在这里免得以后忘）：
;   程序本身在 {app}，服务的配置 / 日志 / 共享目录仍在 {app}\service 下 —— 这是代码现状
;   （ConfigHelper / FtpsOptions 都以 exe 所在目录为基准）。为了界面在非管理员下也能保存配置，
;   安装期只对「配置文件 + 共享目录 + 日志目录」授 Users:Modify，**不给 service 目录本身授权**，
;   否则普通用户可以替换 WebDAVService.exe，等于把 SYSTEM 权限白送。
;   彻底方案是把这些路径挪到 {commonappdata}\PocketNAS，属于代码改动，另行排期。

#define MyAppName "PocketNAS"
#define MyAppVersion "1.0.1"
#define MyAppPublisher "PocketNAS"
#define MyAppExeName "PocketNAS-PC.exe"

; 由 build.ps1 用 /DStageApp=... 覆盖；默认指向最近一次打包目录
#ifndef StageApp
  #define StageApp "..\dist\stage-20261107-2231\app"
#endif

#define WebDavSvcName "WebDAVService"
#define FtpsSvcName "FtpsService"

[Setup]
AppId={{6F1D8A2C-5B3E-4C7A-9E21-3D5A8B4F7C10}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\PocketNAS
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
DisableDirPage=no
OutputDir=..\dist
OutputBaseFilename=PocketNAS-PC-Setup-{#MyAppVersion}
SetupIconFile=..\src\PocketNAS.PC\Assets\pocketnas.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName} {#MyAppVersion}
Compression=lzma2/max
SolidCompression=yes
LZMAUseSeparateProcess=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
RestartApplications=no
AllowNoIcons=yes
VersionInfoVersion={#MyAppVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppName} 安装程序
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}

[Languages]
Name: "cn"; MessagesFile: "lang\ChineseSimplified.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "installservice"; Description: "注册并启动 WebDAV / FTPS 服务（开机自启，无人登录也能访问）"; GroupDescription: "服务："; Flags: checkedonce

[Files]
; 整目录铺开；用户自己改出来的配置和共享数据不在包里，不会被覆盖
Source: "{#StageApp}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\卸载 {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; runascurrentuser 必须加：主程序 manifest 是 requireAdministrator，
; postinstall 默认以原始（未提权）用户 CreateProcess 拉它必报 740；
; 用安装器的提权 token 启动就能正常拉起
Filename: "{app}\{#MyAppExeName}"; Description: "启动 {#MyAppName}"; Flags: nowait postinstall skipifsilent runascurrentuser

; 服务的注册与卸载都放在 [Code] 里做，不用 [UninstallRun]：
; 只有「这次安装确实注册过服务」才写标记文件，卸载时按标记决定要不要删服务。
; 否则便携版/开发目录里已经装好的同名服务会被这个安装包的卸载程序顺手删掉。

[Code]
var
  RemoveUserData: Boolean;

{ 装完服务后留下的标记：卸载时凭它判断这两个服务是不是本安装包注册的 }
function ServiceFlagPath: String;
begin
  Result := ExpandConstant('{app}\service\.setup-installed-services');
end;

{ ---------- 小工具 ---------- }

function RunHidden(const Exe, Params: String): Integer;
begin
  Exec(Exe, Params, '', SW_HIDE, ewWaitUntilTerminated, Result);
end;

{ 网盘 / 云同步目录一旦出现，服务在 Session 0 必然读不到占位符（0x8007018B），提前拦住 }
function LooksLikeCloudPath(const Path: String): Boolean;
var
  P: String;
begin
  P := Lowercase(Path);
  Result :=
    (Pos('onedrive', P) > 0) or (Pos('dropbox', P) > 0) or
    (Pos('google drive', P) > 0) or (Pos('googledrive', P) > 0) or
    (Pos('nutstore', P) > 0) or (Pos('坚果云', P) > 0) or
    (Pos('baidunetdisk', P) > 0) or (Pos('百度网盘', P) > 0) or
    (Pos('微云', P) > 0) or (Pos('weiyun', P) > 0) or
    (Pos('synologydrive', P) > 0) or (Pos('pcloud', P) > 0);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if CurPageID = wpSelectDir then
    if LooksLikeCloudPath(WizardDirValue) then
    begin
      MsgBox('不能装在网盘 / 云同步目录里：' + #13#10 + WizardDirValue + #13#10#13#10 +
             '服务运行在 Session 0，看不到登录用户会话里的云占位符，' +
             '启动时会因为加载不到运行时 DLL 而失败（0x8007018B）。' + #13#10#13#10 +
             '请改成纯本地磁盘目录，例如 C:\Program Files\PocketNAS。',
             mbError, MB_OK);
      Result := False;
    end;
end;

{ 服务配置、日志、共享目录都在 service 子目录下；界面以普通用户身份跑，必须能写 }
procedure PrepareDataDirs;
var
  Base, Svc, Ftps: String;
begin
  Base := ExpandConstant('{app}');
  Svc  := Base + '\service';
  Ftps := Svc + '\ftps';

  ForceDirectories(Svc + '\logs');
  ForceDirectories(Svc + '\webdav_share');
  ForceDirectories(Ftps + '\logs');
  ForceDirectories(Ftps + '\ftps_share');

  { 空 JSON 反序列化后等于全部默认值，界面保存时再用真实配置覆盖 }
  if not FileExists(Svc + '\webdav_config.json') then
    SaveStringToFile(Svc + '\webdav_config.json', '{}', False);
  if not FileExists(Ftps + '\ftps_config.json') then
    SaveStringToFile(Ftps + '\ftps_config.json', '{}', False);
end;

procedure GrantDataAcls;
var
  Base, Svc, Ftps: String;
begin
  Base := ExpandConstant('{app}');
  Svc  := Base + '\service';
  Ftps := Svc + '\ftps';

  { 只授 Users 对这些「数据」的修改权，不授 service 目录本身：}
  { 否则普通用户可以替换 WebDAVService.exe，等于把 SYSTEM 权限送出去 }
  RunHidden('icacls.exe', '"' + Svc + '\webdav_config.json" /grant *S-1-5-32-545:(M)');
  RunHidden('icacls.exe', '"' + Ftps + '\ftps_config.json" /grant *S-1-5-32-545:(M)');
  RunHidden('icacls.exe', '"' + Svc + '\webdav_share" /grant *S-1-5-32-545:(OI)(CI)(M) /T');
  RunHidden('icacls.exe', '"' + Svc + '\logs" /grant *S-1-5-32-545:(OI)(CI)(M) /T');
  RunHidden('icacls.exe', '"' + Ftps + '\logs" /grant *S-1-5-32-545:(OI)(CI)(M) /T');
  RunHidden('icacls.exe', '"' + Ftps + '\ftps_share" /grant *S-1-5-32-545:(OI)(CI)(M) /T');
end;

{ ---------- 覆盖安装：先停服务（P17）；装完再注册 ---------- }

procedure CurStepChanged(CurStep: TSetupStep);
var
  Exe: String;
  R1, R2: Integer;
begin
  if CurStep = ssInstall then
  begin
    RunHidden('net.exe', 'stop "{#WebDavSvcName}"');
    RunHidden('net.exe', 'stop "{#FtpsSvcName}"');
    Sleep(1500);
  end
  else if CurStep = ssPostInstall then
  begin
    PrepareDataDirs;
    GrantDataAcls;

    if WizardIsTaskSelected('installservice') then
    begin
      Exe := ExpandConstant('{app}\{#MyAppExeName}');
      { 复用程序自己的提权入口：防火墙规则、URL ACL、证书绑定都在里面，不另写一份 }
      R1 := RunHidden(Exe, '--install');
      R2 := RunHidden(Exe, '--ftps-install');
      if (R1 = 0) and (R2 = 0) then
        SaveStringToFile(ServiceFlagPath, '1', False)
      else if not WizardSilent then
        MsgBox('服务注册没有全部成功（WebDAV 返回 ' + IntToStr(R1) +
               '，FTPS 返回 ' + IntToStr(R2) + '）。' + #13#10#13#10 +
               '文件已经装好了，可以打开 PocketNAS 在对应页面点「安装服务」重试；' + #13#10 +
               '失败详情见 %TEMP%\webdav_elevated_error.log 与 ftps_elevated_error.log。',
               mbError, MB_OK);
    end;
  end;
end;

{ ---------- 卸载：先删自己注册的服务，再问要不要连数据一起删（P19） ---------- }

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Share, Cfg, Exe: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    { 只有本安装包注册过服务才删；否则会把便携版 / 开发目录里的同名服务一起删掉 }
    if FileExists(ServiceFlagPath) then
    begin
      Exe := ExpandConstant('{app}\{#MyAppExeName}');
      RunHidden(Exe, '--ftps-uninstall');
      RunHidden(Exe, '--uninstall');
      Sleep(1000);
    end;
  end
  else if CurUninstallStep = usPostUninstall then
  begin
    Share := ExpandConstant('{app}\service\webdav_share');
    Cfg   := ExpandConstant('{app}\service\webdav_config.json');
    if DirExists(Share) or FileExists(Cfg) then
    begin
      { 静默卸载不能弹框，否则会一直卡在那里等输入；默认保留数据 }
      if UninstallSilent then
        RemoveUserData := False
      else
        RemoveUserData :=
          MsgBox('是否连共享目录和配置一起删除？' + #13#10#13#10 +
                 '共享目录里可能是你放进去的文件；选择「否」会保留：' + #13#10 +
                 Share + #13#10 + Cfg,
                 mbConfirmation, MB_YESNO) = IDYES;
    end;

    if RemoveUserData then
      DelTree(ExpandConstant('{app}'), True, True, True);
  end;
end;
