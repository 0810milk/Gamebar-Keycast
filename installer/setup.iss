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
; 版本号与 VERSION.md 保持一致（当前 1.1.1 中秋版本），发布时同步修改
#define MyAppVersion "1.2.1"
#define MyAppPublisher "KeyDisplay"
#define MyAppEdition "中秋版本"
#define MyAppExeName "KeyDisplayCompanion.exe"

[Setup]
AppId={{3C1A7E2D-9B4F-4C6A-B5D2-8E0F1A3D6C21}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion} {#MyAppEdition}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\KeyDisplay
; 0.6.0 事故修复：禁用"记住上次安装路径"，强制使用默认目录，避免装进遗留的 %TEMP% 目录
UsePreviousAppDir=no
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
; 仅允许管理员运行：安装程序一律要求提升（覆盖系统/Program Files 与计划任务）
PrivilegesRequired=admin
; 显式禁止"仅为我安装"这类降权覆盖，保证只有管理员模式
PrivilegesRequiredOverridesAllowed=
; ---- 品牌资源（用户 logo）----
SetupIconFile=keydisplay.ico
WizardImageFile=wizard-large.bmp
WizardSmallImageFile=wizard-small.bmp
WizardImageStretch=no
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
SetupWindowTitle=安装 - {#MyAppName} {#MyAppVersion} {#MyAppEdition}
WelcomeLabel1=欢迎使用 {#MyAppName} {#MyAppVersion} {#MyAppEdition} 安装向导
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
FinishedLabel=已成功安装 {#MyAppName} {#MyAppVersion} {#MyAppEdition}。%n%n按 Win+G 打开 Game Bar，在小组件中选择「按键显示」即可使用。%n%n中秋快乐，愿键影如月，常伴左右。
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
Source: "..\KeyDisplay.Input\KeyDisplayInput.exe"; DestDir: "{app}"; Flags: ignoreversion restartreplace
Source: "..\cert\KeyDisplay.cer"; DestDir: "{app}\cert"; Flags: ignoreversion
Source: "..\dist\KeyDisplay.Install\*.msix"; DestDir: "{app}\appx"; Flags: ignoreversion
Source: "install-msix.ps1"; DestDir: "{app}"; Flags: ignoreversion
; 0.9.3：看门狗脚本（计划任务每 5 分钟调用：伴生进程不在就拉起）
Source: "watchdog.vbs"; DestDir: "{app}"; Flags: ignoreversion

; 0.7.0 打包事故修复：每次安装前清空 {app}\appx 下旧 msix，保证安装目录始终只有本次分发的
; 一个 msix（历史事故：appx 目录累积多个版本 → install-msix.ps1 多文件守卫抛错 → Setup 静默失败）
[InstallDelete]
Type: files; Name: "{app}\appx\*.msix"

[Registry]
Root: HKCU; Subkey: "Software\Classes\keydisplay"; ValueType: string; ValueName: ""; ValueData: "URL:KeyDisplay"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\keydisplay"; ValueType: string; ValueName: "URL Protocol"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\keydisplay\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\keydisplay\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"",0"; Flags: uninsdeletekey
; 0.8.3：开机自启伴生进程（Game Bar 沙箱内协议拉起不可靠；低层钩子无需管理员，普通权限常驻即可）
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "KeyDisplayCompanion"; ValueData: """{app}\{#MyAppExeName}"""; Flags: uninsdeletevalue
; 0.9.3：显式把该自启项标记为「已启用」——系统/用户在任务管理器里关掉后会写 03（禁用），
; 导致开机不再自启且无任何提示。这里写 02（启用）+ 全零时间戳，安装即恢复自启能力。
; 实现见下方 [Registry] 的 binary 行（Inno 的 [Code] 无对应的二进制写 API）。
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run"; ValueType: binary; ValueName: "KeyDisplayCompanion"; ValueData: "02 00 00 00 00 00 00 00 00 00 00 00"
; 0.9.3：系统级进程优先级（IFEO PerfOptions，微软文档化用法）——早于进程内设置生效，
; 保证推送线程获得及时调度（CpuPriorityClass 6=Above Normal，IoPriority 3=High）
Root: HKLM; Subkey: "SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\{#MyAppExeName}\PerfOptions"; ValueType: dword; ValueName: "CpuPriorityClass"; ValueData: "6"; Flags: uninsdeletekey
Root: HKLM; Subkey: "SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\{#MyAppExeName}\PerfOptions"; ValueType: dword; ValueName: "IoPriority"; ValueData: "3"

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

{ 0.9.3 稳定性：把 Run 自启项显式标记为「已启用」。
  系统或用户在任务管理器「启动应用」里关闭该条目时会写入 03(禁用)，此后开机不再自启
  且没有任何提示。启用状态由 [Registry] 段的 binary 值写入（02 + 全零时间戳）。
  注意：该值仅作双保险，主力常驻机制是计划任务（登录自启 + 每 5 分钟看门狗）。 }

procedure CurStepChanged(CurStep: TSetupStep);
begin
end;

[Run]
; 安装/更新组件（内部会强制移除旧包 + 验证新增）
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\install-msix.ps1"" -AppxPath ""{app}\appx\KeyDisplay.Widget_*.msix"" -CertPath ""{app}\cert\KeyDisplay.cer"" -CompanionExe ""{app}\{#MyAppExeName}"""; Flags: runhidden waituntilterminated; StatusMsg: "正在安装 Game Bar 小组件..."
; 0.8.4：注册登录自启计划任务 —— 比 Run 键可靠：不受任务管理器「启动应用」开关影响，
; 且由计划任务服务托管（用户只需安装，无需任何设置）
Filename: "schtasks.exe"; Parameters: "/Create /F /TN ""KeyDisplayCompanion"" /SC ONLOGON /RL LIMITED /TR ""wscript.exe \""{app}\watchdog.vbs\"""""; Flags: runhidden waituntilterminated; StatusMsg: "正在注册自启任务..."
; 0.9.3：再加一个每 5 分钟的看门狗任务 —— 伴生进程被结束/崩溃后自动拉回（"永久运行"效果），
; 不依赖 widget 沙箱内的拉起能力（宿主会拦截），也不依赖用户任何操作
Filename: "schtasks.exe"; Parameters: "/Create /F /TN ""KeyDisplayCompanionWatchdog"" /SC MINUTE /MO 5 /RL LIMITED /TR ""wscript.exe \""{app}\watchdog.vbs\"""""; Flags: runhidden waituntilterminated; StatusMsg: "正在注册看门狗任务..."
; 安装/更新完成后启动伴生进程（mutex 保证单实例），widget 无需重开即可连接
Filename: "{app}\{#MyAppExeName}"; Flags: runhidden nowait; StatusMsg: "正在启动数据采集服务..."

; 输入采集：先停掉旧版伴生进程（它占着同名管道），再注册登录任务（最高权限）并启动
Filename: "powershell.exe"; Parameters: "-NoProfile -Command ""Stop-Process -Name KeyDisplayCompanion -Force -ErrorAction SilentlyContinue; Start-Sleep -Milliseconds 600"""; Flags: runhidden waituntilterminated
Filename: "schtasks.exe"; Parameters: "/Create /F /TN ""KeyDisplayInput"" /SC ONLOGON /RL HIGHEST /TR ""{app}\KeyDisplayInput.exe"""; Flags: runhidden
Filename: "schtasks.exe"; Parameters: "/Run /TN ""KeyDisplayInput"""; Flags: runhidden
Filename: "{app}\KeyDisplayInput.exe"; Flags: runhidden nowait

[UninstallRun]
Filename: "schtasks.exe"; Parameters: "/Delete /F /TN ""KeyDisplayInput"""; Flags: runhidden
; 0.8.4/0.9.3：卸载先删除自启与看门狗计划任务（避免残留任务指向已删除的 exe）
Filename: "schtasks.exe"; Parameters: "/Delete /F /TN ""KeyDisplayCompanion"""; Flags: runhidden
Filename: "schtasks.exe"; Parameters: "/Delete /F /TN ""KeyDisplayCompanionWatchdog"""; Flags: runhidden
; 卸载：先彻底结束进程（含 widget 与 GameBar 缓存），再移除 UWP 包与证书。
; 用内联命令：Inno 在 [UninstallRun] 之后才删文件，此处先杀进程避免文件锁。
; 注意：内联 PowerShell 的花括号需用 {{ }} 转义（Inno 常量语法）。
Filename: "powershell.exe"; Parameters: "-NoProfile -Command ""Get-Process | Where-Object {{ $_.Name -like 'KeyDisplay*' }} | Stop-Process -Force -ErrorAction SilentlyContinue; Get-Process | Where-Object {{ $_.Name -in @('GameBar','GameBarFTServer') }} | Stop-Process -Force -ErrorAction SilentlyContinue; Start-Sleep -Milliseconds 800; Get-AppxPackage -Name 'KeyDisplay.Widget' | Remove-AppxPackage -ErrorAction SilentlyContinue; Get-ChildItem 'Cert:\LocalMachine\TrustedPeople' -ErrorAction SilentlyContinue | Where-Object {{ $_.Subject -like '*CN=KeyDisplay*' }} | Remove-Item -ErrorAction SilentlyContinue"""; Flags: runhidden

[UninstallDelete]
Type: files; Name: "{app}\config.json"
Type: filesandordirs; Name: "{app}\appx"
