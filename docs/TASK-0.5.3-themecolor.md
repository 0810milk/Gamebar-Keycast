# 任务书 0.5.3（三）：主题颜色子菜单（自定义主题色）

> 性质：界面/主题功能开发（UWP C# 小组件 `KeyDisplay.Widget`）
> 版本归属：0.5.3 beta（与关于面板、三色主题、透明度滑条同版本，一起发布）
> 执行者：Agent（free 模型；总管集成核对）
> 用户已确认最终版提示词（预设联动 / 实时同步 / 16 色块堆叠 / 文字槽位手动）。本任务书为唯一实现依据。

---

## 0. 需求总览

设置菜单「主题」行右侧新增「自定义」按钮 → 点击弹出二级子菜单「**主题颜色**」：
- 8 行调色目标（面板/边框/按键底/文字/按下底/按下字/鼠标垫/鼠标点），每行 = 目标名 | hex 输入框 | 「颜色盘」按钮
- 底部调色区：点某行「颜色盘」展开——「正在调：X」+ 方形 HSV 调色盘（色相方块+色相滑条）+ 下方常用 16 色快捷色块（**4×4 堆叠**）
- 三方实时同步：hex 输入框 ↔ 调色盘 ↔ 组件实际颜色
- 预设联动：黑/白/粉预设下打开子菜单，8 槽位显示**当前预设**的值；用户改任意槽位 → 固化全部 8 值 + 主题切 custom + 立即生效
- 自定义值持久化，切回预设不丢

---

## 1. 数据模型与主题状态

### 1.1 主题四态
- `_theme` 字符串扩展为四态：`"dark"/"light"/"pink"/"custom"`（持久化键 Theme 读入时兼容：`ts == "light" || ts == "pink" || ts == "custom"` 才接受，否则默认 "dark"）。
- 轮转按钮（`SettingsTheme_Click`）：仍在 dark→light→pink→dark 间循环，**不经过 custom**。
- 「自定义」按钮（新 `CustomTheme_Click`）：`_theme = "custom"` → 刷新 8 个动态画刷 → `ApplyTheme()` → 打开「主题颜色」子菜单。

### 1.2 8 槽位（关键：固化方案）
- 持久化键（8 个，存 `"#RRGGBB"` 字符串）：`CustomPanel_ / CustomBorder_ / CustomKeyBg_ / CustomKeyFg_ / CustomPressedBg_ / CustomPressedFg_ / CustomPad_ / CustomDot_`
- **固化规则（用户拍板的预设联动）**：用户修改任意槽位时，**先把当前显示中的 8 个值全部写入 8 个 Custom_ 键**，再把被修改槽位的新值写入，然后 `_theme="custom"` + 刷新画刷 + `ApplyTheme()`。
  - 效果：黑预设下打开显示黑预设 8 值；改一个 → 8 值整体固化保存，其余 7 个也定格为黑预设值；切回预设再回来，自定义值完整不丢。
- 槽位显示值解析：`_theme == "custom"` → 读 Custom_ 键（缺省回落到 dark 预设对应值）；否则 → 当前预设（dark/light/pink）对应值。
- **启动**：读 8 个 Custom_ 键 → 若存在（至少一个）且 `_theme=="custom"`，按 custom 刷新画刷；`_theme` 非 custom 时忽略（预设值直接来自内置表）。

### 1.3 预设色表（内置，三套 8 值）
| 槽位 | dark | light | pink |
|---|---|---|---|
| 面板 | #B3000000 | #B3FFFFFF | #B3FFB0C4 |
| 边框 | #66FFFFFF | #66000000 | #CCB0577E |
| 按键底 | #FF000000 | #FFFFFFFF | #FFFFCDD8 |
| 文字 | #FFFFFFFF | #FF000000 | #FF000000 |
| 按下底 | #FFFFFFFF | #FF000000 | #FFFFFFFF |
| 按下字 | #FF000000 | #FFFFFFFF | #FF000000 |
| 鼠标垫 | #4D000000 | #4DFFFFFF | #4DFFB0C4 |
| 鼠标点 | #FFFFFFFF | #FF000000 | #FF000000 |

> 与现有画刷字段值一一对应（_darkPanel/_darkBorder/_darkDefaultBg/_darkDefaultFg/_darkPressedBg/_darkPressedFg/_darkPad/_darkDot 等，注意实际字段名以代码为准，从现有字段取值，不要自己发明值）。

### 1.4 动态画刷（custom 态生效）
- 新增 8 个字段：`_customPanelB/_customBorderB/_customKeyBgB/_customKeyFgB/_customPressedBgB/_customPressedFgB/_customPadB/_customDotB`（SolidColorBrush，启动时用 dark 预设值初始化）。
- 新方法 `RefreshCustomBrushes()`：读 8 个 Custom_ 键（`#RRGGBB` 解析，非法/缺省用当前对应预设值）→ 赋给 8 个动态画刷。
- 语义方法改造：`PanelB()` 等 8 个方法开头加：`if (_theme == "custom") return _customPanelB;`（其余按现有 dark/light/pink 分支）。
- **custom 态的文字黑白规则**：不自动判断，**文字槽位完全手动**（用户已拍板）。若用户没调文字槽位，文字值 = 固化时的预设文字值。

### 1.5 hex 解析工具
- `Color ParseHex(string s)`：接受 `#RRGGBB`（大小写均可）；非法返回 null。
- `string ToHex(Color c)`：转 `#RRGGBB`。

---

## 2. UI 实现

### 2.1 入口（Widget1.xaml）
- 主题行（`SettingsThemeLabel`/`SettingsThemeBtn` 所在 Grid）右侧、主题按钮**左边**或**右边**加「自定义」按钮（用户说"主题两个字右边"，主题轮转按钮旁）：`Border x:Name="SettingsCustomBtn" Width="52" Height="22" CornerRadius="11" BorderThickness="1" HorizontalAlignment="Right" Margin="0,0,52,0"`（若放主题按钮左侧则 Right Margin 让位），内含 TextBlock「自定义」，Tapped=`CustomTheme_Click`。配色并入 `ApplySettingsColors`（与 SettingsThemeBtn 同风格，用 InvertKeyBgB/InvertKeyFgB 或主题画刷）。

### 2.2 主题颜色子菜单（覆盖层，同 AboutPanel 模式）
- XAML：`Grid x:Name="ThemeColorPanel"`（遮罩 `Background="#40000000"`，`Grid.RowSpan="2"`，`Tapped="ThemeColorPanel_Tapped"` 仅 OriginalSource==自身收起）+ `Border x:Name="ThemeColorMenu" Width="320" CornerRadius="8" BorderThickness="1"`（`Tapped` 标记 Handled 防冒泡），水平/垂直居中或右下（与 LockMenu 同风格，建议 HorizontalAlignment=Right、VerticalAlignment=Bottom、Margin 0,0,8,44 保持一致）。
- 内容 StackPanel（Padding 12）：
  - 标题行 Grid：「主题颜色」TextBlock（FontSize 14 SemiBold）+ 右侧「✕」或「关闭」小按钮（可选，遮罩已够）。
  - **8 行调色目标**，每行 Grid（Margin 0,10,0,0）：
    - 左：TextBlock 目标名（宽 ~64，FontSize 12，VerticalAlignment Center）——「面板/边框/按键底/文字/按下底/按下字/鼠标垫/鼠标点」
    - 中：`TextBox x:Name="SlotInput0..7"`（宽 ~100，FontSize 12，Text=`#RRGGBB`，TextChanged 校验）
    - 右：`Border x:Name="SlotPick0..7"`（宽 ~56 高 24 CornerRadius 4 BorderThickness 1，内含 TextBlock「颜色盘」FontSize 11，Tapped 展开调色区并设当前行）
  - **调色区**（`Grid x:Name="PickerArea"` Visibility=Collapsed，Margin 0,12,0,0）：
    - `TextBlock x:Name="PickerTitle"`（"正在调：面板"，FontSize 11）
    - **方形调色盘** `Grid x:Name="SvBox"`（160×160，ClipToBounds）：
      - 底层 Rectangle：水平 LinearGradientBrush（GradientStop 0=White，1=当前 Hue 纯色 `HsvColor(h,1,1)`）
      - 上层 Rectangle：垂直 LinearGradientBrush（0=Transparent，1=Black）
      - PointerPressed/Moved（capture）/Released 事件：X/160=S、1-Y/160=V → 取色
      - 当前位置标记：Ellipse（12×12，Stroke White + 外圈黑或反色描边，Pointer 移动时更新位置）
    - **色相滑条** `Grid x:Name="HueBar"`（160×16，Margin 0,6,0,0）：Rectangle 横向 7 段彩虹渐变（红→黄→绿→青→蓝→品红→红，#FF0000→#FFFF00→#00FF00→#00FFFF→#0000FF→#FF00FF→#FF0000），Pointer 取 X/160 → Hue（0~360）
    - **16 色快捷块**（Margin 0,10,0,0）：Grid 4 行 × 4 列（每行一个 Grid 高 20，列宽均匀），每格 Border（Margin 2，CornerRadius 3，BorderThickness 1，Background=色值，Tapped 设为当前行颜色）。16 个常用色建议：`#000000 #FFFFFF #808080 #C0C0C0 #FF0000 #FF8000 #FFFF00 #80FF00 #00FF00 #00FF80 #00FFFF #0080FF #0000FF #8000FF #FF00FF #FF0080`（黑/白/灰×2/红/橙/黄/黄绿/绿/青绿/青/天蓝/蓝/紫/品红/粉红）
  - 调色区默认 Collapsed；点某行「颜色盘」→ Visible + PickerTitle=「正在调：X」+ 该行当前色同步到盘（Hue/盘指针）+ 记录 `_activeSlot`（0~7）。

### 2.3 交互逻辑（Widget1.xaml.cs）
- `CustomTheme_Click`：`_theme="custom"` → `RefreshCustomBrushes()` → `ApplyTheme()` → `ThemeColorPanel.Visibility=Visible`（并 `ApplySettingsColors()` 刷新子菜单配色）。
- `ThemeColorPanel_Tapped` / `ThemeColorMenu_Tapped`：同 AboutPanel 模式（遮罩收起、面板内 Handled）。
- `SlotPick0..7_Tapped`：`_activeSlot=i` → `PickerTitle.Text="正在调："+名` → `PickerArea.Visibility=Visible` → 用该行当前色初始化 SvBox 渐变与指针（**必须**：盘显示的是当前行的颜色，不是上次的）。
- **取色流程**（SvBox/HueBar Pointer + 一个公共 `ApplyPickedColor(Color c)`）：
  1. `ApplyPickedColor(c)`：hex 文本 `SlotInput[_activeSlot].Text = ToHex(c)`（设标志 `_syncing=true` 防递归）→ `CommitSlotColor(_activeSlot, c)`。
  2. `CommitSlotColor(i, c)`：按 §1.2 固化规则写 8 个 Custom_ 键 → `_theme="custom"` → `RefreshCustomBrushes()` → `ApplyTheme()` → 若调色区开着，同步盘指针/渐变到新色。
- **hex 输入**（`SlotInput0..7_TextChanged`）：
  - `_syncing` 标志跳过（防递归：盘/色块驱动的文本更新不回触发）。
  - 校验 `^#[0-9a-fA-F]{6}$`：合法 → 输入框 BorderBrush 恢复主题边框色 → `CommitSlotColor(i, c)` + 盘同步；非法（非空时）→ 输入框 BorderBrush=Red，不应用。
- **色相/方块 Pointer**：Pressed 捕获指针（CapturePointer），Moved 取坐标算 S/V/H → `ApplyPickedColor(HsvToRgb(h,s,v))`；Released 释放。PointerExited 不丢（用 capture）。取色需有当前位置行（`_activeSlot` 有效且 PickerArea 可见）。
- **HSV↔RGB 转换**：标准算法（H 0~360，S/V 0~1）；无第三方依赖。若自写转换在编译/运行遇到问题，允许搜 GitHub 开源实现套用（**MIT 优先**，注释注明来源）。
- **启动**：§1.2 已述（读 Custom_ 键 + theme=custom 时 RefreshCustomBrushes + ApplyTheme）。
- **子菜单配色**：ThemeColorMenu/标题/目标名/「颜色盘」按钮随主题（并入 `ApplySettingsColors`：PanelB/BorderB/KeyFgB/InvertKeyBgB 风格）；hex 输入框与色块 BorderBrush 用主题边框色。
- **关闭逻辑**：遮罩收起 ThemeColorPanel；`SettingsPanel_Tapped` 收起时同时收起 ThemeColorPanel（同 AboutPanel 处理）。

---

## 3. 不动（红线）
- 吸附/移动 RenderTransform/缩放/持久化格式/协议 v3/内置键删除重置/关于面板/透明度滑条/三色主题轮转——全部不碰。
- 黑/白/粉三套预设的**视觉表现与数值**不变。
- 不引入第三方 NuGet 包（调色盘自写或仅复制开源源码文件，不进包依赖）。

## 4. 自检（逐项，代码级核对）
1. MSBuild Release x64 编译通过（无 error）。
2. 主题行右侧「自定义」按钮 → 打开「主题颜色」子菜单；标题「主题颜色」。
3. 8 行齐全：面板/边框/按键底/文字/按下底/按下字/鼠标垫/鼠标点；每行 目标名|输入框|颜色盘。
4. 黑色预设下打开 → 8 槽位显示黑预设 8 值；切白/粉预设再打开 → 显示对应预设值。
5. 改任意槽位（输入或取色）→ 主题切 custom + 组件颜色立即生效 + 8 值固化持久化；切回预设再开自定义 → 自定义值还在。
6. 输入 hex 合法 → 实时生效 + 调色盘指针/方块同步；非法（如 #GGGGGG、#12345）→ 红框不应用。
7. 调色盘：方形盘拖动取色 → hex 框数字实时同步 + 组件实时变色；色相条拖动 → 方块渐变与取色更新；「正在调：X」正确。
8. 16 色块 4×4 堆叠显示在调色盘下方；点击色块 → 立即设为当前行颜色并同步。
9. 重启保持（主题=custom 时 8 色正确恢复；预设时正常）。
10. 暗/亮主题下子菜单/输入框配色正常；原有功能（轮转/滑条/关于/移动缩放吸附）不回归。

## 5. 汇报要求
1. 改动文件/函数清单（行号）；
2. 8 槽位固化模型与 Custom_ 键结构；
3. HSV 调色盘实现方式（自写 or 套用开源+来源）；
4. 三方实时同步与防递归（_syncing）机制；
5. 16 色块列表与布局；
6. 自检 10 项逐项结论；
7. 编译结果；
8. 给用户的验收指引。