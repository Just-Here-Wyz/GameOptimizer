; ============================================================================
;  游戏优化助手 —— Inno Setup 安装脚本
;
;  作用：把 dotnet publish 产出的单文件自包含 exe 封装成
;        「可安装 / 可覆盖安装 / 可卸载」的 Windows 安装程序。
;
;  推荐用法：
;      pwsh -File installer\build-installer.ps1
;  该脚本负责读版本号 → dotnet publish → 调 ISCC 编译本文件 → 校验产物。
;
;  手动编译（版本号由命令行传入，脚本里不写死）：
;      ISCC.exe /DMyAppVersion=<版本号> installer\GameOptimizer.iss
;  不传 MyAppVersion 时回退读取暂存目录里 exe 的文件版本，
;  仍然不会出现「脚本里留着一个过期版本号」的情况。
;
;  刻意不做的事（与 docs/00 的边界一致）：
;    · 不启动游戏模式、不结束用户进程、不改电源计划，安装器只复制文件 + 建快捷方式；
;    · 不写注册表启动项、不装服务、不加驱动、不做自动更新/在线版本检查；
;    · 不碰 %LocalAppData%\GameOptimizer 里的用户数据
;      （background-whitelist.json / active-state.json / downloads.json 全部保留）。
; ============================================================================

; ---- 版本号：唯一来源是仓库根的 Directory.Build.props，由 build-installer.ps1 读出来传进来 ----
#ifndef MyAppVersion
  #define MyAppVersion GetFileVersion(AddBackslash(SourcePath) + "..\publish\win-x64\app\GameOptimizer.exe")
  #define MyAppVersionQuad MyAppVersion
#else
  #define MyAppVersionQuad MyAppVersion + ".0"
#endif

; ---- 路径：全部相对本脚本解析，不依赖开发者机器上的绝对路径 ----
#ifndef StageDir
  #define StageDir AddBackslash(SourcePath) + "..\publish\win-x64\app"
#endif
#ifndef OutputDir
  #define OutputDir AddBackslash(SourcePath) + "..\publish\win-x64"
#endif
#define StageDirAbs AddBackslash(StageDir)
#define OutputDirAbs AddBackslash(OutputDir)
#define RepoRoot AddBackslash(SourcePath) + ".."
#define LangFile AddBackslash(SourcePath) + "languages\ChineseSimplified.isl"

#define MyAppName "游戏优化助手"
#define MyAppExeName "GameOptimizer.exe"
#define MyAppPublisher "GameOptimizer"
#define MyAppCopyright "Copyright (C) 2026 Wang Yazheng"

[Setup]
; AppId 决定「同一个应用」的身份：保持这个 GUID 不变，新安装包才能覆盖旧版本，
; 而不是在「应用和功能」里多出一条。
AppId={{8C1F4A62-2B7D-4E93-9A55-6D0C3F17B284}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL=https://github.com/Just-Here-Wyz/GameOptimizer
AppSupportURL=https://github.com/Just-Here-Wyz/GameOptimizer
AppCopyright={#MyAppCopyright}
; exe 属性里看到的安装包版本（四段数字，来自同一个 MyAppVersion）
VersionInfoVersion={#MyAppVersionQuad}
VersionInfoProductVersion={#MyAppVersionQuad}
VersionInfoProductName={#MyAppName}
VersionInfoDescription={#MyAppName} {#MyAppVersion} 安装程序
VersionInfoCompany={#MyAppPublisher}
VersionInfoCopyright={#MyAppCopyright}

; 安装目录与应用数据目录必须分开：
;   程序 → {autopf}\GameOptimizer（按安装范围解析成 Program Files 或 %LocalAppData%\Programs）
;   数据 → %LocalAppData%\GameOptimizer（由程序自己创建，安装器不碰）
DefaultDirName={autopf}\GameOptimizer
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
AllowNoIcons=yes

; 默认「仅为我安装」：向导全程不请求管理员权限，只有应用自己启动时才提权；
; 用户确实要装到 Program Files 时，可在向导第一页选「为所有使用这台电脑的人安装」，
; 由 Inno 统一弹一次 UAC 重启向导。避免「只复制文件却全程管理员」。
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

; 覆盖安装：沿用上次的安装目录与快捷方式选择
UsePreviousAppDir=yes
UsePreviousTasks=yes

; 程序是 x64 自包含单文件
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; 不自动结束应用：文件被占用时由下面的 [Code] 明确提示用户自己关，
; 由用户在应用里正常退出（托盘 → 退出），安装器绝不强杀进程。
CloseApplications=no
RestartApplications=no

LicenseFile={#RepoRoot}\LICENSE
OutputDir={#OutputDirAbs}
OutputBaseFilename=GameOptimizer-Setup-v{#MyAppVersion}
SetupIconFile={#RepoRoot}\src\GameOptimizer.App\app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName} {#MyAppVersion}
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
SetupLogging=yes

[Languages]
; Inno 6.7 官方安装包未附带简体中文，文件取自 Inno Setup 源码仓库
; Files/Languages/ChineseSimplified.isl（保留原作者署名）
Name: "chs"; MessagesFile: "{#LangFile}"

[Tasks]
Name: "startmenuicon"; Description: "创建开始菜单快捷方式"; GroupDescription: "快捷方式："
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "快捷方式："; Flags: unchecked

[Files]
; 只打包发布目录里的产物（单文件 exe），不含源码、测试产物、开发缓存
Source: "{#StageDirAbs}*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; 分发合规文件（MIT 许可 + 第三方署名），不是源码
Source: "{#RepoRoot}\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
Source: "{#RepoRoot}\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Comment: "{#MyAppName} {#MyAppVersion}"; Tasks: startmenuicon
Name: "{group}\卸载 {#MyAppName}"; Filename: "{uninstallexe}"; Tasks: startmenuicon
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Comment: "{#MyAppName} {#MyAppVersion}"; Tasks: desktopicon

[Run]
; 完成页的「立即启动」勾选项。程序自身声明 requireAdministrator，启动时会弹 UAC。
Filename: "{app}\{#MyAppExeName}"; Description: "立即启动 {#MyAppName}"; Flags: nowait postinstall skipifsilent

[Code]
const
  { 与应用 SingleInstanceGate.DefaultMutexName 保持一致（第二次启动靠它唤醒已有窗口）。
    安装器只「检测」这个互斥体，不管有没有拿到，都不去结束进程。 }
  AppMutexName = 'Local\GameOptimizer.SingleInstance.v1';

function AppIsRunning: Boolean;
begin
  Result := CheckForMutexes(AppMutexName);
end;

{ 应用正在运行时反复询问，直到用户自己关掉或取消安装。
  返回 False 时安装/卸载都会中止，界面不会出现「已成功」的假象。 }
function ConfirmAppClosed(const ActionName: String): Boolean;
var
  Answer: Integer;
begin
  Result := True;
  while AppIsRunning do
  begin
    Answer := MsgBox('检测到「游戏优化助手」正在运行，无法' + ActionName + '。' + #13#10#13#10 +
      '请先在任务栏托盘图标上右键 → 退出，等程序完全关闭后再点「重试」。' + #13#10 +
      '安装程序不会强制结束你的进程。',
      mbError, MB_RETRYCANCEL);
    if Answer <> IDRETRY then
    begin
      Result := False;
      Exit;
    end;
  end;
end;

function InitializeSetup: Boolean;
begin
  Result := ConfirmAppClosed('安装');
end;

{ 用户可能在向导打开期间又启动了应用：正式复制文件前再查一次。
  应用只要在跑就一定持有这个互斥体，所以这里查到的就是「窗口还开着」。
  真的被别的程序独占打开时，Inno 自带的复制失败对话框会如实报错并给重试，
  不会出现「装完了」的假象。 }
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  NeedsRestart := False;

  if AppIsRunning then
    Result := '「游戏优化助手」正在运行，请先从托盘退出后再继续安装。';
end;

function InitializeUninstall: Boolean;
begin
  Result := ConfirmAppClosed('卸载');
end;

{ 卸载完成后如实告知数据还在哪：卸载只删程序与快捷方式，用户数据一律保留。 }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    if not UninstallSilent then
      MsgBox('程序文件与快捷方式已删除。' + #13#10#13#10 +
        '用户数据（后台进程保护名单、下载任务、游戏模式恢复快照）仍保留在：' + #13#10 +
        ExpandConstant('{localappdata}\GameOptimizer') + #13#10#13#10 +
        '如需彻底清除，请手动删除上面这个文件夹。',
        mbInformation, MB_OK);
  end;
end;
