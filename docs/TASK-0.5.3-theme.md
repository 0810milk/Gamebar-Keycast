# 任务书 0.5.3（二）：三色主题轮转（含粉色）+ 按键透明度滑条

> 性质：界面/主题功能开发（UWP C# 小组件 `KeyDisplay.Widget`）
> 版本归属：0.5.3 beta（与「关于面板」同版本，一起攒着发布）
> 执行者：Agent（free 模型；总管集成核对）
> 用户已拍板：粉色用黑色字体；字体统一黑白两档（深色主题白字、浅色主题黑字）；透明度默认 100%、范围 10%~100%、无数值标签、线性；锁定逻辑如下。

---

## 0. 需求总览（两项）

### A. 主题三色轮转 + 粉色主题
- 主题按钮点击**轮转**：黑（dark）→ 白（light）→ **粉（pink）** → 黑，循环。
- 按钮文字显示"下一色"（沿用现状语义：dark 时显示「白」、light 显示「粉」、pink 显示「黑」）。
- **粉色主题配色（用户拍板，字体黑色）**：
  - 面板背景：浅粉半透明 `#B3FFB0C4`
  - 面板/按键边框：深粉 `#CCB0577E`
  - 按键默认背景：粉白 `#FFFFCDD8`
  - **默认文字：黑色**（字体只有黑白两档：深色主题=白字，浅色主题=黑字；粉色属浅色→黑字）
  - **按下反馈：白底 + 黑字**（用户指定的专属按下色）
  - 鼠标垫：浅粉半透明 `#4DFFB0C4`；鼠标点：黑色
- **扩展性要求（用户明确未来会加更多颜色）**：主题色做成**数据驱动**——新增主题色只加一组配色值 + 轮转列表加一项，不改散落的 if/else 逻辑。

### B. 按键透明度滑条
- **位置**：设置菜单 `SettingsPanel` 里，**主题行下方**（主题行与鼠标垫行之间）。
- **控件**：`Slider`（横向、线性、无数值标签），Minimum=10、Maximum=100、StepFrequency=1（或连续）、Value 默认 100。
- **作用**：调整**游玩状态下按键的透明度**（`_keys`/`_mouse`/`_customKeys` 全部 Border + `MousePad`；菜单/面板/参考线不受影响）。实现用 `Border.Opacity`。
- **锁定逻辑（用户确认，核心，别写错）**：
  | 状态 | 按键透明度 |
  |---|---|
  | 锁定**开启**（游玩中） | = 滑条设定值（如 70%） |
  | 锁定**关闭**（编辑布局中） | **临时强制 100%** |
  - **关键 bug 预防（用户点名）**：透明度 70% 状态进设置关掉锁定 → 按键临时 100%（正常）→ **退出/重新开启锁定必须回到 70%**，绝不能残留 100%。实现上：设定值单独存变量，任何应用点都"从设定值计算"，而不是"从当前 Opacity 计算"。
- **持久化**：`KeyOpacity_`（int，10~100，默认 100），启动恢复；按当前锁定状态应用。

---

## 1. 架构设计（总管指定，照此实现）

### 1.1 主题状态三态化
- `_dark`（bool）→ 改为三态主题标识。推荐 `private string _theme = "dark";`（"dark"/"light"/"pink"）。
- 持久化键沿用 `Theme`（字符串 "dark"/"light"/"pink"）；老数据只有 dark/light 自动兼容；缺失默认 dark。
- 主题按钮 `SettingsTheme_Click`：轮转 dark→light→pink→dark，写 `Theme`，调 `ApplyTheme()`。
- 按钮文字：dark→「白」、light→「粉」、pink→「黑」（`SettingsThemeText`）。

### 1.2 集中配色表（数据驱动，扩展性）
- 定义主题画笔组（字段或局部方法）。每组包含：
  `Panel`（面板背景）、`Border`（边框）、`KeyBg`（按键默认背景）、`KeyFg`（默认文字）、`PressedBg`（按下背景）、`PressedFg`（按下文字）、`Pad`（鼠标垫背景）、`Dot`（鼠标点）。
- 三组值：
  | 画笔 | dark | light | pink |
  |---|---|---|---|
  | Panel | `#B3000000` | `#B3FFFFFF` | `#B3FFB0C4` |
  | Border | `#66FFFFFF` | `#66000000` | `#CCB0577E` |
  | KeyBg | Black | White | `#FFFFCDD8` |
  | KeyFg | White | Black | **Black** |
  | PressedBg | White | Black | **White** |
  | PressedFg | Black | White | **Black** |
  | Pad | `#4D000000` | `#4DFFFFFF`（沿用现有 _lightPad 值） | `#4DFFB0C4` |
  | Dot | White | Black | **Black** |
- **改造方式**：现有 86 处 `_dark ? A : B` 三元全部改为 `GetThemeBrush(...)` 或按 `_theme` 分支返回对应画刷。可用辅助方法：
  ```csharp
  private Brush P(Brush dark, Brush light, Brush pink) => _theme == "dark" ? dark : _theme == "pink" ? pink : light;
  ```
  或按语义分组（PanelB/BorderB/KeyBgB/...）。**优先把散落三元替换为语义化查询**，便于未来加色。
- 文字只有黑白两档：`IsLightTheme = _theme != "dark"`（light/pink 都算浅色→黑字）。

### 1.3 按下反馈规则
- `SetKey`（约 L371）：dark 按下=白底黑字；light 按下=黑底白字；**pink 按下=白底黑字**（即 PressedBg/PressedFg 表驱动，pink 的 PressedBg=White 已覆盖）。

### 1.4 透明度滑条实现
- XAML：`Slider x:Name="OpacitySlider" Minimum="10" Maximum="100" Value="100"` + 行标签「透明度」（标签只写"透明度"三字，**不显示数值**）。`ValueChanged="OpacitySlider_Changed"`。
- 字段：`private double _keyOpacity = 100.0;`（设定值，默认 100）。
- 核心方法：
  ```csharp
  private void ApplyKeyOpacity()
  {
      double target = _layoutLocked ? _keyOpacity / 100.0 : 1.0;   // 锁定开=设定值；锁定关=临时100%
      foreach (var kv in _keys) kv.Value.Opacity = target;
      foreach (var kv in _mouse) kv.Value.Opacity = target;
      foreach (var kv in _customKeys) kv.Value.Opacity = target;
      if (MousePad != null) MousePad.Opacity = target;
  }
  ```
- `OpacitySlider_Changed`：`_keyOpacity = e.NewValue`；写 `KeyOpacity_`（int）；`ApplyKeyOpacity()`。
- **锁定切换处（`LockSwitch_Click`）**：锁定状态翻转后调 `ApplyKeyOpacity()`——这是"退出编辑恢复设定值"的关键：锁定重新开启时 target 从 `_keyOpacity` 算（70% 回来），不会残留 100%。
- **启动恢复**：读 `KeyOpacity_`（默认 100）→ 设 `_keyOpacity` 与 `OpacitySlider.Value` → `ApplyKeyOpacity()`。
- 注意：被删的内置键（不在字典）不受影响；`ApplyKeyOpacity` 用 foreach 天然跳过。菜单/关于面板/参考线**不要**设 Opacity。
- 滑块配色随主题（背景/前景用主题画刷）。

---

## 2. 不动（红线）
- 吸附（全局参考线/分级）、移动 RenderTransform、缩放、持久化格式（w;h;tx;ty / CustomPos_ / PadCustom_ / PadVisible_）、协议 v3、内置键删除/重置、关于面板（0.5.3 已实现的部分）——全部不碰。
- 现有黑/白主题的**视觉表现与数值**保持不变（只改代码结构，不改暗/亮配色值）。

## 3. 自检（逐项）
1. MSBuild Release x64 编译通过（无 error）。
2. 主题按钮轮转：黑→白→粉→黑循环；按钮文字正确（白/粉/黑）；重启记住当前主题（dark/light/pink 持久化）。
3. 粉色主题：面板/按键/边框粉色系；文字黑色；**按键按下=白底黑字**。
4. 黑/白主题视觉与改动前完全一致（截图对比或逐项核对）。
5. 透明度滑条：在主题下方；范围 10~100；无数值标注；线性。
6. 锁定**开启**：按键透明度 = 滑条值（如 70%）。
7. 锁定**关闭**：按键临时 100%。
8. **核心 bug 场景**：设 70% → 进设置关锁定 → 按键 100% → 退出设置/重新开锁定 → **按键回到 70%**（不残留 100%）。
9. 透明度重启保持；默认 100%。
10. 老用户数据兼容：Theme 只有 dark/light 的老数据正常；无 KeyOpacity_ 时默认 100%。
11. 菜单/关于面板/参考线透明度不受影响；原有功能（移动/缩放/吸附/删除/重置）不回归。

## 4. 汇报要求
1. 改动文件/函数清单（行号）；
2. 三态主题状态机与配色表结构（说明如何加第四种颜色）；
3. 86 处三元的改造方式（语义查询方法签名）；
4. 透明度滑条实现（XAML + ApplyKeyOpacity + 锁定切换钩子 + 启动恢复）；
5. 自检 11 项逐项结论；
6. 编译结果；
7. 给用户的验收指引。
