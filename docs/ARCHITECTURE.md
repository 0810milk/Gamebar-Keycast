# 架构说明

## 组件与职责

```
KeyDisplayCompanion（Python，桌面进程）           KeyDisplay.Widget（UWP，Game Bar 沙箱）
┌──────────────────────────────┐                 ┌──────────────────────────────────┐
│ hooks.py                     │                 │ Widget1.xaml(.cs)               │
│  · WH_KEYBOARD_LL / WH_MOUSE │                 │  · 键盘 12 键 + 鼠标 5 键 UI     │
│  · RAWINPUT（游戏内增量累计）│                 │  · CompositionTarget.Rendering  │
│  · 可见性 150ms 去抖/校准缩放 │  命名管道        │    渲染（跟随显示器刷新率）      │
│ state.py  InputState         │ \\.\pipe\        │  · 右下角胶囊按钮切主题         │
│  · 键盘/鼠标位掩码           │ KeyDisplayState │ InputStateReader.cs             │
│ pipe_server.py               │ ───────────────► │  · CreateFileW + FileStream 读  │
│  · 多客户端，240Hz 推送 36B  │                 │    （MESSAGE 读模式）           │
│  · SDDL DACL + 包 SID 放行   │                 │  · 断线自动重连                 │
└──────────────────────────────┘                 │ NativeMethods.cs                │
                                                 └──────────────────────────────────┘
```

- **伴生进程**：无窗口（`--noconsole`），单实例互斥体
  `Local\KeyDisplayCompanionMutex`；由小组件通过 `keydisplay://start`
  协议拉起，也常驻于 `%ProgramFiles%\KeyDisplay`。
- **小组件**：仅在 Game Bar 显示时存活；打开即尝试连接管道，连不上时
  每 2 秒重试，并触发一次协议启动。

## 快照协议（68 字节，小端，v3）

> 0.4.0 起协议升 v3：为支持「自定义控件」的任意按键反色，在 v2 基础上
> 追加 32 字节的 256 位虚拟键（VK）位图，快照由 36 → 68 字节。
> 旧 36 字节连接仍可解析（`ExtraKeys` 为 null）。

```
偏移  类型    字段
0     char[4]  MAGIC "KDSP"
4     u8       version = 3
5     u16      keys
7     u8       mouse
8     i32      mouseX
12    i32      mouseY
16    i32      vsX（虚拟屏幕原点 X）
20    i32      vsY
24    i32      vsW
28    i32      vsH
32    u32      seq
36    u8[32]   extra（256 位 VK 位图）  ← v3 新增
```

- `keys` 位序（12 位）：`0=Q 1=W 2=E 3=R 4=A 5=S 6=D 7=F 8=Shift 9=Ctrl 10=Alt 11=Space`
- `mouse` 位序（5 位）：`0=L 1=R 2=M 3=X1 4=X2`
- `extra`（v3 新增）：256 位虚拟键位图，`bit = (extra[vk >> 3] >> (vk & 7)) & 1`，
  覆盖全部按键按下状态，供小组件驱动自定义键反色（UWP 沙箱禁止 P/Invoke
  `GetAsyncKeyState`，故由伴生进程采集全键位图后随帧下发）。
- `mouseX/Y`：Win32 屏幕坐标；虚拟屏幕范围（`vsX/vsY/vsW/vsH`）由伴生进程
  用 `GetSystemMetrics`（SM_*VIRTUALSCREEN）采集后随帧下发，
  小组件据此把坐标映射到 80×80 鼠标垫（UWP 沙箱内不允许 P/Invoke
  `user32!GetSystemMetrics`，故由桌面侧传入）。
- 字节布局 `struct.calcsize('<4sBHBiiiiiiI32s') == 68`。

Python 侧序列化：`KeyDisplay.Companion\state.py`；
C# 侧解析：`KeyDisplay.Widget\InputStateReader.cs`。

## 输入采集

- 键盘：`WH_KEYBOARD_LL`，VK→位映射；左右修饰键统一映射
  （LShift/RShift 均置 Shift 位）。
- 鼠标：`WH_MOUSE_LL`，按下置位、抬起清零，`WM_MOUSEMOVE` 更新坐标。
- 独占全屏：游戏用原始输入（RAWINPUT）接管鼠标并隐藏光标，LL 钩子不再产生
  `WM_MOUSEMOVE`。伴生进程 `RegisterRawInputDevices`（`RIDEV_INPUTSINK`，
  消息窗口收 `WM_INPUT`）：光标可见时用 `GetCursorPos` 校准绝对坐标，
  隐藏时累计原始增量，保证全屏游戏中坐标仍实时更新。
- 兜底校准：每帧 `GetAsyncKeyState` 核对实际按下状态（`reconcile()`），
  覆盖钩子漏事件场景（如按住期间切前台）。
- 已知限制：自动化注入的模拟键盘（SendInput）不触发 LL 钩子，
  物理按键正常（见 README「已知环境限制」）。

## 命名管道

- 名称：`\\.\pipe\KeyDisplayState`
- 服务器：Python 侧（重叠 I/O，可被停止标志中断）。
- 安全描述符：`D:(A;;GA;;;WD)(A;;GA;;;OW)(A;;GA;;;BU)`，
  并额外放行 `packageFamilyName` 派生的包 SID
  （`DeriveAppContainerSidFromAppContainerName`，来自 `config.json`）。
- 客户端：UWP 沙箱内 `System.IO.Pipes` 不可用，改用
  `CreateFileW` + `FileStream(SafeFileHandle)` 同步读 36 字节帧
  （`FILE_FLAG_OVERLAPPED` 以支持取消）。

## UWP 小组件清单要点（Package.appxmanifest）

- 包身份：`Name=KeyDisplay.Widget`，`Publisher=CN=KeyDisplay, O=KeyDisplay, C=CN`
- 激活：`Protocol` scheme `ms-gamebarwidget` →
  `XboxGameBarWidgetActivatedEventArgs`（`App.OnActivated`），
  `IsLaunchActivation=true` 时新建 `XboxGameBarWidget` 并导航到 `Widget1`。
- `uap3:AppExtension Name="microsoft.gameBarUIExtension" Id="KeyDisplayMain"`
  `PublicFolder="GameBar"`（内含图标 `KeyDisplayMain.png`，文件名须与 Id 一致）。
- `GameBarWidget Type="Standard"`：`PinningSupported`、`ActivateAfterInstall`、
  `FavoriteAfterInstall`、`Window/AllowForegroundTransparency`、
  初始 `560×280`（可缩放）。
- 透明背景：`Page Background="Transparent"` + 半透明面板画刷
  （暗 `#B3000000`，亮 `#B3FFFFFF`）。
- 依赖：`Microsoft.Gaming.XboxGameBar 7.2.240903001`、
  `Microsoft.NETCore.UniversalWindowsPlatform 6.2.9`。

## UI 尺寸（与需求规格一致）

| 元素 | 尺寸 |
|---|---|
| 标准键 Q/W/E/R、A/S/D/F | 52×48，间距 6 |
| 修饰键 Shift/Ctrl/Alt | 68×48，间距 6 |
| 空格 | 176×48 |
| 行距 | 8 |
| 面板内边距 | 16 |
| 鼠标垫 | 80×80 |
| 鼠标按钮 | 36×36 |

## 安装链路

- 证书：`make-cert.ps1` 生成自签名代码签名证书 →
  `certutil -addstore TrustedPeople`。
- MSIX：`build-msix.ps1` 侧载构建 + SignTool 签名。
- 协议：`HKCU\Software\Classes\keydisplay\shell\open\command`
  → `"<companion.exe>" "%1"`。
- 配置：`config.json` 写入 `packageFamilyName`。
- Setup.exe：Inno Setup（`installer\setup.iss`），控制面板卸载。

## 数据流时序（一次按键）

1. 物理按键 → `WH_KEYBOARD_LL` 回调置位 `state.keys`。
2. `pipe_server` 每 16.7ms 组帧推送（seq++）。
3. `InputStateReader` 读到帧 → `Snapshot` 事件 → 更新 `_latest`。
4. `DispatcherTimer`（33ms）应用反色/光标位置，视觉延迟约 1 帧 ≤ 50ms。
