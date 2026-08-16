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
