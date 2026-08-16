# 按键显示 —— Game Bar 键盘鼠标状态小组件

> 当前版本：**0.0.3 beta**，版本登记见 [VERSION.md](VERSION.md)。

在 Windows Game Bar（`Win+G`）中实时显示键盘与鼠标操作状态的小组件：

- 键盘：`Q/W/E/R`、`A/S/D/F`、`Shift`、`Ctrl`、`Alt`、`空格`
- 鼠标：移动垫（光标实时定位）+ `左 / 中 / 右 / 侧下 / 侧上` 五个按键
- 按下反色、松开恢复，无动画
- 暗色 / 亮色半透明主题（右下角胶囊按钮切换）
- 高帧率：伴生进程推送频率可配置（默认 240Hz），UI 渲染跟随显示器刷新率
- `Setup.exe` 一键安装，支持控制面板卸载

## 架构总览

```
┌─────────────────────────┐       命名管道        ┌──────────────────────────┐
│  KeyDisplayCompanion    │  \\.\pipe\KeyDisplay  │  KeyDisplay.Widget       │
│  (Python + PyInstaller) │ ←── 36B/帧, 240Hz ──► │  (UWP C# Game Bar 小组件) │
│  · WH_KEYBOARD_LL 钩子   │                      │  · CreateFileW + FileStream│
│  · WH_MOUSE_LL 钩子      │                      │  · 跟随刷新率渲染         │
│  · GetAsyncKeyState 兜底 │                      │  · 右下角胶囊按钮切主题    │
└─────────────────────────┘                      └──────────────────────────┘
        ▲ keydisplay://start（协议唤起）
        │
  UWP 小组件打开时自动拉起
```

伴生进程负责采集输入（全局钩子），小组件负责渲染（Game Bar 沙箱内）。
两侧不共享进程边界，通过命名管道通信；管道路径、包 SID 放行、快照协议见
[ARCHITECTURE.md](docs/ARCHITECTURE.md)。

## 目录结构

| 目录 / 文件 | 说明 |
|---|---|
| `KeyDisplay.Companion/` | Python 伴生进程（采集输入 + 管道服务） |
| `KeyDisplay.Widget/` | UWP C# 小组件工程（VS 构建） |
| `installer/` | 证书、MSIX 构建、安装/卸载脚本、Inno Setup 脚本 |
| `tools/` | `gen_assets.py`（生成 UWP 资源）、`preview.py`（tkinter 开发预览） |
| `docs/` | ARCHITECTURE / BUILD / INSTALL 说明，及交接文档 `HANDOFF.md` 与未解决问题 `ISSUE-PINNED-CHROME.md` |
| `dist/` | 构建产物（EXE、APPX、Setup.exe，均为 .gitignore 忽略） |

## 快速开始

```powershell
# 1) 开发预览（无需 Game Bar，直接看布局与反色效果）
python tools\preview.py

# 2) 构建伴生进程 EXE
cd KeyDisplay.Companion
powershell -ExecutionPolicy Bypass -File build.ps1

# 3) 构建并签名 UWP 小组件 APPX（需要 VS + Windows SDK）
cd ..\installer
.\build-msix.ps1

# 4) 一键安装
.\install.ps1 -Appx ..\dist\KeyDisplay.Install\KeyDisplay.Widget_*.appx
# 或编译 installer\setup.iss 生成 Setup.exe 后运行
```

完整流程见 [docs/BUILD.md](docs/BUILD.md) 与 [docs/INSTALL.md](docs/INSTALL.md)。

> 接手维护/移植请先读 [docs/HANDOFF.md](docs/HANDOFF.md)（含本机工具链、构建命令、易踩坑）；
> 「固定后只显示按键」问题的调查与修复记录见 [docs/ISSUE-PINNED-CHROME.md](docs/ISSUE-PINNED-CHROME.md)。

## 测试

```powershell
cd KeyDisplay.Companion
python -m unittest test_units -v
```

## 已知环境限制

- 本仓库开发环境无法编译 UWP/MSIX（无 VS/.NET SDK），`KeyDisplay.Widget` 为可交付源码，
  需在装有 Visual Studio（含 UWP 工作负载）与 Windows SDK 的机器上构建。
- 自动化注入的模拟键盘输入（SendInput）不会触发全局钩子（系统注入丢弃），
  需物理按键实测键盘链路；鼠标链路已用真实输入验证通过。
