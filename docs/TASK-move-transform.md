# 任务书：移动挤压修复（移动改用渲染变换 TranslateTransform）—— 0.5.x 修复

> 性质：bug 修复（UWP C# 小组件 `KeyDisplay.Widget`）
> 版本归属：0.5.x（修复）待定，发布时定号
> 执行者：Agent（free 模型；总管集成核对）
> ⚠️ 用户强调：**这次改动可能困难一点，务必做好自检，绝不能影响其他功能产生新 bug。**

---

## 0. 背景与根因
按键都放在 **StackPanel（流式布局）** 里，当前移动按键通过改 `Margin.Left/Top` 实现 → 流式布局中 margin 变大会**挤压后续兄弟元素**，表现为"移动一个按键时，后面的按键跟着动"。
**修复目标**：移动改用 `TranslateTransform`（渲染变换，不影响布局流），其他按键保持不动。

## 1. 方案要点
- **移动（_moveKey 模式）**：位置用 `border.RenderTransform = new TranslateTransform(tx, ty)` 表达，`Margin` 保持布局天然位置不变。
- **缩放（_dragKey 模式）**：**保持现状不变**（仍 Width/Height + Margin；缩放挤压兄弟的问题本轮不扩大范围处理）。缩放与移动通过 transform/margin 各自独立表达视觉位置，不冲突。
- **吸附**：不变（吸附修正改作用于 transform.tx/ty）。
- **持久化兼容**：位置统一存"相对静态布局位置的偏移"，移动写 transform 值；老数据（原 margin 偏移）数值等价于 transform 偏移，可直接兼容。

## 2. 详细改动点（对照现有代码）

### 2.1 移动拖动（Key_PointerMoved 的 _moveKey 分支，约 L1344-1371）
- 新增字段：`_moveStartTX/_moveStartTY`（进入移动时的 transform 偏移起点）。
- 拖动增量 `tx = _moveStartTX + dx; ty = _moveStartTY + dy;`（原 `ml = _moveStartML + dx` 改）。
- **吸附修正**：命中后 `tx += hitH.Delta; ty += hitV.Delta;`（原 ml/mt 改）。
- **四边反推（吸附用）**：`ea[0] = _moveBaseLeft + (tx - _moveStartTX);`（原 `(ml - _moveStartML)` 改），其余三边同理。
- **落位应用**：`key.RenderTransform = new TranslateTransform(tx, ty);`（不再写 Margin）。

### 2.2 进入移动（Key_PointerPressed / LongPress_Tick，约 L1130/L1162 附近）
- 记录 `_moveStartTX/_moveStartTY` = 当前 `((TranslateTransform)key.RenderTransform)?.X/Y ?? 0`。
- `_moveStartML/_moveStartMT` 相关仍在缩放用，勿混；移动不再改它们。
- 用户拍板逻辑不变（长按 200ms、15px 阈值、锁定拦截）。

### 2.3 移动落位（Key_PointerReleased 的 _moveKey 分支，约 L1428-1453）
- 持久化改为写 transform：默认键 `SaveKeyLayout`、自定义键 `CustomPos_<名>`、鼠标垫 `SavePadCustom` 都要把"位置"写为 transform.tx/ty（语义=相对静态位置的偏移，与原 margin 偏移等价）。

### 2.4 持久化格式（关键，兼容老数据）
- `SaveKeyLayout(name, b)`（L1852）：现 `w;h;ml;mt` → 改为 `w;h;tx;ty`（**ml/mt 不再用，位置=transform 值**）。老 4 段数据可直接解读为 transform 偏移（数值等价），无需迁移。
  - 但注意：缩放后的 Margin（l/t 边缩放补偿）当前也占 ml/mt——**缩放保持现状（仍改 Width/Height + Margin），SaveKeyLayout 若存的是缩放后的 margin，与该新的"移动用 transform"值冲突**。彻底规避：`SaveKeyLayout` 位置统一取 `(TranslateTransform)RenderTransform` 的 tx/ty（若 null 视为 0），**不再写 margin**；宽度/高度仍写 b.Width/Height。缩放导致的 margin 变化不持久化位置（因为视觉位置=布局margin+transform，布局margin 变化会改变视觉位置——**所以缩放落位时若动了 ml/mt，需同时把等价偏移并入 transform 并把 margin 归零**，见 2.5）。
- `RestoreKeyLayout`（L1865）：读 `w;h;tx;ty` → 设 Width/Height + `RenderTransform=TranslateTransform(tx,ty)` + **Margin 归零**（不还原 margin）。
- `AddCustomKey` 恢复 `CustomPos_`（L902-916）：写/读 `tx;ty`，应用 transform（不再设 margin，margin 保持 (0,0,6,0) 布局间距）。

### 2.5 缩放落位（Key_PointerReleased 的 _dragKey 分支 + SaveLayout，约 L1472-1480）
- 缩放仍改 Width/Height + Margin（现状）。
- **落位时**：把 `Margin.Left/Top` 与 `(TranslateTransform)RenderTransform` 合并 → `transform.tx = 原tx + Margin.Left; ty = 原ty + Margin.Top; Margin.Left/Top = 0`。这样视觉位置不变（margin+transform 等价归一到 transform），且持久化位置只有一个来源（transform），避免冲突。
- 缩放中对边不动的 ml/mt 补偿逻辑保持现状，只在落位时归一。
- `_padW/_padH` 鼠标垫尺寸同步保持。

### 2.6 鼠标垫（Tag="Pad"）
- 移动：与普通键一致改 transform（PadPos_left/top → 存 transform.tx/ty）。
- 等比缩放：保持 Width/Height（现状），落位按 2.5 归一 margin→transform。
- `RestorePadCustom`（L611）：恢复时应用 transform + Margin 归零。
- 自动跟随（_padCustomized）开关逻辑不变。

### 2.7 主题/锁/重置/触摸板
- `EndMoveStyle`（L391）：移动高亮恢复样式逻辑不变（transform 不回退，只清高亮）。
- 锁定：不进入移动（既有拦截），transform 自然不生效。
- 重置（PerformLayoutReset）：清持久化后，transform 归零（RenderTransform=null）+ Margin 归零 + 默认尺寸。
- 触摸板（_customKeys 里 80×80）：随自定义键一套逻辑自动覆盖，无特殊处理。

## 3. 明确"不动"（防新 bug 红线）
- 缩放自由逻辑与吸附（ApplyDragSnap/Snap*Edge）、全局参考线吸附算法——**原样不碰**。
- 协议 v3、快照渲染、主题配色、锁定拦截、Game Bar 扩展——不碰。
- 移动的交互手感（200ms 长按、15px 阈值、滞回 8/10px）——不碰。

## 4. 自检（必做，逐项核对）
改动完成后：
1. MSBuild Release x64 编译通过（无 error）。
2. **移动不挤压**：键盘区/鼠标区/自定义区各移一个键，观察其他键是否纹丝不动。
3. **吸附正常**：移动时全局参考线吸附（隔空对齐/贴边/距离分级参考线）照常生效。
4. **缩放不回归**：边缘/四角缩放正常、右缘/左缘不动、最小尺寸保护；缩放落位后位置正确（2.5 归一正确）。
5. **持久化/恢复**：移动+缩放后杀进程重启，位置/尺寸完全恢复；**老版本（0.4.x）保存的布局数据加载正常**（4 段格式兼容）。
6. **鼠标垫**：移动/等比缩放正常、Pad 持久化恢复、自动跟随开关、重置恢复默认。
7. **重置**：重置键布局后 transform 归零、布局恢复初始。
8. **锁定态**：锁定时移动/缩放无效。
9. **触摸板**：能添加/移动/缩放/删除。
10. **主题切换**：移动过的键切换主题后高亮/样式正常。
11. 用 `TransformToVisual` 的吸附坐标在移动后仍准确（被拖键四边视觉位置正确，参考线贴齐正确）。

## 5. 汇报要求
1. 改动函数清单（行号）；
2. transform 移动 + 吸附修正 + 四边反推的实现说明；
3. 持久化格式（w;h;tx;ty）与老数据兼容说明；
4. 缩放落位归一（margin→transform）说明；
5. 自检 11 项逐项结论；
6. 编译结果；
7. 给用户的验收指引。
