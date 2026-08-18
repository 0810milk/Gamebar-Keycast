# 按键显示 —— Game Bar 键盘鼠标状态小组件

> 当前版本：**0.5.2 beta**，版本登记见 [VERSION.md](VERSION.md)。
> 许可证：MIT（版权 2026 恐龙milk），见 [LICENSE](LICENSE)。

在 Windows Game Bar（`Win+G`）中实时显示键盘与鼠标操作状态的小组件（桌面叠加显示）：

- **键盘**：`Q/W/E/R`、`A/S/D/F`、`Shift`、`Ctrl`、`Alt`、`空格`（0.4.0 起支持自定义添加任意按键，87 配列键位图）
- **鼠标**：移动垫（光标实时定位）+ `左 / 中 / 右 / 侧下 / 侧上` 五个按键
- 按下反色、松开恢复，无动画
- 暗色 / 亮色半透明主题（右下角胶囊按钮切换）
- 高帧率：伴生进程推送频率可配置（默认 240Hz），UI 渲染跟随显示器刷新率
- `Setup.exe` 一键安装，支持控制面板卸载

## 架构总览

```
┌──────────────────────────┐       命名管道             ┌───────────────────────────┐
│  KeyDisplayCompanion     │  \\.\pipe\KeyDisplayState │  KeyDisplay.Widget        │
│  (Python + PyInstaller)  │ ←── 68B/帧, 240Hz ──────► │  (UWP C# Game Bar 小组件) │
│  · WH_KEYBOARD_LL 钩子    │                           │  · CreateFileW + FileStream│
│  · WH_MOUSE_LL 钩子       │                           │  · 跟随刷新率渲染          │
│  · RAWINPUT 游戏内累计    │                           │  · 右下角胶囊按钮切主题    │
│  · GetAsyncKeyState 兜底  │                           │  · 自定义控件/长按移动/删除 │
└──────────────────────────┘                           └───────────────────────────┘
        ▲ keydisplay://start（协议唤起）
        │
  UWP 小组件打开时自动拉起
```

**两部分职责与其配合**：
- **伴生进程**（Python，桌面进程）：通过全局钩子 + 原始输入采集键盘/鼠标状态，作为命名管道服务器以 240Hz 推送给小组件。无窗口、单实例。
- **小组件**（C#/UWP，Game Bar 沙箱内）：作为 Game Bar 扩展（`microsoft.gameBarUIExtension`）渲染状态界面，从管道读取快照并按显示器刷新率绘制。

两侧不共享进程边界，仅通过命名管道通信。完整细节（管道路径、包 SID 放行、快照协议字节布局）见 [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)。

### 快照协议（关键演进）

| 版本 | 快照大小 | 说明 |
|---|---|---|
| v1 | 36 字节 | 初始：12 键 + 5 鼠标键位掩码 + 鼠标坐标 + 虚拟屏幕 |
| v2 | 36 字节 | 增加序号 seq、虚屏采集合流（`<4sBHBiiiiiiI`） |
| **v3**（当前） | **68 字节** | 追加 256 位 VK 位图（`<4sBHBiiiiiiI32s`），驱动**任意自定义键**按下反色 —— 因 UWP 沙箱禁止 `GetAsyncKeyState`，改由伴生进程采集全键位图下发 |

> v3 引入原因：0.4.0 加入「自定义控件」后，需要支持任意按键（不只固定 12 键）的反色，故快照扩充为 68 字节、额外携带 256 位虚拟键位图。Python 侧 `state.py`，C# 侧 `InputStateReader.cs`。

## 目录结构

| 目录 / 文件 | 说明 |
|---|---|
| `KeyDisplay.Companion/` | Python 伴生进程（输入采集 + 管道服务 + 单元测试） |
| `KeyDisplay.Widget/` | UWP C# 小组件工程（Visual Studio 构建） |
| `installer/` | 证书、MSIX 构建脚本、安装/卸载脚本、Inno Setup 脚本 |
| `tools/` | `gen_assets.py`（生成 UWP 资源）、`preview.py`（tkinter 开发预览） |
| `docs/` | 架构 / 构建 / 安装说明、交接文档、历史问题记录 |
| `release/` | **每个版本的安装包归档**（`release/<版本>/KeyDisplaySetup.exe`，随源码提交，一一对应） |

## 版本更新日志

完整技术细节见 [VERSION.md](VERSION.md)。下表为全量历史：

| 版本 | 日期 | 更新内容 |
|---|---|---|
| **0.5.2 beta** | 2026-08-18 | 设置按钮→正常按钮「设置」+ 鼠标垫显示/隐藏开关（持久化+隐藏≠删除）+ 删触摸板模板 + 自带键可删除（重置兜底）（UWP 包 1.2.2.0） |
| **0.5.1 beta** | 2026-08-18 | 移动挤压修复：移动改用 RenderTransform（不挤压兄弟按键）+ 持久化 transform + 缩放归一（UWP 包 1.2.1.0） |
| **0.5.0 beta** | 2026-08-18 | 全局参考线吸附（隔空对齐 + 距离分级参考线）+ 鼠标垫长按移动/等比缩放 + 触摸板控件 + 文字调整（UWP 包 1.2.0.0） |
| **0.4.1 beta** | 2026-08-17 | 安装器紧急修复：升级残留 bug + 防强制重启 + 中文化 + `.ps1` 编码根治（UWP 包 1.1.0.0） |
| **0.4.0 beta** | 2026-08-17 | 自定义控件（87 配列按键添加器）：设置面板精简 + 长按移动 + 右键删除 + 折叠键盘选择器 + 协议 v3 + 窗口 +1/3 + 重置恢复出厂 + 锁定时不可移动 |
| **0.3.1 beta** | 2026-08-17 | 按键边缘悬停光标反馈（Size 光标）+ 同键同模式去重修复 + CaptureLost 光标兜底 |
| **0.3.0 beta** | 2026-08-17 | 自定义布局（拖拽缩放 + 锁定开关 + 持久化）、设置新增「重置按键布局」、修复拖拽释放闪退 |
| **0.2.0 beta** | 2026-08-17 | 新增设置子菜单 + 测试按钮（点击反馈）+ 日志链路 |
| **0.1.0 beta** | 2026-08-16 | 起步阶段小版本收敛（功能稳定） |
| 0.0.4 beta | 2026-08-16 | 修复累计浮点导致泵崩溃（光标/按键冻结） |
| 0.0.3 beta | 2026-08-16 | 游戏内校准缩放 + 屏幕钳制 |
| 0.0.2 beta | 2026-08-16 | 鼠标点平滑跟手（指数插值） |
| 0.0.1 beta | 2026-08-16 | 首个统一版本号 |

## 快速开始

### 安装（普通用户）
直接下载对应版本的 `release/<版本>/KeyDisplaySetup.exe`，右键「以管理员身份运行」一键安装，随后 `Win+G` 打开 Game Bar 固定「按键显示」小组件即可。

### 开发 / 构建（开发者）
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

## 测试

```powershell
cd KeyDisplay.Companion
python -m unittest test_units -v   # 伴生进程单元测试（26 个用例）
```

## 已知环境限制

- 构建需 Visual Studio BuildTools（含 UWP 工作负载）与 Windows SDK。
- 自动化注入的模拟键盘输入（SendInput）不会触发全局钩子（系统注入丢弃），需物理按键实测键盘链路。
- 小组件的指针 hover 事件（PointerMoved）无法用注入式鼠标移动触发，需真人真实鼠标悬停验证。

## 致谢 / 说明

本项目由 AI 辅助开发（多 Agent 协作），代码与文档均为源码可见。参与维护请先读 [docs/HANDOFF.md](docs/HANDOFF.md)；历史问题的调查与修复记录见 [docs/ISSUE-PINNED-CHROME.md](docs/ISSUE-PINNED-CHROME.md)。
