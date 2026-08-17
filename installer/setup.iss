; Inno Setup 脚本：生成 Setup.exe（一键安装 + 控制面板卸载）
;
; 前置：
;   1. 安装 Inno Setup 6（https://jrsoftware.org/isinfo.php）
;   2. 运行 installer\build-msix.ps1 生成签名后的 .appx 与证书
;   3. 运行 KeyDisplay.Companion\build.ps1 生成伴生进程 EXE
;   4. 在 Inno 编译器中打开本文件并编译（或 iscc setup.iss）
;
; 安装流程：复制伴生进程与证书到 {app}，然后调用 install-msix.ps1
; （信任证书 + Add-AppxPackage + 协议注册 + config.json）。
; 卸载流程：内联 PowerShell 移除 UWP 包与证书，Inno 负责删除文件与注册表。

#define MyAppName "按键显示"
; 版本号与 VERSION.md 保持一致（当前 0.3.1 beta），发布时同步修改
#define MyAppVersion "0.3.1"
#define MyAppPublisher "KeyDisplay"
#define MyAppExeName "KeyDisplayCompanion.exe"

[Setup]
AppId={{3C1A7E2D-9B4F-4C6A-B5D2-8E0F1A3D6C21}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
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

[Files]
Source: "..\KeyDisplay.Companion\dist\KeyDisplayCompanion.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\cert\KeyDisplay.cer"; DestDir: "{app}\cert"; Flags: ignoreversion
Source: "..\dist\KeyDisplay.Install\*.msix"; DestDir: "{app}\appx"; Flags: ignoreversion
Source: "install-msix.ps1"; DestDir: "{app}"; Flags: ignoreversion

[Registry]
Root: HKCU; Subkey: "Software\Classes\keydisplay"; ValueType: string; ValueName: ""; ValueData: "URL:KeyDisplay"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\keydisplay"; ValueType: string; ValueName: "URL Protocol"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\keydisplay\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\keydisplay\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"",0"; Flags: uninsdeletekey

[Run]
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\install-msix.ps1"" -AppxPath ""{app}\appx\KeyDisplay.Widget_*.msix"" -CertPath ""{app}\cert\KeyDisplay.cer"" -CompanionExe ""{app}\{#MyAppExeName}"""; Flags: runhidden waituntilterminated; StatusMsg: "正在安装 Game Bar 小组件..."
; 安装/更新完成后启动伴生进程（mutex 保证单实例），widget 无需重开即可连接
Filename: "{app}\{#MyAppExeName}"; Flags: runhidden nowait; StatusMsg: "正在启动数据采集服务..."

[UninstallRun]
; 注意：Inno 在 [UninstallRun] 之前会先删除文件，因此这里用内联命令（不依赖已删除的脚本）。
Filename: "powershell.exe"; Parameters: "-NoProfile -Command ""Get-AppxPackage -Name 'KeyDisplay.Widget' | Remove-AppxPackage -ErrorAction SilentlyContinue; Get-ChildItem 'Cert:\LocalMachine\TrustedPeople' -ErrorAction SilentlyContinue | Where-Object {{ $_.Subject -like '*CN=KeyDisplay*' }} | Remove-Item -ErrorAction SilentlyContinue"""; Flags: runhidden

[UninstallDelete]
Type: files; Name: "{app}\config.json"