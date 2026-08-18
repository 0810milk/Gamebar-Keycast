# 任务书 0.5.2：设置按钮改按钮 + 鼠标垫显隐开关 + 删触摸板模板 + 自带键可删除

> 性质：功能/界面开发（UWP C# 小组件 `KeyDisplay.Widget`）
> 版本归属：0.5.2 beta（待发布定号）
> 执行者：Agent（free 模型；总管集成核对）
> 背景：该项目自 0.5.1 发布后用户提出的下一批需求。四个改动相互独立，逐个实现并自检。

---

## 一、需求清单（4 项，全部用户确认）

### 1. 设置按钮：齿轮 → 正常按钮「设置」
- **现状**：底部工具条右下角是齿轮图标 ⚙（`SettingsBtnIcon`，XAML 里 `&#x2699;`，`SettingsBtn` 圆角壳 `Tapped="Settings_Click"`）。
- **改为**：`SettingsBtn` 内部不再放齿轮字符，改为**正常按钮外观 + 文字「设置」**：
  - 保留 `SettingsBtn` 这个圆角 Border 壳（含 `Tapped="Settings_Click"`），里面放 `TextBlock Text="设置"`（FontSize 12~13，居中，前景色随主题 `_dark` 切换——见 `ApplySettingsColors` 中 `SettingsBtnIcon.Foreground`，同步改为 `SettingsBtnText.Foreground`）。
  - 若壳本身不够"按钮感"，可增补底色/边框强调（与「自定义控件」按钮同风格）。
- **使用逻辑完全不变**：点击 → 弹设置子菜单；位置固定右下角；显示时机不变；按钮固定、不参与移动/锁定。
- 注意：`ApplySettingsColors`（约 L568 `SettingsBtnIcon.Foreground = ...`）要同步指向新 TextBlock。

### 2. 鼠标垫「显示/隐藏」开关（新增）
- **位置**：设置子菜单 `SettingsPanel`，在「主题」行下方新增一行：「鼠标垫」开关。
- **界面**：参考主题行结构 —— `SettingsPadLabel`（Text="鼠标垫"）+ `SettingsPadBtn`（圆角 Border 壳，`Tapped="PadToggle_Click"`）+ `SettingsPadText`（显示状态文字，如「显示/隐藏」或「开/关」，随状态切换 + 反色）。
- **行为**：
  - 默认**显示**（`PadVisible_=1`）。
  - 点击 → 切 `MousePad.Visibility`（Visible↔Collapsed），并写 `PadVisible_`（1/0）到 `ApplicationData.Current.LocalSettings`。
  - 启动/OnLoaded 时读 `PadVisible_`：为 0 → `MousePad.Visibility=Collapsed` + 开关显示隐藏态；否则显示态。
- **性质**：仅「显示/隐藏」临时切换（Visibility），**不是删除**——`_padW/_padH`、`PadCustom_`（transform/尺寸持久化）全部保留，重新显示原样恢复位置尺寸。不影响其他控件移动/缩放/吸附。
- **范围**：只对鼠标垫（`MousePad`）生效，其他按键不做此开关。
- **配色**：开关文字/颜色随 `_dark` 主题 + 显隐状态反色（跟随 `ApplySettingsColors` 一并刷新）。

### 3. 删「触摸板」添加模板
- **删除**：`KeyPickerScroll`（87 键布局）里 v3 加的 `Tag="触摸板"` 的 Border 入口行（含注释行）。
- **清理**：`AddCustomKey` 中 `bool isPad = name=="触摸板"` 的 80×80 特判分支可一并移除（入口已删不会触发；如保留也无害，但建议清掉避免死代码）。
- 保留触摸板自定义键的持久化读取兼容（`Custom_触摸板`/`CustomPos_触摸板` 若已存在，加载时自动恢复为普通键即可，无需特殊处理）。

### 4. 自带键可删除
- **现状**：右键删除仅对自定义键生效（删除判断限 `_customKeys`），内置键（`_keys`/`_mouse`：QWER/ASDF/Shift/Ctrl/Alt/空格/鼠标键）不可删（测试期保护）。
- **改为**：放开——**内置键也可右键删除**（走同一套删除确认面板）。
  - `Key_PointerPressed` 右键删除分支：去掉"仅 `_customKeys`"限制，`_keys`/`_mouse` 里的键也允许进入删除确认。
  - 删除内置键的实现：从对应字典（`_keys`/`_mouse`/`_customKeys`）移除 + 清理持久化 + `Visibility=Collapsed`（不销毁对象，便于重置恢复）。
    - `_keys`/`_mouse` 删除：`Remove("Layout_"+name)` 持久化 + 字典移除 + `b.Visibility=Collapsed`。
    - `_customKeys` 删除：现有逻辑（`Custom_`/`CustomPos_` 清理 + 面板移除）。
- **兜底**：`PerformLayoutReset`（重置按键布局）恢复**全部默认键**（含被删内置键）到刚安装初始状态：
  - 被删内置键：恢复 `Visibility=Visible` + 重新加回 `_keys`/`_mouse` + 恢复默认布局（现有 ResetKeyLayout 逻辑扩展：当前循环所有默认键恢复；需确保被删的键也回来）。
  - `RestoreLayout`/`SaveLayout` 循环要注意：被删的键不在字典里，重置时要重新登记。

> ⚠️ 实现要点：内置键删除/重置涉及 `_keys`（12 键？含 QWER/ASDF/修饰/空格）与 `_mouse`（L/MR/M/X1/X2）两个字典。建议删除时记录"已删默认键"（如持久化 `Deleted_<name>=1` 或仅靠字典缺失+Collapsed 判断），重置时统一恢复。保持简单、不破坏协议与渲染循环（`OnRendering` 对 `_keys`/`_mouse` 的 foreach 要能容忍被删键）。

## 二、不动（红线）
吸附（全局参考线/分级）、缩放、移动（RenderTransform）、持久化 `w;h;tx;ty`、协议 v3、锁定逻辑、主题配色体系、Game Bar 扩展激活——**全部不碰**。
鼠标垫等比缩放/自动跟随开关/缩放落位归一——不动。

## 三、自检（必做，逐项）
1. MSBuild Release x64 编译通过（无 error）。
2. 设置按钮：显示「设置」二字、无齿轮；点击弹设置菜单，位置/行为与旧完全一致；主题切换时文字颜色跟随。
3. 鼠标垫开关：默认显示态；点 → 隐藏（Collapsed）+ 反色；再点 → 恢复显示且位置/尺寸/transform 不变；重启保持状态。
4. 鼠标垫隐藏时：其他控件移动/缩放/吸附/锁定仍正常。
5. 添加按键布局无「触摸板」入口。
6. 内置键可右键删除；删除后其他键/渲染/吸附不崩；点「重置按键布局」→ 全部默认键恢复刚安装状态（含被删的）。
7. 重置后：自定义键全清、鼠标垫恢复默认、主题不动。
8. 原功能不回归：吸附、缩放、移动、锁定、主题、协议、持久化。
9. AddCustomKey 移除 isPad 分支后，普通键宽度/尺寸正常。

## 四、汇报要求
1. 改动函数/字段清单（行号）；
2. 设置按钮实现（XAML 改动 + 配色联动）；
3. 鼠标垫开关实现（Visibility 切换、PadVisible_ 持久化、启动恢复、反色）；
4. 删触摸板模板 + isPad 清理；
5. 内置键删除/重置恢复实现（含字典处理、持久化、渲染循环兼容性说明）；
6. 自检 9 项逐项结论；
7. 编译结果；
8. 给用户的验收指引。
