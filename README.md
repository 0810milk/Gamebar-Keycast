# 按键显示 —— Game Bar 键盘鼠标状态小组件

> 当前版本：**1.2.4（本地测试版 · 未发布）**（已构建并安装到本机，等待实测；确认后再发布 Release），版本登记见 [VERSION.md](VERSION.md)。
> 许可证：MIT（版权 2026 恐龙milk），见 [LICENSE](LICENSE)。

在 Windows Game Bar（`Win+G`）里实时显示键盘、鼠标（以及正在开发中的手柄）操作状态的小组件，可直接固定在游戏画面上当叠加层。

- **键盘**：`Q/W/E/R`、`A/S/D/F`、`Shift`、`Ctrl`、`Alt`、`空格`；可从 87 配列键位图里添加**任意按键**，支持改名、复制、删除、拖动与四角缩放
- **鼠标**：移动垫（光标真实镜像，带跟手平滑）+ `左 / 中 / 右 / 侧上 / 侧下` 五个按键 + 滚轮上/下
- **手柄**：Xbox/PS/Switch 三种键位风格，ABXY、十字键、肩键、**模拟量扳机**、**摇杆实时位置**（环 + 点）、View/Menu、Guide、可选电量；设置入口在**设置窗口 → 布局页 → 手柄显示**，也可用独立预览窗口先看效果（见下文）
- **六个主题状态**：暗色 / 灰色 / 亮色 / 粉色 / 蓝色 / 自定义，10 个色槽（面板・边框・按键底・文字・按下底・按下字・鼠标垫・鼠标点・强调色・鼠标点按下）逐槽可调
- **布局**：拖动/缩放带隔空对齐吸附与参考线；「整体按键大小」按百分比整体缩放；面板背景可全透明
- **性能**：伴生进程推送频率可配置（60~480Hz，默认 240），UI 渲染跟随显示器刷新率，静止时每帧零重绘

## 架构总览

```
┌────────────────────────────┐      命名管道            ┌──────────────────────────────┐
│  KeyDisplayCompanion       │  \\.\pipe\KeyDisplayState│  KeyDisplay.Widget           │
│  (Python + PyInstaller)    │ ←── 76B/帧, 60~480Hz ──► │  (UWP C# Game Bar 小组件)     │
│  · WH_KEYBOARD_LL 钩子     │  (+16B 手柄尾块，握手后)  │  · 命名管道读取 + 快照解析    │
│  · WH_MOUSE_LL 钩子        │                          │  · 跟随刷新率渲染             │
│  · RAWINPUT 游戏内累计      │                          │  · 自定义键/移动/缩放/吸附     │
│  · GetAsyncKeyState 兜底   │                          │  · 设置窗口（7 个页面）        │
│  · XInput 手柄轮询          │                          │  · 右键菜单（Windows 11 风格）  │
└────────────────────────────┘                          └──────────────────────────────┘
        ▲ keydisplay://start（协议唤起）
        │
  UWP 小组件打开时自动拉起
```

**两部分职责**：

- **伴生进程**（Python，桌面进程）：通过全局钩子 + 原始输入采集键盘/鼠标状态，作为命名管道服务器推送快照给小组件；同时是设置窗口与小组件之间的中继（预设读写、系统选项）。无窗口、单实例、登录自启 + 看门狗。
- **小组件**（C#/UWP，Game Bar 沙箱内）：作为 Game Bar 扩展（`microsoft.gameBarUIExtension`）渲染状态界面，还提供一个官方**设置窗口**（主题 / 参数 / 颜色 / 字体 / 布局 / 预设 / 关于）。

两侧不共享进程边界，仅通过命名管道通信；设置项通过 UWP `LocalSettings` 共享。完整细节（管道路径、包 SID 放行、快照字节布局）见 [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)。

### 快照协议

| 版本 | 大小 | 说明 |
|---|---|---|
| v1 / v2 | 36 字节 | 12 键 + 5 鼠标键位掩码 + 鼠标坐标 + 虚拟屏幕；v2 起带序号 `seq` |
| v3 | 68 字节 | 追加 256 位 VK 位图（`<4sBHBiiiiiiI32s`），驱动**任意自定义键**反色 —— UWP 沙箱禁止 `GetAsyncKeyState`，改由伴生进程采集全键位图下发 |
| **v4**（当前） | **76 字节** | 追加 8 字节输入时间戳（`<4sBHBiiiiiiI32sQ`，`perf_counter_ns` = QPC 纳秒，供小组件计算延迟） |
| v4 + 手柄尾块 | 92 字节 | **加法式扩展、默认不发**：客户端连接后发 `CMD\|PROTO\|92` 握手，服务端才把该连接切到 92 字节（前 76 字节逐字节不变 + 16 字节手柄块：连接掩码/活跃槽/按钮/双扳机/双摇杆/电量/SubType）。版本号刻意保持 `4`，老小组件完全不受影响 |

> 兼容性铁律：改协议前先看 `KeyDisplay.Companion/test_units.py::GamepadTailTests`（逐字节锁定偏移）。0.9.5 曾因字段顺序错位（时间戳与 VK 位图颠倒）导致"滚轮疯狂连点"。

## 目录结构

| 目录 / 文件 | 说明 |
|---|---|
| `KeyDisplay.Companion/` | Python 伴生进程（输入采集 + 管道服务 + XInput 手柄轮询 + 单元测试） |
| `KeyDisplay.Widget/` | UWP C# 小组件工程（Visual Studio 构建），含设置窗口 `SettingsPage.xaml` |
| `installer/` | 证书、MSIX 构建/安装/卸载脚本、Inno Setup 脚本 |
| `tools/` | `gen_assets.py`（UWP 资源）、`preview.py`（键盘布局开发预览）、`pad_preview.py`（**手柄显示预览窗口**） |
| `docs/` | 架构 / 构建 / 安装说明、开发交接文档、历史问题记录 |
| `release/` | **每个版本的安装包归档**（`release/<版本>/KeyDisplaySetup.exe`，随源码提交，一一对应） |

## 版本更新日志

完整技术细节见 [VERSION.md](VERSION.md)（含 0.0.1 起的全部历史）。近期版本：

| 版本 | 日期 | 更新内容 |
|---|---|---|
| **1.2.1 中秋版本**<br>*（未发布 · 本地测试）* | 2026-09-30 | 手柄检测修复（仅伴生进程）：蓝牙手柄电量不再误报"空"（XInput 不上报电量时按未知处理）+ 手柄类型 SubType 改为接上即读（UWP 包保持 2.2.0.0） |
| **1.2.0 中秋版本**<br>*（未发布 · 本地测试）* | 2026-09-30 | **手柄按键显示**：伴生进程 XInput 只读轮询（4 槽位/120Hz）+ 快照 16 字节手柄尾块（`CMD\|PROTO\|92` 握手制，不握手仍是 76 字节 v4）+ 小组件手柄组（ABXY/十字键/肩键/模拟量扳机/双摇杆环+点/View·Menu/Guide/电量，复用主题色与圆角键样式、跟随整体缩放）+ 设置窗口**布局页新增「手柄显示」**（显示方式/手柄选择/键位风格/显示部件/扳机显示/死区/品牌色，默认「自动」即无手柄时完全不影响现有界面）+ 独立预览窗口 `tools/pad_preview.py`（UWP 包 2.2.0.0） |
| **1.1.2 中秋版本**<br>*（正式版）* | 2026-09-30 | 修复「关闭小组件重开后设置回默认」（整体按键大小 / 光标大小 / 鼠标速度 / 鼠标光标按键 / 面板透明 / 按键透明度 / 布局锁定：冷启动未走设置恢复链）+ 修复缩放 ≠ 100% 时拖拽与吸附把屏幕像素当图层单位导致的速度与吸附错位（鼠标垫尺寸钳制改用同一系数保持等比）+ 修复删除过的内置键在「重置布局 / 应用预设」后不出现、应用带垫参数的预设需重启才生效 + 设置窗口 11 项修复（侧键 1/2 的 VK 互换、布局快照时间戳判等、应用预设漏写鼠标垫位置、主题预设误存设置窗口专用色槽、重置布局误删自定义配色、重置色彩漏第 10 槽、导入预设可写任意键、异常预设导致崩溃、应用主题预设不清色槽覆盖、光标按键开关状态自相矛盾）+ 伴生进程 3 项修复（options.json 非有限值启动崩溃、Backspace 撞「滚轮下」合成位、刷新率报 1 时推送降为 1Hz）+ 渲染路径幂等写入 + 手柄支持的采集模块与预览窗口（UWP 包 2.1.2.0） |
| **1.1.1 中秋版本**<br>*（正式版）* | 2026-09-26 | 布局页新增「整体按键大小」滑条（−100 ~ +100，默认 0，按百分比整体缩放面板；不改动键位坐标/尺寸数据，窗口自适应、右键菜单定位与吸附参考线一致跟随）（UWP 包 2.1.1.0） |
| **1.1.0 中秋版本**<br>*（正式版）* | 2026-09-26 | 默认布局全面同步（含 Tab 0,-222 / CapsLock 0,-170 / 鼠标垫 246.371×154@(72,2)）+ 参数页「鼠标速度」「光标大小」+ 鼠标光标按键（开关 + 按键捕获框）+ 颜色第 10 槽「鼠标点按下」+ 右键菜单 Windows 11 化（图标/投影/悬停/危险红/跟随缩放/实时跟色）+ 删除免二次确认 + 修复重置布局乱・Tab 掉下・鼠标垫变 1:1・预设保存存旧值・保存慢・重名加 (1) 后缀・粘贴按键字体不一致・Tab 与滚轮连点・CapsLock/Win 不亮・鼠标垫缩放无吸附（UWP 包 2.1.0.0） |
| **1.0.1 中秋版本**<br>*（正式版）* | 2026-09-26 | 右键菜单 Windows 11 风格化（宽圆角面板 / ThemeShadow 柔和投影 / 菜单项图标 / 悬停与按下反馈 / 删除项危险红 / 分组线）+ 菜单配色跟随色槽 + 菜单跟随窗口缩放 + 重置按键布局改为小组件进程执行 + 布局页补操作反馈（UWP 包 2.0.1.0） |
| **1.0.0 中秋版本**<br>*（正式版）* | 2026-09-26 | 全新设置窗口（主题/参数/颜色/字体/布局/预设/关于）+ 参数页（推送频率/跟随刷新率/低延迟/鼠标加速）+ 鼠标速度 + 按键区背景全透明 + 9 色槽重构与行内调色盘 + 字体 1-10 档粗细与号数偏移 + 预设文件导入导出与出厂默认预设 + 主面板旧设置 UI 全部移除 + 协议 v4（时间戳/GET_STATS/SET_OPT）+ 全新中秋 logo 与仅管理员安装包（UWP 包 2.0.0.0） |

更早的 0.0.1 ~ 0.9.3 版本说明见 [VERSION.md](VERSION.md)。

## 快速开始

### 安装（普通用户）

下载 `release/<版本>/KeyDisplaySetup.exe`，右键「以管理员身份运行」一键安装，然后按 `Win+G` 打开 Game Bar，在小组件列表里固定「按键显示」。安装包需要管理员权限（安装 UWP 包 + 注册登录自启与看门狗），卸载走控制面板，用户设置与预设不会被卸载包清掉。

### 手柄显示预览（不需要 Game Bar）

手柄显示已接入小组件，也可以用独立预览窗口单独看观感（无需 Game Bar）：

```powershell
python tools\pad_preview.py            # 双击 tools\pad_preview.cmd 也可以
python tools\pad_preview.py --mode live        # 实时读手柄（XInput）
python tools\pad_preview.py --selftest         # 几何/状态自检，不开窗口
```

窗口里同时画出键盘键与手柄组（同一套主题与圆角键样式），支持五主题、键位风格（Xbox/PlayStation/Switch）、整体大小、摇杆死区、扳机显示方式、部件开关；没有手柄时用内置演示动画也能看效果。

### 开发 / 构建（开发者）

```powershell
# 1) 键盘布局开发预览（无需 Game Bar）
python tools\preview.py

# 2) 构建伴生进程 EXE
cd KeyDisplay.Companion
python -m PyInstaller --onefile --noconsole --clean --name KeyDisplayCompanion `
    --collect-all ctypes --icon ..\installer\keydisplay.ico companion.py

# 3) 构建并签名 UWP 小组件 MSIX（需要 VS BuildTools + Windows SDK）
cd ..\installer
.\build-msix.ps1

# 4) 安装（会做就地升级，保留用户设置）
.\install-msix.ps1 -Appx ..\dist\KeyDisplay.Install\KeyDisplay.Widget_*.msix

# 5) 打安装包（Inno Setup 6）
& "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" installer\setup.iss
```

完整流程见 [docs/BUILD.md](docs/BUILD.md) 与 [docs/INSTALL.md](docs/INSTALL.md)。

## 测试

```powershell
cd KeyDisplay.Companion
python -m unittest test_units -v          # 45 个用例：快照协议字节布局 / 键位映射 / 钩子回调 / 管道命令 / 手柄尾块
python tools\pad_preview.py --selftest    # 手柄预览的几何与状态自检
```

## 诊断

| 用途 | 位置 |
|---|---|
| 小组件日志 | `%LOCALAPPDATA%\Packages\KeyDisplay.Widget_hdjf4fqmxxv8g\LocalState\diag.txt` |
| 布局快照（保存布局预设时由小组件写出） | 同上目录 `layout-snapshot.json` |
| 伴生进程日志 | `%LOCALAPPDATA%\KeyDisplay\pipe-debug.log` |
| 伴生进程选项 | `%LOCALAPPDATA%\KeyDisplay\options.json` |

## 已知限制

- 构建需 Visual Studio BuildTools（含 UWP 工作负载）与 Windows SDK；打安装包需 Inno Setup 6。
- 自动化注入的模拟键盘输入（`SendInput`）不会触发全局低级钩子（系统会丢弃注入的 LL 事件），键盘链路需物理按键实测。
- 小组件的指针 hover 事件（`PointerMoved`）无法用注入式鼠标移动触发，需真实鼠标悬停验证。
- 手柄部分目前只走 **XInput**：覆盖 Xbox 360/One/Series（有线/蓝牙/无线适配器）以及 Steam Input、DS4Windows 生成的虚拟手柄；不经 Steam 直连的 DS4/DualSense/Switch Pro 暂时看不到（计划用 `Windows.Gaming.Input` 补）。
- 小组件本身无法在无 Game Bar 的环境下验证界面，故新增 UI 先以独立预览窗口形式交付。

## 参与维护

- 先读 [docs/HANDOFF.md](docs/HANDOFF.md)（工程结构、关键字段与函数、踩坑清单与"不要重新引入"的结论）。
- 历史问题的完整调查与修复记录见 [docs/ISSUE-PINNED-CHROME.md](docs/ISSUE-PINNED-CHROME.md)。
- 版本号规则：产品号 `1.x.y`（登记在 `VERSION.md`）对应 UWP 包版本 `2.x.y.z`；每次发布递增并补一行历史，同时归档 `release/<产品号>/KeyDisplaySetup.exe`。

作者：恐龙milk　·　许可证：MIT
