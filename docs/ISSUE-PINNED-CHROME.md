# 已解决：固定组件后只显示按键（隐藏背景与黑白按钮）

> 状态：**根因已定位并已修复（2026-08-16，三轮），待用户实测确认**。
> 第一轮修复见「第 9 节」，第二轮（移除黑白按钮、修复退出闪退）见「第 10 节」，
> 第三轮（事件封送 UI 线程、主题切换改回屏幕按钮）见「第 11 节」。1–8 节为历史调查记录，保留备查。

## 1. 需求（用户原话归纳）

- 在 Game Bar 内打开组件（可调整）：要能看到**黑/白半透明面板背景**和底部**「黑/白」主题切换按钮**。
- 固定到屏幕后、退出 Game Bar：**只显示按键**，背景、按钮、状态字全部隐藏。

也就是说：`GameBarDisplayMode == Foreground`（Game Bar 打开）显示完整 UI，
`== PinnedOnly`（仅固定叠加、Game Bar 已关）只留按键。

## 2. 当前代码状态（工作区，领先最后提交）

### `KeyDisplay.Widget\Widget1.xaml.cs`
- 字段 `_docked`：true=只留按键。
- `OnLoaded`：读 `widget.GameBarDisplayMode == PinnedOnly` 初始化 `_docked`；
  订阅 `GameBarDisplayModeChanged`；启动 500ms 轮询 `_modeTimer`。
- `OnModePoll` / `OnGameBarDisplayModeChanged`：`_docked` 变化时调 `ApplyDocked()`。
- `ApplyDocked()`：`ApplyTheme()` + 记录 `widget.WindowBounds` 到 diag（用于确认是否窗口裁剪）。
- `ApplyTheme()` 末尾：`_docked` 时把 `RootPanel.Background/BorderBrush` 置 Transparent、
  `ThemeRow`（黑白按钮行）与 `StatusText` 置 `Collapsed`；否则恢复。
- `DiagLog`：写 `ApplicationData.Current.LocalFolder\diag.txt`。

### `KeyDisplay.Widget\App.xaml.cs`
- `widget1` 字段 + 公开只读属性 `Widget`；`OnActivated` 在 `IsLaunchActivation=true`
  时新建 `Frame` + `XboxGameBarWidget` + 导航 `Widget1`；`DiagLog` 同款。

### `KeyDisplay.Widget\Widget1.xaml`
- `RootPanel`（半透明背景 Border）→ 内层 Grid 两行：
  行0 = 键盘列 + 鼠标列；行1 = `ThemeRow`（黑/白按钮 `ThemeDarkBtn`/`ThemeLightBtn`）；
  右上角 `StatusText`。
- 窗口初始 `560×280`，可缩放（manifest `GameBarWidget`）。

## 3. 已确认的 API 事实（ildasm 反编译 winmd 7.2.240903001）

`XboxGameBarWidget`（sealed, public，命名空间 `Microsoft.Gaming.XboxGameBar`）：

- **属性**（均只读或可写见下）：`GameBarDisplayMode`、`Pinned`(bool)、`Visible`、`WindowState`、
  `WindowBounds`(Rect)、`AppExtensionId`、`Favorited`、`ClickThroughEnabled`、
  `CompactModeEnabled`、`RequestedOpacity`、`RequestedTheme`；
  `MinWindowSize`/`MaxWindowSize`/`PinningSupported`/`SettingsSupported`/
  `HorizontalResizeSupported`/`VerticalResizeSupported` 可读写。
- **事件**（几乎都是 `TypedEventHandler<XboxGameBarWidget, object>`）：
  `GameBarDisplayModeChanged`、`PinnedChanged`、`VisibleChanged`、`WindowStateChanged`、
  `WindowBoundsChanged`、`CloseRequested`、`SettingsClicked`、`RequestedThemeChanged` 等。
- **方法**：`TryResizeWindowAsync(Size)→IAsyncOperation<bool>`、`MinimizeAsync`、`RestoreAsync`、
  `CenterWindowAsync`、`Close`、`LaunchUriAsync`、`ActivateSettingsAsync`。
- 构造：`XboxGameBarWidget(XboxGameBarWidgetActivatedEventArgs, CoreWindow, Frame)`。

**枚举**：
- `XboxGameBarDisplayMode`：`Foreground=0`（Game Bar 打开）、`PinnedOnly=1`（仅固定叠加）。
- `XboxGameBarWidgetWindowState`：`Minimized=0`、`Restored=1`。

> 结论：API 足够支撑需求（用 `GameBarDisplayMode` 判断即可），问题不在"没有 API"。

## 4. 调查时间线与已尝试方案（含失败项）

| 版本 | 方案 | 结果 |
|---|---|---|
| A（提交 36f1a47） | 监听 `PinnedChanged`，读 `Pinned` | 用户"目测没有改变" |
| B | 改用 `GameBarDisplayMode` 初始化 + 事件 | 日志显示事件正确翻转，但用户仍"目测没有改变"；且用户反馈 Game Bar 内看不到黑白按钮 |
| C | 初始化强制 `_docked=false`（先显示）靠事件隐藏 | 用户反馈"和以前一样"（固定后背景按钮又可见）——因为固定叠加实例创建时已是 PinnedOnly，无后续事件，永远不隐藏 |
| D | 初始化读模式 + 事件 + 500ms 轮询 | 日志显示 `docked` 正确翻转；用户仍说看不到黑白按钮 |
| E | D + `TryResizeWindowAsync` 随状态调整窗口尺寸 | **固定/退出瞬间小组件闪退**（异步调用在 PinnedOnly 切换时崩溃），已回退，只保留 WindowBounds 日志 |

## 5. diag.txt 实测证据（多轮会话关键点）

- 每次 `Win+G` 打开组件，`App.OnActivated` 常触发**多次** `IsLaunchActivation=True`
  → 创建**多个 XboxGameBarWidget 实例/窗口**（多次 `onloaded enter`）。
- **激活瞬间 `GameBarDisplayMode` 常误报 `PinnedOnly`**，随后事件才翻转为 Foreground；
  反之关 Game Bar 时事件正确翻转为 PinnedOnly。日志中 `display mode changed => docked=True/False` 均正常触发。
- 多实例时第二个实例连接管道失败：`CreateFileW failed err=231`（ERROR_PIPE_BUSY），
  长时间无法重连（第一个实例的连接未释放，伴生进程单客户端模型）。
- 事件触发与 UI 视觉**不一致**：日志正确，用户看到的窗口却没变。

## 6. 疑点与假设（供下一位排查）

1. **多实例叠置**：Game Bar 每次激活都新建实例；用户看到的窗口可能不是收到事件的那个，
   或旧窗口未销毁（`Close()` 疑似不销毁窗口，曾用其去重但留下"孤儿窗口"）。
2. **窗口裁剪**：固定时 `ThemeRow` Collapsed → 窗口可能被 Game Bar 压矮；
   回 Game Bar 后按钮行在可视区外 →"看不到黑白按钮"。当前代码已记录 `WindowBounds`，
   **请先跑一轮：Win+G→打开→固定→退出→再打开，然后读 diag.txt 看 `bounds WxH`**，
   判断 docked=false 时窗口是否够高容纳按钮行（按钮行约需 +42px 高度）。
3. **事件对象与可见实例错位**：多实例共享 `App.Widget`（只存最后一个），
   `OnModePoll` 用的是共享引用，可能轮询的不是自己所在窗口的实例。

## 7. 下一步建议（按优先级）

1. **先取证**：用当前已装版本（已含 WindowBounds 日志）跑一轮完整流程，
   读 `diag.txt` 的 `bounds` 与 `docked`，确认是"状态错位"还是"窗口裁剪"。
   - 若是裁剪：不要在 PinnedOnly 切换瞬间改尺寸（会闪退）；
     改为在转到 Foreground 后延迟 300-500ms 再 `TryResizeWindowAsync`；
     或把按钮行做成**不占高度的浮层**（叠在按键区右下角），彻底摆脱窗口高度依赖；
     或让窗口始终保持最高尺寸、固定时只改透明（底部留透明空白）。
   - 若是状态错位：给每个实例持**自己的** `XboxGameBarWidget` 引用（不要走共享 `App.Widget`），
     各自判断自己的 `GameBarDisplayMode`。
2. 排查多次激活：在 `App.OnActivated` 记录 `widgetArgs` 更多字段（AppId/InstanceId），
   判断 Game Bar 是否为 pinned+floating 各建一个实例；若是，则按各自模式独立渲染即可（不合并）。
3. 修好后的收尾流程（必做）：
   - 重建 MSIX → 签名 → `Remove-AppxPackage` + `Add-AppxPackage` → 清 diag → 用户实测；
   - 重建 `Setup.exe`（`ISCC installer\setup.iss`）；
   - `git add` + commit；清理临时的过度日志输出（保留精简版）。

## 8. 兜底方案（若自动检测始终不可靠）

- 把主题切换收敛到**右键菜单**（已存在 `ThemeItem`），底部黑白按钮仅在"可见且未固定"时显示；
- 或者默认隐藏底部按钮，只在右键菜单提供，减少对固定状态的依赖。
- 用户最初需求是"退出后只显示按键"，若状态检测不稳定，
  一个可接受的退路是：**固定场景下始终只显示按键，主题切换全走右键菜单**。

---

## 9. 根因与修复（2026-08-16，已实施）

### 根因

1. **主因（diag.txt 实测确认）**：组件激活瞬间 `GameBarDisplayMode` 误报 `PinnedOnly`，
   且 `Pinned=false`。实测日志里该误报持续约 54 秒（`11:19:02` 激活 `initial docked=True pinned=False`，
   直到 `11:19:56` 用户操作才翻转为 Foreground）。旧代码 `_docked = (mode==PinnedOnly)` 只看 mode，
   于是组件在 Game Bar 内一打开就进入"只留按键"态 → **Game Bar 内看不到黑白按钮/面板**。
   微软官方文档（learn.microsoft.com/gaming/game-bar）定义"固定态" = `GameBarDisplayMode==PinnedOnly`
   且 `Pinned==true`（`Pinned` 是复合属性，激活误报时 `Pinned=false`，可区分"真固定"与"误报"）。
2. **次因（潜在）**：Widget1 页面通过共享的 `App.Widget`（最后一次创建的实例）做轮询/订阅/退订，
   Game Bar 多实例时状态会错位、事件泄漏。本仓库 grep 确认 4 处 `app.Widget` 均集中在 Widget1.xaml.cs。
3. **裁剪风险**：`ThemeRow` 所在 Grid 行为 `Auto` 高度，布局随固定状态伸缩；
   且 `ApplyDocked` 里 bounds 日志的异常被静默吞掉（实测 diag 中从未出现 `bounds` 行），无尺寸证据。

### 修复内容（工作区代码，3 个文件）

| 文件 | 改动 |
|---|---|
| `Widget1.xaml.cs` | 新增实例私有 `_widget`（由 `App` 经 `NavigationParameter` 传入，`OnNavigatedTo` 获取）；所有轮询/订阅/退订改用自己的 `_widget`，不再读 `app.Widget`。docked 判定改为复合规则 `mode==PinnedOnly && Pinned`。同时订阅 `GameBarDisplayModeChanged` 与 `PinnedChanged`，保留 500ms 轮询兜底。`ApplyDocked` 的 bounds 日志捕获并输出异常（不再静默），每次记录 `mode+pinned+docked`。转 Foreground 后延迟 400ms，若 `WindowBounds.Height<240` 则 `TryResizeWindowAsync(560,280)` 恢复高度（仅在非固定态调用，规避版本 E 的闪退场景）；`OnLoaded` 时抬升 `MinWindowSize` 高至 240。 |
| `App.xaml.cs` | `rootFrame.Navigate(typeof(Widget1), widget1)` 把 widget 作为导航参数传入；激活日志追加 `AppExtensionId`。 |
| `Widget1.xaml` | `ThemeRow` 所在 `RowDefinition` 高度由 `Auto` 改为固定 `42`（30 按钮 + 12 上边距），Grid 总高不随固定状态变化（约 234px），窗口无需随状态改尺寸 → 从根本上消除按钮被裁剪的路径；固定时该行折叠后留透明空间，视觉上仍是"只显示按键"。 |

### 验证状态

- 已重建 MSIX、签名、`Remove-AppxPackage` + `Add-AppxPackage` 重装成功（`Status: Ok`），diag.txt 已清空。
- **待用户实测**：Win+G 打开组件 → 应立刻看到半透明面板 + 黑白按钮 + 状态字；
  固定并退出 Game Bar → 只剩按键；再次打开 → 面板/按钮回来。随后回读 diag.txt，
  确认 `mode/pinned/docked/bounds` 序列符合预期（重点：激活时 `initial docked=False mode=Foreground pinned=False`，
  固定后 `mode=PinnedOnly pinned=True docked=True`）。
- `Setup.exe` 重建与 git commit 见后续收尾流程。

### 遗留说明

- 若实测仍有问题，优先回读 diag.txt 的新增日志（bounds 异常现在会打印异常类型与消息，
  可判断 `WindowBounds` 在固定态是否可读、`TryResizeWindowAsync` 是否被触发）。
- 多实例场景：每个实例现在各自持自己的 widget 引用、各自渲染，不再互相串扰；
  `App.OnActivated` 的 `AppExtensionId` 日志可帮助确认 Game Bar 是否为 pinned+floating 各建实例。

---

## 10. 第二轮修复（2026-08-16，移除黑白按钮 + 修复退出闪退）

### 用户反馈

- 主题切换正常；
- **退出 Game Bar 时小组件直接关闭**（期望：固定后退出只留按键）；
- 退出后背景没有隐藏。

### 取证（新 diag.txt，43 行）

- 激活误报已被第一轮修复正确消化：`initial docked=False mode=PinnedOnly pinned=False` ✓；
- 固定/退出时 `mode changed => docked=True mode=PinnedOnly pinned=True` 均正确翻转 ✓；
- **关键：全程没有任何 `bounds` / `resize failed` 行** —— 说明每次模式切换后 `ApplyDocked`
  在 `ApplyTheme()` 阶段就中断（异常沿 async void 同步段上抛 → 未处理 → 进程崩溃 → 小组件关闭），
  而每次 `activate launch=True` 都是崩溃后 Game Bar 重新拉起的实例；
  即第一轮加的「延迟 400ms TryResizeWindowAsync」与 `MinWindowSize=240` 在退出/切换时机仍是闪退源
  （与版本 E 的闪退同机制，之前归因不准）。
- 每次打开都会新增实例（5 个实例/会话），第二个实例起管道连接失败 `err=231`（伴生进程单客户端，未修）。

### 改动（采纳用户判断：黑白按钮是问题根源，按文档兜底方案收敛）

1. **`Widget1.xaml`**：整体删除 `ThemeRow`（黑/白两个 Border 按钮）与 Grid 行定义，
   组件表面不再有任何按钮；主题切换改由**右键菜单 `ThemeItem`**（原已存在）提供。
2. **`Widget1.xaml.cs`**：
   - 删除 `SetThemeToggle` / `ThemeDark_Click` / `ThemeLight_Click`；`ApplyTheme` 的 docked 分支只隐藏
     `RootPanel` 背景/边框与 `StatusText`。
   - **删除第一轮的 `MinWindowSize=240` 与「延迟 400ms `TryResizeWindowAsync`」**（退出闪退源）。
   - 所有 widget 属性读取（`IsDocked`、事件 handler 日志、poll）一律 try-catch：
     COM 属性在退出/销毁瞬间可能抛错，任何异常按"未固定"处理且绝不外抛 → 进程不可能再被模式处理炸掉。
   - `ApplyDocked` 的 `ApplyTheme()` 也包 try-catch，失败仅记日志。
   - 保留：实例私有 `_widget`（导航参数传入）、复合判定 `mode==PinnedOnly && Pinned`、
     `GameBarDisplayModeChanged`+`PinnedChanged` 双事件 + 500ms 轮询兜底、bounds 诊断日志。

### 验证状态

- 已重建 MSIX、签名、重装成功（`Status: Ok`），diag.txt 已清空。
- **待用户实测**：Win+G 打开 → 面板+按键+状态字；右键可切换黑白主题；
  固定后退出 Game Bar → 只剩按键（背景/状态字隐藏）、**不再闪退关闭**；再次打开 → 面板回来。
- 回读 diag.txt 应能首次看到 `bounds WxH` 行（证明 ApplyDocked 完整执行）与干净的
  `docked` 翻转序列。
---

## 11. 第三轮修复（2026-08-16，事件封送 UI 线程 + 主题切换改回屏幕按钮）

### 用户反馈

- 主题切换（右键菜单路径）**直接崩溃**；模式切换本身已不再闪退。

### 取证

- diag.txt：模式切换全部出现 `applytheme failed: ... 0x8001010E`（"应用程序调用一个已为另一线程整理的接口"）
  —— 证明 `GameBarDisplayModeChanged`/`PinnedChanged` 在**非 UI 线程**回调，直接触碰 UI 元素即抛此错。
- Windows 事件日志：**六次崩溃（含第一、二轮）签名完全一致** —— `Windows.UI.Xaml.dll` +
  `0xc000027b`（未处理的 XAML stowed 异常）+ 同一偏移 `0x8f9cc3`，即每次都是"未处理 XAML 回调异常"终止进程。
- 第一轮实测"切换主题没问题"用的是**屏幕上的黑白按钮**（Border+Tapped），本轮崩溃路径是**右键菜单**
  （MenuFlyout，Game Bar 特殊宿主窗口内 PopUp 交互存在风险）→ 主题切换应改回屏幕控件。

### 改动

1. **`Widget1.xaml.cs`**：
   - `OnGameBarDisplayModeChanged`/`OnPinnedChanged`：先在回调线程计算 `_docked`，界面更新统一走
     `Dispatcher.RunAsync(CoreDispatcherPriority.Normal, ApplyDocked)` 封送到 UI 线程
     （官方示例的标准写法），彻底消除 `0x8001010E` 跨线程 UI 访问；`ApplyDocked` 内仍保留 try-catch 兜底。
   - 删除 `Root_RightTapped` / `ThemeItem_Click` 等全部菜单逻辑。
   - 新增 `ThemeToggle_Click`（Border+Tapped，与第一轮验证可用的按钮同机制）。
2. **`Widget1.xaml`**：删除 `MenuFlyout`/`RightTapped`；底部恢复**固定 42px 行**，
   放一个**单一紧凑胶囊主题按钮**（44×26，标签显示可切换到的主题：暗色时显示"白"、亮色时显示"黑"，
   自身配色即预览）。固定时隐藏。
3. **`App.xaml.cs`**：增加 `Application.UnhandledException` 兜底日志（记录异常类型与消息到 diag.txt）。

### 验证状态

- 已重建 MSIX、签名、重装成功（`Status: Ok`），diag.txt 已清空。
- **待用户实测**：Win+G 打开 → 面板+按键+状态字+右下角主题按钮（点按切换，不应崩溃）；
  固定后退出 Game Bar → 只剩按键、不再闪退；再次打开 → 面板回来。
- diag.txt 应不再出现 `applytheme failed` 行（事件已正确封送 UI 线程），并能看到 `bounds WxH` 与干净翻转序列。

---

## 12. 伴生进程修复（2026-08-16，鼠标点在两个位置间疯狂闪现）

### 现象

鼠标移动时（尤指光标被隐藏/抑制的场景，如游戏），小组件鼠标垫上的鼠标点在两个位置间反复振荡闪现。

### 根因（hooks.py 双坐标源冲突）

- WH_MOUSE_LL 钩子在每个 `WM_MOUSEMOVE` 上**无条件**用 `ms.pt` 覆写绝对坐标；
- RAWINPUT 路径在**光标隐藏时**改为累计增量（`mx += lLastX`）；
- 光标隐藏时两条路径同时生效：LL 把坐标打回旧位置，RAWINPUT 又在旧值上累加增量
  → 坐标反复在两个位置间跳动（"查询 GetCursorInfo 并修改坐标"即该内部逻辑）。
  全屏独占场景下 LL 通常不产生 WM_MOUSEMOVE（原 6bda7da 修复的前提），
  但无边框/窗口化全屏、光标被抑制（CURSOR_SUPPRESSED）等场景仍会触发。

### 修复（KeyDisplay.Companion/hooks.py）

- 抽出 `_cursor_visible()`：GetCursorInfo 失败时按可见处理（尽力用绝对坐标）；
- `_mouse_proc` 的 `WM_MOUSEMOVE` 仅在光标可见时校准绝对坐标；
- `_handle_raw_input` 同样改走 `_cursor_visible()`，可见时用 `GetCursorPos` 校准，
  隐藏时累计增量 —— 两条路径语义一致，任何时刻只有一个坐标来源生效。
- `test_units.py` 适配（mock `_cursor_visible`）并新增"光标隐藏不覆写坐标"用例，
  15 项单测全过。已重建 `KeyDisplayCompanion.exe` 并部署替换运行中的实例
  （`用户测试\KeyDisplay\KeyDisplayCompanion.exe`），widget 已重连（diag `connected`）。

### 验证状态

- 待用户实测：游戏中/光标隐藏时移动鼠标，鼠标点应平滑跟随，不再双位置闪烁。

---

## 13. 鼠标坐标实现重构（2026-08-16，单一数据源）

### 需求

"改一下鼠标位置的实现方式逻辑，目的不变、UI 不变"（第 12 节的"钩子可见性门控"方案结构上仍是双写，
进一步收敛为单源）。

### 新逻辑（KeyDisplay.Companion/hooks.py + pipe_server.py）

- **桌面（光标可见）**：坐标由 60Hz 推送循环里的 `sync_mouse_position()`（GetCursorPos）轮询校准；
- **游戏（光标隐藏）**：坐标由 RAWINPUT 增量累计（`_handle_raw_input`）；
- **WH_MOUSE_LL 钩子只负责鼠标按键采集**（`_mouse_proc`），不再写坐标 —— 从结构上消除双源冲突；
- 可见性判定 `_cursor_visible()` 仍是两条路径的切换开关，任何时刻只有一个坐标来源写入。

协议、widget、UI 均未改动。

### 验证

- 16 项单测全过（WM_MOUSEMOVE 不再写坐标；sync 可见→更新 / 隐藏→保持）。
- 已重建 `KeyDisplayCompanion.exe` 并部署重启，widget 重连（diag `connected`）；
- `Setup.exe` 已重建，桌面备份已刷新。

---

## 14. 帧率放开（2026-08-16，解除 60Hz/30fps 硬限制）

### 需求

"帧率跑多高跑多高，不要只限制在 60 帧"。原链路有两处硬限制：
伴生进程固定 60Hz 推送（`time.sleep(1/60)`）、小组件固定 30fps 渲染（DispatcherTimer 33ms）。

### 改动

1. **伴生进程推送频率可配置**（`pipe_server.py`）：`PipeServer(..., fps=240)`，
   `config.json` 新增 `"fps": 240`（默认 240Hz，覆盖 60/120/144/240Hz 高刷显示器，可再调高）。
   `companion.py` 从 config 读取并传入。
2. **小组件渲染跟随显示器刷新率**（`Widget1.xaml.cs`）：删除 33ms DispatcherTimer，
   改用 `CompositionTarget.Rendering`（每 UI 帧触发一次，显示器多快就渲染多快）。
3. **序号去重**（`InputStateReader.cs` 解析 36 字节帧的 seq 字段；`Widget1.OnRendering`
   按 `Seq` 相同即跳过重绘）—— 高帧率下数据未变化时零开销。
4. 顺带修复 `install-msix.ps1` 写 config.json 带 BOM 导致 Python json 解析失败的问题
   （改 `UTF8Encoding($false)` 无 BOM 写入，fps 配置真正生效）。

### 说明

- 显示器刷新率是渲染侧的天花板（60Hz 显示器最多显示 60fps；120/144/240Hz 才能看到更高帧率）。
- 推送频率高于渲染/显示器频率没有额外收益（widget 只消费最新帧），240Hz 默认已足够；
  需要更高可改 `config.json` 的 `fps`（注意 Windows `time.sleep` 精度上限约 500–1000Hz）。

### 验证

- 16 项单测全过；widget MSIX 构建/签名/重装成功；新伴生进程已部署重启；
  `Setup.exe` 已重建、桌面备份已刷新；config.json 无 BOM 解析正常（fps=240 生效）。

---

## 15. 鼠标垫跟随屏幕纵横比（2026-08-16）

### 需求

"鼠标垫尺寸/比例按用户实际屏幕比例调整；如有更好的比例判定方法请告知。"

### 比例判定（比"识别分辨率"更优的方案）

**直接用协议帧里已带有的虚拟屏幕尺寸 `vs_w/vs_h`**（伴生进程每帧 GetSystemMetrics
`SM_CXVIRTUALSCREEN/CYVIRTUALSCREEN` 实时下发）：
它是鼠标坐标的映射基准，比例天然一致；分辨率切换/多显示器/投影时实时跟随；
零新增数据。备选方案均更差：`GetSystemMetrics(SM_CXSCREEN)` 只含主屏（多显示器会错）、
`EnumDisplayMonitors` 需自选显示器且静态、EDID/DXGI 枚举过重无收益。
注意虚拟屏幕是所有显示器的并集（多显示器时鼠标垫显示的是整个虚拟屏）——如需只按主屏比例，
伴生进程改用 `SM_CXSCREEN/SM_CYSCREEN` 即可（一行改动，但多显示器时坐标会落到垫外）。

### 实现（KeyDisplay.Widget/Widget1.xaml.cs）

- `ComputePadSize`：按 `vs_w:vs_h` 保比例装入 180×120 上限盒子，极端比例保比例缩放
  到最小边（40×36）以上，防面板被撑爆；无效值按 16:9 兜底。
- `UpdatePadSize`：尺寸变化时才设置 `MousePad.Width/Height`（避免每帧触发布局）。
- 鼠标点映射与钳位改用动态 `_padW/_padH`（不再硬编码 80/70）。

### 验证

- widget MSIX 构建/签名/重装成功（`Status: Ok`），`Setup.exe` 已重建、桌面备份已刷新。
- 待用户实测：16:9 → 约 180×101 宽垫；21:9 → 约 180×77；竖屏/超高屏 → 高垫。

---

## 17. 全屏下光标无法触垫边 → 活动范围自适应（2026-08-16）

### 现象

全屏（尤其游戏隐藏光标）时，鼠标垫上的点够不到垫边。

### 根因

垫子按虚拟屏幕严格 1:1 镜像，但游戏光标坐标本身走不到屏幕边界：
光标隐藏时只能累计 RAW 增量（游戏灵敏度≠像素）、光标被游戏锁定/限制在子区域、
或 60Hz GetCursorPos 校准把累计值拉回 —— 三种情况都会让点走不满垫子。

### 方案（用户选定：活动范围自适应）

- **协议升级 v2→v3（36→44 字节）**：`state.py` 快照末尾新增 `ux`/`uy`（int32，0..1000），
  即鼠标在"最近活动范围"内的归一化位置；`VERSION=3`、`SNAPSHOT_SIZE=44`。
- **伴生进程（`pipe_server.py`）**：`_pump` 维护最近约 1.5 秒（`n_history = 1.5/fps` 帧）的
  `(mx,my)` 滑动窗口，取 min/max 作为活动范围，`normalize_position()` 把当前坐标归一化到
  0..1000（范围退化/单点时返回 500；越界钳制）。
- **小组件**：`InputStateReader` 改读 44 字节帧（`Ux`/`Uy`），`Widget1` 点位置改为
  `Ux/1000*padW`、`Uy/1000*padH`；垫子纵横比仍由 `vs_w/vs_h` 决定（形状不变）。

效果：光标无论被限制在屏幕哪个子区域，点都能铺满垫子并触边（活动范围两端即垫子两端）；
桌面下活动范围≈屏幕，行为与严格镜像基本一致；垫子形状仍保持屏幕纵横比。

### 验证

- 17 项单测全过（roundtrip 含 ux/uy、归一化边界/退化/越界用例）。
- 新伴生 EXE 已部署重启、widget MSIX 已重装（`Status: Ok`）、`Setup.exe` 已重建、桌面备份已刷新。

---

## 18. 修复活动范围方案的严重缺陷：光标静止时点自行行走（2026-08-16）

### 现象

游戏内光标静止（未更新）时，鼠标垫上的点会自行漂移行走。用户判定为严重问题。

### 根因（两个机制叠加）

1. **窗口收敛漂移**：滑动窗口保存最近 1.5 秒样本。光标冻结后，早先移动留下的
   min/max 极值样本逐渐过期，窗口向冻结点收拢，冻结点被归一化后的位置随之漂移
   （实测可横穿整个垫子）。
2. **微抖动放大**：活动范围变小时，±2px 的抖动会被归一化放大成满垫跑动。

### 修复（KeyDisplay.Companion/pipe_server.py）

1. **窗口冻结**：`_push_sample` 在位置与上帧相同（静止）时**不推进、不过期**窗口 ——
   光标静止 → 窗口静止 → 归一化值不变，点完全静止。
2. **最小范围地板**：`normalize_position` 增加 `floor_x/floor_y`（屏幕尺寸的 10%），
   活动范围小于地板时以**当前位置为中心**锚定窗口 —— 小范围移动时的微抖动被阻尼，
   不再放大成满垫跑动。
3. 活动范围大于地板时仍保持"铺满垫子、触边"的自适应行为（原目标不变）。

### 验证

- 19 项单测全过，新增回归用例：
  `test_normalize_frozen_cursor_does_not_drift`（静止不推进 + 收敛窗口值稳定）、
  `test_normalize_floor_damps_jitter`（±2px 抖动位移 ≤1% 垫宽）。
- 新伴生 EXE 已部署重启、`Setup.exe` 已重建、桌面备份已刷新（14:41）。

---

## 19. 修复伴生进程崩溃导致鼠标点不显示（2026-08-16）

### 现象

修复"自行行走"后，鼠标垫上的点完全不显示了。

### 根因

`pipe_server.py` 调用 `_push_sample(history, mx, my, n_history)` 与函数签名
`_push_sample(history, n_history, mx, my)` **参数顺序不匹配**：`n_history` 收到的是
鼠标坐标（多显示器时可为负），`while len(history) > n_history` 把窗口清空 →
`min()` 对空序列抛 ValueError → 泵线程崩溃 → 管道服务器死亡 → widget 收不到帧
（diag 连续 `err=2`），鼠标点从未出现。单测直接调用 helper 用了正确顺序，未覆盖到
真实调用路径，故未拦截。

### 修复

- 调用改为 `_push_sample(history, n_history, self._state.mx, self._state.my)`；
- `_update_normalized` 增加 `if not history: return` 防御；
- 新增端到端回归用例 `test_update_normalized_end_to_end`（直接驱动 `_update_normalized`，
  覆盖单点退化/满行程/静止不漂移），20 项单测全过。

### 验证

- 新伴生 EXE 已重建部署，widget 已重连（diag `connected` 14:46:59）；
  `Setup.exe` 已重建、桌面备份已刷新（14:47）。

---

## 20. 鼠标检测回报率限频 500Hz（2026-08-16）

### 需求

高回报率鼠标（1000Hz/8000Hz）导致卡顿；先按用户要求把**检测回报率限制到 500Hz**。

### 实现（KeyDisplay.Companion/hooks.py）

- `_handle_raw_input` 增加时间限频：距上次处理不足 `1/500s` 的 WM_INPUT 事件直接跳过
  （常量 `RAW_REPORT_LIMIT = 500.0`，可调）。
- 无损性：桌面坐标走 GetCursorPos 绝对校准（限频不影响精度）；游戏内点位置经活动范围
  归一化（第 17 节），限频后累计增量减少只压缩轨迹范围，归一化后仍铺满垫子、比例不变。

### 验证

- 21 项单测全过（新增 `test_raw_input_throttled`：限频内跳过不解析 lParam）。
- 新伴生 EXE 已部署重启，widget 重连（diag `connected` 14:53:24）；
  `Setup.exe` 已重建、桌面备份已刷新（14:53）。

---

## 21. 日志监控 + 多客户端管道修复"没有位置显示"（2026-08-16）

### 现象与取证

用户反馈"光标没有位置显示"。diag.txt 显示该 widget 实例 **8 分钟连续
`CreateFileW failed err=231`（管道忙）从未连上** —— 管道单客户端（nMaxInstances=1）
被残留 widget 实例占住，新实例收不到任何帧 → 点不显示（状态"未连接"）。

### 改动

1. **日志监控（用户要求）**：
   - 新增 `KeyDisplay.Companion/debuglog.py`：写入 `pipe-debug.log`（exe 同目录），
     不依赖 stdout 重定向，`Start-Process` 启动也可靠落盘。
   - `hooks.py`：原生输入计数（`_raw_count` 已处理 / `_raw_skip` 限频跳过）、
     光标可见性切换日志（`[raw] cursor visible/hidden`）、坐标来源标记
     （`sync`=GetCursorPos / `acc`=增量累计 / `abs`=绝对原始坐标）、
     隐藏态增量日志（每次进入隐藏记前 20 条）。
   - `pipe_server.py` 泵循环：每 0.5s 一条摘要
     `[pump] fps=.. raw=.. skip=.. src=.. vis=.. mx=.. my=.. ux=.. uy=..`；
     客户端连接/断开日志。
   - `Widget1.xaml.cs`：diag.txt 每秒一条 `dot ux=.. uy=.. pad=WxH pos=x,y vis=0/1`。
2. **多客户端管道（根因修复）**：`_make_pipe` 实例数 1 → `PIPE_UNLIMITED_INSTANCES`，
   `run()` 每接受一个客户端创建一个管道实例并起独立推送线程（`_pump` 持有句柄生命周期）。
   每个 widget 实例都能连上，err=231 不再出现。

### 验证

- 21 项单测全过；`test_client.py` 双客户端同时连接成功（伴生日志两条
  `client connected`/`disconnected`，无 231）；泵摘要 fps≈216、`src=sync`、
  静止光标 `ux=500 uy=500`（冻结窗口不漂移）。
- 新伴生 EXE + widget MSIX 已部署重装；`Setup.exe` 已重建、桌面备份已刷新（15:52）。

### 日志位置

- 伴生进程：`用户测试\KeyDisplay\pipe-debug.log`
- 小组件：`%LOCALAPPDATA%\Packages\KeyDisplay.Widget_hdjf4fqmxxv8g\LocalState\diag.txt`

---

## 22. 游戏内移动校准：增量缩放 + 屏幕钳制（2026-08-16）

### 现象

用户实测：游戏内（光标隐藏）点移动"不正常、和桌面很不一样"。

### 日志证据

- 游戏内 `src=acc`（累计路径）工作、原生输入流入（raw≈30/0.5s）；
- 但累计坐标漂移严重：游戏内 mx 一度到 -2206，恢复可见瞬间 GetCursorPos=853，
  偏差约 3000px —— 1 增量=1 像素 的假设失准（系统指针速度/游戏灵敏度），
  且 FPS 转视角时累计值无界增长，活动范围被撑大导致正常移动被压缩。

### 修复（KeyDisplay.Companion/hooks.py）

1. **增量→像素缩放校准**：桌面（光标可见）期间用 GetCursorPos 真值每 1s 指数平滑校准
   `_scale_x/_scale_y`（原始增量与光标位移的比值）；游戏内累计时套用该校准系数，
   点速与桌面手感一致（`_calibrate_scale` / `_accumulate_motion`）。
2. **累计坐标钳制到虚拟屏幕**：`_accumulate_motion` 将累计值限制在 `[vs_x, vs_x+vw]`
   `[vs_y, vs_y+vh]`，FPS 转视角不再无界漂移，点正确地停在垫边（对应屏幕边缘）。
3. 泵摘要日志增加 `sx=.. sy=..`（当前缩放系数，便于监控校准是否生效）。

### 验证

- 23 项单测全过（新增 `test_accumulate_motion_scale_and_clamp`、
  `test_calibrate_scale`）。
- 新伴生 EXE 已部署重启，widget 重连；`Setup.exe` 已重建、桌面备份已刷新（16:26）。
- 待用户实测：进游戏移动鼠标，点速应与桌面一致，光标到屏幕边缘时点停在垫边。

---

## 23. 修复"游戏内光标不动"：校准系数塌陷 + 可见性闪烁（2026-08-16）

### 现象

上一版（第 22 节）实测后用户反馈：游戏内光标（点）不动了。

### 日志证据（pipe-debug.log）

- `sx` 从正常的 ~1.0 一路衰减到 **0.07**（sy=0.10）—— 校准把系数打对折了；
- 16:27:44 一秒内 `cursor visible→hidden→visible→hidden` 高频闪烁。

### 根因（两个叠加）

1. **校准系数塌陷**：可见性在游戏/菜单边界闪烁时，`_cursor_visible()` 短暂返回 True →
   `_calibrate_scale` 在游戏内运行；游戏光标锁定（增量持续但 GetCursorPos 不动）
   → 测得比值 0 → 系数每 1s 减半（1.0→0.5→0.25→0.06）→ 游戏内累计 `delta×0.07` 几乎不动。
2. **可见性闪烁 + sync 回写**：泵每帧在"可见"的闪烁瞬间把累计坐标打回 GetCursorPos
   （游戏锁定的冻结位置）→ 累计被反复清零。

### 修复（KeyDisplay.Companion/hooks.py）

1. **校准守卫**：仅当光标实际移动（位移 >1px）才更新系数；锁定/顶边场景不衰减。
   系数限制在 [0.1, 5.0] 防异常（`_calibrate_scale`）。
2. **可见性 150ms 去抖**：`GetCursorInfo` 状态需持续 150ms 才切换 visible/hidden
   （`VIS_DEBOUNCE`），闪烁不再触发 sync 回写、也不触发游戏内校准。

### 验证

- 25 项单测全过（新增 `test_calibrate_scale_locked_cursor_keeps_scale`、
  `test_visibility_debounced`）。
- 新伴生 EXE 已部署重启，日志显示 `sx=1.00 sy=1.00`（未塌陷）；
  `Setup.exe` 已重建、桌面备份已刷新（16:30）。

---

## 16. 键盘布局：空格键移到第四行（2026-08-16）

用户指正：空格键应放**第四行**（单独一行，标准键盘式底部空格），按键尺寸不变
（QWER/ASDF 52、Shift/Ctrl/Alt 68、空格 176）。改动：

- `Widget1.xaml`：第三行改为 Shift/Ctrl/Alt，新增第四行放空格（176 宽）；
- `tools/preview.py`：`KEY_ROWS` 同步（空格独立一行）。

协议位序（KEY_ORDER）与渲染无关，无需改动。widget 已重建/签名/重装，
`Setup.exe` 已重建、桌面备份已刷新。

---

## 24. 修复"调整控件后卡死闪退"：拖拽释放 CaptureLost 竞态 NRE（2026-08-17）

### 现象

0.3.0 开发中，解锁布局后拖拽按键边缘/四角，释放鼠标后 widget 卡死闪退。

### 根因

`Key_PointerReleased` 中 `_dragKey.ReleasePointerCapture(e.Pointer)` 会**同步**触发
`Key_PointerCaptureLost` → 回调里 `_dragKey = null` → 回到 Released 的下一行再访问
`_dragKey.Width` 抛 `NullReferenceException`（on-released 路径未保护）。

### 修复（`KeyDisplay.Widget/Widget1.xaml.cs`）

- `Key_PointerReleased`：先用 `var key = _dragKey;` 存局部变量，后续都用 `key`；
- `Key_PointerMoved` 拖拽分支同样先存局部变量；
- 0.3.0 曾因此**误判**"CoreWindow.PointerCursor 沙箱内赋值引发闪退"而把光标赋值整体移除；
  实际根因是上述 NRE。0.3.1 已安全重新启用 `CoreWindow.PointerCursor`（全程 try/catch）。

### 验证

- 拖拽/锁定/持久化/重置全流程通过；0.3.0 已发布并归档 `release/0.3.0-beta/`。

---

## 25. 光标悬停反馈的验证难点：注入式鼠标移动不触发 UWP PointerMoved（2026-08-17，进行中）

### 背景

0.3.1 目标：悬停按键边缘显示系统"拉放窗口"光标（SizeWestEast 等）。实现用
`CoreWindow.GetForCurrentThread().PointerCursor`（元素级 `InputCursor`/`ProtectedCursor`
在当前工程元数据不可见，CS1061，放弃）。

### 自动化验证遇到的坑（重要，写自动化脚本前必读）

1. **注入式移动不触发 hover**：`SetCursorPos` / `SendInput` 相对移动 / `mouse_event` ABSOLUTE
   移动，都不会让 UWP 产生 `PointerMoved`（所以 hover 高亮与光标都不响应）。
   只有**真人真实鼠标移动**能触发 hover。注入点击（`SetCursorPos` + SendInput 相对 down/up）
   可稳定触发 Tapped（已用齿轮/锁定开关验证），但那是点击不是 hover。
2. **读全局光标用 `GetCursorInfo`（hCursor），不是 `GetCursor()`**：
   `GetCursor()` 返回当前线程光标，PowerShell 读永远是线程默认值（误导）；`GetCursorInfo`
   返回鼠标所在位置的全局光标。标准句柄对照：ARROW=65539、IBeam=65541、SIZENWSE=65549、
   SIZENESW=65551、SIZEWE=65553、SIZENS=65555。
3. **像素采样验证 hover 高亮**：用纯 P/Invoke `GetPixel(GetDC(0), x, y)` 采样按键边框像素
   （System.Drawing 在 Add-Type C# 里编译引用会失败，改用纯 P/Invoke 或 PowerShell 层）。

### 当前状态

- ✅ hover 触发已确认：真人悬停 Q 键边缘，diag.txt 出现 `cursor set mode=r cw=True` +
  `cursor applied ok`，l/r/t/b 等模式均触发、赋值无异常；
- ⬜ **未完成（需用户真人操作）**：最终确认悬停时全局光标实际显示为 Size 光标
  （需真人悬停时读 GetCursorInfo；注入式移动不触发 UWP hover，见本节坑 1）；
- ✅ **已完成**：清理 `ApplyCursor` 中 3 处临时 DiagLog（2026-08-17 收尾任务，保留 try/catch 静默降级）；
- ✅ **已完成**：git commit + 发布 0.3.1 beta（三处版本号同步 + 重建 Setup + 归档 `release/0.3.1-beta/`）。

> 完整交接与坐标信息见 `docs/HANDOFF.md` 第 14 节。
