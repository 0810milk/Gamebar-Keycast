; Inno Setup 脚本：生成 Setup.exe（一键安装 + 控制面板卸载）
;
; 前置：
;   1. 安装 Inno Setup 6（https://jrsoftware.org/isinfo.php）
;   2. 运行 installer\build-msix.ps1 生成签名后的 .appx 与证书
;   3. 运行 KeyDisplay.Companion\build.ps1 生成伴生进程 EXE
;   4. 在 Inno 编译器中打开本文件并编译（或 iscc setup.iss）
;
; 安装流程：先结束残留进程 → 复制伴生进程与证书 → 调用 install-msix.ps1
; （信任证书 + 强制移除旧 APPX + Add-AppxPackage + 协议注册 + config.json）。
; 卸载流程：先结束伴生进程与小组件进程 → 移除 UWP 包与证书 → Inno 删除文件与注册表。

#define MyAppName "按键显示"
; 版本号与 VERSION.md 保持一致（当前 0.5.0 beta），发布时同步修改
#define MyAppVersion "0.5.0"
#define MyAppPublisher "KeyDisplay"
#define MyAppExeName "KeyDisplayCompanion.exe"

[Setup]
AppId={{3C1A7E2D-9B4F-4C6A-B5D2-8E0F1A3D6C21}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\KeyDisplay
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
OutputDir=..\dist\KeyDisplay.Setup
OutputBaseFilename=KeyDisplaySetup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName},0
; 关键：安装/卸载时如检测到应用文件被占用，先尝试关闭应用，而非直接要求重启
CloseApplications=yes
RestartApplications=no
; 首次可完成后再重启确认，不强制
AlwaysRestart=no

; ---- 中文界面（零外部依赖：覆盖内置英文文案）----
[Messages]
SetupAppTitle=安装 {#MyAppName}
SetupWindowTitle=安装 - {#MyAppName} {#MyAppVersion}
WelcomeLabel1=欢迎使用 {#MyAppName} {#MyAppVersion} 安装向导
WelcomeLabel2=本向导将引导您安装 {#MyAppName}（Windows Game Bar 键盘鼠标状态显示小组件）。%n%n建议先关闭已打开的 Game Bar（Win+G）再继续安装。
WizardSelectDir=选择安装位置
SelectDirLabel3=安装程序将把 {#MyAppName} 安装到以下文件夹。
SelectDirBrowseLabel=要继续安装，请单击"下一步"。如果您想选择其他文件夹，请单击"浏览"。
WizardReady=准备安装
ReadyLabel1=安装程序已准备好将 {#MyAppName} 安装到您的计算机。
ReadyLabel2a=请单击"安装"以开始安装；如果想回顾或更改任何设置，请单击"后退"。
WizardInstalling=正在安装
InstallingLabel=正在安装 {#MyAppName}，请稍候...
WizardFinished=正在完成 {#MyAppName} 安装向导
FinishedHeadingLabel=正在完成 {#MyAppName} 安装向导
FinishedLabel=已成功安装 {#MyAppName}。%n%n按 Win+G 打开 Game Bar，在小组件中选择「按键显示」即可使用。
ButtonNext=下一步 >
ButtonBack=< 上一步
ButtonInstall=安装
ButtonFinish=完成
ButtonCancel=取消
ButtonYes=是(&Y)
ButtonNo=否(&N)
DiskSpaceMBLabel=至少需要 [mb] MB 的可用磁盘空间。
ClickNextToContinue=单击"下一步"继续安装。
ClickInstall=单击"安装"开始安装。

[Files]
Source: "..\KeyDisplay.Companion\dist\KeyDisplayCompanion.exe"; DestDir: "{app}"; Flags: ignoreversion restartreplace
Source: "..\cert\KeyDisplay.cer"; DestDir: "{app}\cert"; Flags: ignoreversion
Source: "..\dist\KeyDisplay.Install\*.msix"; DestDir: "{app}\appx"; Flags: ignoreversion
Source: "install-msix.ps1"; DestDir: "{app}"; Flags: ignoreversion

[Registry]
Root: HKCU; Subkey: "Software\Classes\keydisplay"; ValueType: string; ValueName: ""; ValueData: "URL:KeyDisplay"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\keydisplay"; ValueType: string; ValueName: "URL Protocol"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\keydisplay\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\keydisplay\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"",0"; Flags: uninsdeletekey

; ---- 安装前：结束残留进程（伴生进程 + Game Bar 宿主），避免文件占用导致"要求重启" ----
[Code]
procedure KillProcessByName(AName: String);
var
  C: Integer;
begin
  Exec('taskkill.exe', '/F /IM ' + AName + ' /T', '', SW_HIDE, ewWaitUntilTerminated, C);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  { 安装前：结束可能占用文件的伴生进程与 widget 进程，避免重启 }
  KillProcessByName('KeyDisplayCompanion.exe');
  KillProcessByName('KeyDisplay.Widget.exe');
  KillProcessByName('GameBar.exe');
  KillProcessByName('GameBarFTServer.exe');
  NeedsRestart := False;
  Result := '';
end;

[Run]
; 安装/更新组件（内部会强制移除旧包 + 验证新增）
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\install-msix.ps1"" -AppxPath ""{app}\appx\KeyDisplay.Widget_*.msix"" -CertPath ""{app}\cert\KeyDisplay.cer"" -CompanionExe ""{app}\{#MyAppExeName}"""; Flags: runhidden waituntilterminated; StatusMsg: "正在安装 Game Bar 小组件..."
; 安装/更新完成后启动伴生进程（mutex 保证单实例），widget 无需重开即可连接
Filename: "{app}\{#MyAppExeName}"; Flags: runhidden nowait; StatusMsg: "正在启动数据采集服务..."

[UninstallRun]
; 卸载：先彻底结束进程（含 widget 与 GameBar 缓存），再移除 UWP 包与证书。
; 用内联命令：Inno 在 [UninstallRun] 之后才删文件，此处先杀进程避免文件锁。
; 注意：内联 PowerShell 的花括号需用 {{ }} 转义（Inno 常量语法）。
Filename: "powershell.exe"; Parameters: "-NoProfile -Command ""Get-Process | Where-Object {{ $_.Name -like 'KeyDisplay*' }} | Stop-Process -Force -ErrorAction SilentlyContinue; Get-Process | Where-Object {{ $_.Name -in @('GameBar','GameBarFTServer') }} | Stop-Process -Force -ErrorAction SilentlyContinue; Start-Sleep -Milliseconds 800; Get-AppxPackage -Name 'KeyDisplay.Widget' | Remove-AppxPackage -ErrorAction SilentlyContinue; Get-ChildItem 'Cert:\LocalMachine\TrustedPeople' -ErrorAction SilentlyContinue | Where-Object {{ $_.Subject -like '*CN=KeyDisplay*' }} | Remove-Item -ErrorAction SilentlyContinue"""; Flags: runhidden

[UninstallDelete]
Type: files; Name: "{app}\config.json"
Type: filesandordirs; Name: "{app}\appx"
