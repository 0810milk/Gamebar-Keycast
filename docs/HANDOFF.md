# 交接文档（给下一个 Agent / 新维护者）

> 目标：让你在不熟悉本仓库的情况下，快速理解项目、在本机复现问题、继续开发或移植到新机器。
> 读完后按顺序看 `docs/ARCHITECTURE.md`（架构细节）、`docs/BUILD.md`（构建）、`docs/INSTALL.md`（安装），
> 以及 `docs/ISSUE-PINNED-CHROME.md`（当前未解决的"固定后只显示按键"问题，重点）。

---

## 1. 项目是什么

Windows Game Bar（`Win+G`）里的键盘/鼠标状态显示小组件：

- 桌面侧一个 **Python 伴生进程**（`KeyDisplayCompanion.exe`）用全局钩子采集键盘/鼠标，
  通过**命名管道**把 36 字节输入快照以 60Hz 推送给小组件。
- **UWP C# 小组件**（`KeyDisplay.Widget`）在 Game Bar 沙箱内渲染：12 个按键 + 5 个鼠标键 + 80×80 鼠标垫。
- 支持暗/亮主题（默认暗色），右键菜单可切换；底部还有「黑/白」两个主题切换按钮。
- `Setup.exe`（Inno Setup）一键安装 + 控制面板卸载。

```
KeyDisplayCompanion（Python，桌面，无窗口）
  hooks.py        WH_KEYBOARD_LL / WH_MOUSE_LL + RAWINPUT（全屏游戏）
  state.py        InputState：键盘12位 + 鼠标5位 + 屏幕坐标
  pipe_server.py  60Hz 组帧推送 36B，SDDL 放行 UWP 包 SID
        │  \\.\pipe\KeyDisplayState
        ▼
KeyDisplay.Widget（UWP C#，Game Bar 沙箱）
  App.xaml.cs           协议激活 ms-gamebarwidget → XboxGameBarWidget
  Widget1.xaml(.cs)     UI + 30fps 刷新 + 主题 + 固定状态检测
  InputStateReader.cs   CreateFileW + FileStream 读 36B 帧，断线重连
```

**通信协议（v2，36 字节）**：`<4sBHBiiiiiiI` = MAGIC"KDSP" + ver + keys(u16) + mouse(u8)
+ mouseX/Y + vsX/vsY/vsW/vsH(虚拟屏幕，i32) + seq(u32)。详见 `docs/ARCHITECTURE.md`。

---

## 2. 目录结构

| 路径 | 说明 |
|---|---|
| `KeyDisplay.Companion/` | Python 伴生进程（`companion.py` 入口，`hooks.py`/`state.py`/`pipe_server.py`），`build.ps1` 打 PyInstaller EXE，`test_units.py` 单测 |
| `KeyDisplay.Widget/` | UWP C# 工程（sln/csproj）。`App.xaml.cs` 激活与实例管理；`Widget1.*` 主 UI；`InputStateReader.cs` 管道读取；`NativeMethods.cs` P/Invoke |
| `installer/` | `build-msix.ps1`（构建+签名 MSIX）、`install.ps1`/`install-msix.ps1`（安装注册）、`make-cert.ps1`（自签名证书）、`setup.iss`（Inno）、`uninstall.ps1` |
| `tools/` | `gen_assets.py`（批量生成 47 个 UWP 资源 PNG）、`preview.py`（tkinter 预览） |
| `cert/` | `KeyDisplay.cer`（公钥）/ `KeyDisplay.pfx`（私钥，密码 `KeyDisplayDev!`） |
| `dist/` | 构建产物（gitignore）：`KeyDisplay.Install\*.msix`（签名）、`KeyDisplay.Setup\KeyDisplaySetup.exe` |
| `docs/` | ARCHITECTURE / BUILD / INSTALL / HANDOFF（本文）/ ISSUE-PINNED-CHROME |

---

## 3. 本机环境（关键事实，Agent 可直接使用）

- 工作目录：`C:\恐龙\项目\Game Bar 按键显示组件`；**git 仅本地备份，无远程**。
- 已安装的包：`KeyDisplay.Widget 1.0.0.0 x64`，PFN=`KeyDisplay.Widget_hdjf4fqmxxv8g`，
  AUMID=`KeyDisplay.Widget_hdjf4fqmxxv8g!App`。
- 小组件诊断日志（必看）：`C:\Users\恐龙milk\AppData\Local\Packages\KeyDisplay.Widget_hdjf4fqmxxv8g\LocalState\diag.txt`
  （内含连接、激活、`GameBarDisplayMode`/`_docked` 变化、`WindowBounds` 等）。
- 安装目录（用户自选的测试目录）：`用户测试\KeyDisplay\`（含 `KeyDisplayCompanion.exe`、`config.json`、`appx\*.msix`、`cert\*.cer`）。
- 运行中的伴生进程即该目录下 EXE（已含 RAWINPUT 全屏修复）。

### 工具链（命令行构建，均已在本机验证）

```powershell
# MSBuild（vswhere 在这台 BuildTools 机器上找不到 MSBuild，必须用直连路径 + VisualStudioVersion）
$MSB = 'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe'

# 签名
$SIGN = 'C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe'

# Inno Setup 6
$ISCC = 'C:\Users\恐龙milk\AppData\Local\Programs\Inno Setup 6\ISCC.exe'
```

### 构建 UWP 组件（改动 C# 后必做）

```powershell
& $MSB '.\KeyDisplay.Widget\KeyDisplay.Widget.sln' /t:Restore,Build `
  /p:Configuration=Release /p:Platform=x64 /p:AppxBundle=Never `
  /p:UapAppxPackageBuildMode=SideloadOnly /p:AppxPackageSigningEnabled=false `
  /p:VisualStudioVersion=17.0 /v:m
# 产物：KeyDisplay.Widget\AppPackages\KeyDisplay.Widget_1.0.0.0_x64_Test\KeyDisplay.Widget_1.0.0.0_x64.msix
```

### 签名 + 重装组件（同版本无法直接 Add-AppxPackage，必须先移除）

```powershell
$src='.\KeyDisplay.Widget\AppPackages\KeyDisplay.Widget_1.0.0.0_x64_Test\KeyDisplay.Widget_1.0.0.0_x64.msix'
$dst='.\dist\KeyDisplay.Install\KeyDisplay.Widget_1.0.0.0_x64.msix'
Copy-Item $src $dst -Force
& $SIGN sign /fd SHA256 /f '.\cert\KeyDisplay.pfx' /p 'KeyDisplayDev!' $dst
Get-Process | Where-Object Name -like 'KeyDisplay.Widget*' | Stop-Process -Force   # 先杀进程
Get-AppxPackage -Name 'KeyDisplay.Widget' | Remove-AppxPackage
Add-AppxPackage -Path $dst
# 清空诊断日志，方便下次复现后回读：
Remove-Item "$env:LOCALAPPDATA\Packages\KeyDisplay.Widget_hdjf4fqmxxv8g\LocalState\diag.txt" -Force
```

### 启动小组件（独立启动，不进 Game Bar，走 MainPage 不经过 Widget1）

```powershell
explorer.exe "shell:AppsFolder\KeyDisplay.Widget_hdjf4fqmxxv8g!App"
```

### 重建 Setup.exe（交付前最后一步）

```powershell
& $ISCC '.\installer\setup.iss'
# 产物：dist\KeyDisplay.Setup\KeyDisplaySetup.exe
```

### 构建伴生进程 EXE（改 Python 后）

```powershell
powershell -ExecutionPolicy Bypass -File '.\KeyDisplay.Companion\build.ps1'
# 产物：KeyDisplay.Companion\dist\KeyDisplayCompanion.exe
# 替换已安装副本：Copy-Item 到 用户测试\KeyDisplay\ 或 %ProgramFiles%\KeyDisplay\
```

### 单元测试

```powershell
cd KeyDisplay.Companion; python -m unittest test_units -v
```

---

## 4. git 历史（提交顺序）

```
c1cf6ac 初版：伴生进程 + UWP 小组件 + 安装器
001b830 协议 v2（36B 含虚拟屏幕），修复 UWP 资产尺寸与构建链路
c629f5d 修复 install-msix.ps1 无 UTF-8 BOM 导致 PS5.1 解析失败
266b3d1 修复 UWP 连不上管道：DACL 增加 ALL APPLICATION PACKAGES (S-1-15-2-1)
6bda7da 修复独占全屏鼠标坐标冻结：RAWINPUT 累计增量
e702e68 组件底部增加黑白主题切换按钮
36f1a47 固定后只显示按键：监听 PinnedChanged 隐藏面板/主题按钮/状态字
```

> 说明：`36f1a47` 后 `Widget1` 又经多轮改动（改用 `GameBarDisplayMode` + 轮询 + 日志），
> 当前工作区代码**领先于最后一次提交**。当前问题见 `docs/ISSUE-PINNED-CHROME.md`。

---

## 5. 易踩坑清单

1. **UTF-8 BOM**：`installer\*.ps1` 含中文注释/字符串，必须带 UTF-8 BOM，
   否则 PowerShell 5.1 按 ANSI 解析会吞引号导致语法错误（见 `c629f5d`）。
   用「保存为 UTF-8 with BOM」或 `Set-Content -Encoding UTF8` 写。
2. **证书 Publisher 固定**：包身份 `Publisher=CN=KeyDisplay, O=KeyDisplay, C=CN`，
   改动需同步 `Package.appxmanifest`、`make-cert.ps1`、安装脚本的主题匹配。
3. **同版本 MSIX 无法重装**（0x80073CFB）：先 `Remove-AppxPackage` 再 `Add-AppxPackage`。
4. **Game Bar 小组件沙箱限制**：UWP 内 `System.IO.Pipes` 不可用、不允许 `GetSystemMetrics`，
   所以用 `CreateFileW`+`FileStream` 读管道，虚拟屏幕由桌面侧随帧下发。
5. **模拟输入不触发钩子**：SendInput 注入的按键不产生 LL 钩子事件，物理按键才正常。
6. `build-msix.ps1` 里 vswhere 在本机定位不到 MSBuild，改用手工 MSBuild 命令（见上）。

---

## 6. 移植到新机器 checklist

1. 整仓库复制（git 本地仓库即可），路径可任意，但注意中文路径在 PowerShell 5.1 的编码。
2. 装依赖：Python 3.12+（`pip install pillow pyinstaller`）、
   Visual Studio 2022（勾选「通用 Windows 平台开发」）、Windows SDK（含 SignTool）、Inno Setup 6。
3. 依次：`python tools\gen_assets.py` → `KeyDisplay.Companion\build.ps1` →
   构建 MSIX（`build-msix.ps1` 或手工 MSBuild）→ 签名 → `install.ps1`（或 Inno 出 Setup.exe）。
4. 目标机器需先开侧载（设置→隐私和安全性→开发者选项→开发人员模式 或 旁加载）。
5. 安装后 `Win+G` 打开 Game Bar，小组件里固定/打开「按键显示」（图标名 KeyDisplayMain）。
6. 用 `diag.txt` 定位问题（路径随 PFN，见上文）。

---

## 7. 下一步（当前状态）

「固定后只显示按键」「主题切换崩溃」「鼠标点双位置振荡」均已修复（见 `docs/ISSUE-PINNED-CHROME.md`
第 9–13 节）；2026-08-16 另放开帧率（第 14 节，推送 fps 配置 + 渲染跟随显示器刷新率）并让
鼠标垫跟随屏幕纵横比（第 15 节，直接用协议里的 vs_w/vs_h 保比例缩放）。
**待用户实测**：固定后退出只留按键、主题切换不崩、游戏中鼠标点平滑跟随、高刷流畅、
鼠标垫比例随屏幕/显示器布局变化。确认后按需调整 `fps`（config.json），重建 `Setup.exe` 并 commit；
新 MSIX 已重装、diag.txt 已清空。