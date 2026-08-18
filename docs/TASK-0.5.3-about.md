# 任务书 0.5.3：设置菜单 ⓘ 信息面板（关于页）

> 性质：界面功能开发（UWP C# 小组件 `KeyDisplay.Widget`）
> 版本归属：0.5.3 beta（待发布定号）
> 执行者：Agent（free 模型；总管集成核对）
> 用户已确认全部决策：标题「关于」、GitHub 可点击打开浏览器、QQ 群号显示文本（点击跳 QQ 群链接，链接本身不显示）。

---

## 一、需求（用户确认版）

### 1. ⓘ 按钮（设置菜单右上角）
- 位置：`SettingsMenu`（设置子菜单）**右上角**，与「设置」标题（`SettingsTitle`）**同一行平行**。
- 实现：把标题行改成 Grid——左侧 `SettingsTitle`「设置」，右侧新增 ⓘ 按钮（`SettingsInfoBtn`）。
- 样式：小圆角 Border 按钮（无边框或细边框），内部 TextBlock 显示「ⓘ」（U+24D8 `&#x24D8;`，FontSize 约 14），配色随主题。

### 2. 点击 ⓘ → 「关于」子面板
- 模式：与 `LockPanel`（自定义控件二级菜单）相同的**覆盖层模式**——新增 `AboutPanel`（Grid 遮罩层 `Background="#40000000"`，`Grid.RowSpan="2"`，`Tapped="AboutPanel_Tapped"` 点遮罩收起）+ `AboutMenu`（Border 圆角面板，`Tapped="AboutMenu_Tapped"` 不冒泡）。
- 面板标题：「**关于**」。
- 面板内容（从上到下）：
  1. **圆形头像**：`Assets/Avatar.jpg`（已复制进项目 Assets，12KB）。显示为圆形（Ellipse + ImageBrush 或 Image 裁剪，直径约 48~56px，居中）。
  2. **作者**：恐龙milk（居中，FontSize 13~14）。
  3. **GitHub 行**：显示文本 `https://github.com/0810milk/Gamebar-Keycast`（FontSize 11~12，可换行/省略）；**点击整行 → 用 `Windows.System.Launcher.LaunchUriAsync(new Uri(...))` 打开浏览器**，必须 try/catch（Game Bar 沙箱可能拦截，失败静默降级为仅显示，不崩溃）。
  4. **QQ 反馈群行**：显示文本「QQ 反馈群：2152061189」；**点击整行 → Launcher 打开 QQ 群快捷链接**（链接**不显示**在界面上）：`https://qun.qq.com/universal-share/share?ac=1&authKey=O`（用户提供原样；同样 try/catch 失败静默）。
- 两行可点击项加 `Tapped` 处理器（`GitHubRow_Tapped` / `QqRow_Tapped`）；视觉上加浅色区分（如下划线或文字颜色提示可点击）。

### 3. 主题适配
- `AboutPanel/AboutMenu/AboutTitle` 及各行文字、ⓘ 按钮配色随 `_dark` 主题切换（并入 `ApplySettingsColors`/`ApplyTheme` 刷新，与其他菜单一致：面板背景 `_darkPanel/_lightPanel`、边框 `_darkBorder/_lightBorder`、文字 `_darkDefaultFg/_lightDefaultFg`）。

### 4. 关闭逻辑
- 点遮罩（`AboutPanel_Tapped`，判断 `e.OriginalSource == AboutPanel` 才收起）→ `AboutPanel.Visibility = Collapsed`。
- 面板内可加右上角「✕」关闭按钮（可选，遮罩已够；加则更顺手）。
- 打开 ⓘ 时若设置菜单收起（点遮罩）→ 关于面板也应一并收起（在 `SettingsPanel_Tapped` 收起处加 `AboutPanel.Visibility=Collapsed`）。

## 二、不动（红线）
- 设置按钮/设置菜单现有内容与逻辑、鼠标垫显隐开关、自定义控件菜单、移动/缩放/吸附、持久化、协议 v3、内置键删除/重置——全部不碰。
- 头像文件只读打包进应用（Content），不改动其他 Assets。

## 三、自检（逐项）
1. MSBuild Release x64 编译通过（无 error；确认 `Avatar.jpg` 被作为 Content 打进 msix——UWP SDK 项目 Assets 默认自动包含，可 `Unzip` msix 或检查 `Get-AppxPackageManifest` 验证）。
2. 设置菜单右上角 ⓘ 与「设置」标题同行显示。
3. 点击 ⓘ → 「关于」面板弹出：圆形头像可见、作者「恐龙milk」、GitHub 地址、QQ 反馈群号。
4. 点遮罩 → 关于面板收起；设置菜单收起时关于面板也收起。
5. 暗/亮主题切换后，关于面板与 ⓘ 配色正确。
6. 点击 GitHub 行 → 尝试打开浏览器（成功最好；沙箱拦截则无反应但不崩溃）。
7. 点击 QQ 群行 → 尝试打开 QQ 群链接（同上，失败静默）。
8. 原有功能不回归：设置菜单其它项、自定义控件菜单、移动/缩放/吸附、鼠标垫开关。
9. 界面显示的是群号「2152061189」，**不显示** QQ 链接本身。

## 四、汇报要求
1. 改动文件/函数清单（行号）；
2. ⓘ 按钮与关于面板 XAML 结构；
3. 头像显示方式（圆形裁剪实现）；
4. 两个可点击行的 Launcher 实现与失败降级说明；
5. 主题适配改动点；
6. 自检 9 项逐项结论；
7. 编译结果（含 Avatar.jpg 打包确认）；
8. 给用户的验收指引。
