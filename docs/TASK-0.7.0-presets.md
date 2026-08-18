# TASK-0.7.0：用户预设功能（主题预设 / 布局预设）

> 目标版本：0.7.0（UWP 包 1.4.0.0）
> 状态：待开发
> 核心诉求：**新增「主题预设」「布局预设」两组用户预设，且版本更新后用户数据不丢失**

---

## 1. 功能概述

新增 **4 个按钮**、**2 组用户预设**：

| # | 按钮 | 位置 | 点击行为 |
|---|---|---|---|
| 1 | **主题预设**（入口） | **设置菜单**（SettingsMenu）内，「自定义控件」按钮**正下方** | 打开「主题预设」子菜单 |
| 2 | **布局预设**（入口） | 同上，与「主题预设」**同一行并排**（或上下排列，见 5.1） | 打开「布局预设」子菜单 |
| 3 | **添加预设**（动作） | 「主题预设」子菜单内 | 展开输入区：输入预设名 → 保存 |
| 4 | **添加布局预设**（动作） | 「布局预设」子菜单内 | 同上，保存布局预设 |

- **入口位置**（用户明确）：不在「主题颜色」「自定义控件」子菜单内部，而是在**设置主菜单**里，紧挨「自定义控件」按钮下方新增两个入口按钮
- **子菜单**：「主题预设」「布局预设」各自是一个独立子菜单（覆盖层），里面是**用户已保存的预设列表** + **添加预设按钮**
- **切换使用**：点击列表中某预设 → 应用该预设（主题预设切配色 / 布局预设恢复键位布局）

---

## 2. UI 交互细节

### 2.0 设置菜单入口（SettingsPanel）

```
┌─────────────────────────┐
│ 设置                [关于] │
│ 主题        [自定义][黑]   │
│ 透明度      ────────      │
│ 鼠标垫            [显示]  │
│ ┌───────────────┐        │
│ │   自定义控件   │        │ ← LockKeyBtn（现有）
│ └───────────────┘        │
│ [主题预设] [布局预设]      │ ← 新增 2 个入口按钮（并排；与自定义控件同宽或分列）
└─────────────────────────┘
```

- 两个入口按钮紧挨「自定义控件」下方，样式与 `LockKeyBtn` 一致（圆角 Border + 居中文本）
- 点击「主题预设」→ 弹出主题预设子菜单；点击「布局预设」→ 弹出布局预设子菜单
- 两个子菜单都是**独立覆盖层**（遮罩点击收起），与设置菜单本身互不遮挡

### 2.1 主题预设子菜单

```
┌───────────────────────────────┐
│ 主题预设                [×]    │ ← 标题行 + 关闭
├───────────────────────────────┤
│ （已保存预设列表，逐行可点击）   │
│   ▶ 我的配色A                  │ ← 点击 = 应用该预设
│   ▶ 我的配色B                  │
│   暂无预设（空态提示）          │
├───────────────────────────────┤
│          [添加预设]            │ ← 动作按钮 #3
│   输入框（展开后出现）          │
│   [保存]  [取消]               │
└───────────────────────────────┘
```

### 2.2 布局预设子菜单

- 结构、流程与主题预设子菜单完全一致；动作按钮名为「添加布局预设」（#4）

### 2.3 交互约束（与现有菜单一致）

- 子菜单打开时点遮罩/外部区域收起（沿用 `Tapped` 遮罩模式）
- 子菜单之间（主题预设 / 布局预设 / 调色盘 / 主题颜色 / 自定义控件）**并排或互斥，禁止重叠**（沿用双栏 Grid 布局引擎）
- 预设名**去重**：同名时提示「已存在同名预设」（推荐，不自动覆盖）
- 预设名为空/全空白：保存按钮禁用或提示
- 预设名长度上限（建议 20 字符），非法字符（路径分隔符等）过滤

---

## 3. 数据模型

### 3.1 主题预设内容（一份 = 一次快照）

```json
{
  "name": "预设名",
  "type": "theme",
  "savedAt": "2026-08-19T12:00:00",
  "data": {
    "theme": "custom",                       // 应用时的主题态 dark/gray/light/pink/blue/custom
    "colors": {                              // 8 个调色目标（= 现有 Custom* 键，格式 #RRGGBB / #AARRGGBB）
      "panel": "#B3FFB3C6",
      "border": "#CCB0577E",
      "keyBg": "#FFFFB3C6",
      "keyFg": "#FFFFFFFF",
      "pressedBg": "#FFB0577E",
      "pressedFg": "#FFFFFFFF",
      "pad": "#4DFFB3C6",
      "dot": "#FFB0577E"
    }
  }
}
```

### 3.2 布局预设内容（一份 = 一次快照）

```json
{
  "name": "预设名",
  "type": "layout",
  "savedAt": "2026-08-19T12:00:00",
  "data": {
    "layoutLocked": false,                    // LayoutLocked
    "keyOpacity": 255,                        // KeyOpacity_
    "padVisible": true,                       // PadVisible_
    "keys": {                                 // Layout_<键名> = "x,y,w,h"（默认键全量）
      "W": "0,0,44,44",
      "...": "..."
    },
    "customKeys": {                           // 自定义键：名 → {pos: "x,y", size: "w,h"}
      "F13": { "pos": "100,50", "size": "44,44" }
    },
    "deletedKeys": ["Caps"]                   // Deleted_<键名> = 1 的键列表
  }
}
```

### 3.3 存储文件（包外，更新/重装不丢）

```
%LOCALAPPDATA%\KeyDisplay\presets.json
```

```json
{
  "version": 1,
  "themePresets": [ /* 主题预设数组 */ ],
  "layoutPresets": [ /* 布局预设数组 */ ]
}
```

---

## 4. 持久化架构（关键：更新后数据不丢失）

### 4.1 现状与问题

- 现有全部用户数据（Theme、Custom* 8 键、Layout_*/Custom_*/CustomPos_*/Deleted_*/LayoutLocked/KeyOpacity_/PadVisible_）存于 **UWP `ApplicationData.LocalSettings`（包内）**
- 正常升级（`Add-AppxPackage` 覆盖安装）时 LocalSettings **保留**；但本项目的安装器是 **Remove-AppxPackage + Add-AppxPackage 重装**——**LocalSettings 会丢**
- 因此预设数据**绝不能只存包内**，必须落到包外

### 4.2 方案（推荐）：companion 中转存储

- **数据落点**：`%LOCALAPPDATA%\KeyDisplay\presets.json`（companion 进程写；安装器卸载/重装**不触碰**该目录）
- **读写通道**：扩展现有命名管道 `\\.\pipe\KeyDisplayState` 为**请求/应答双工**（管道已 `PIPE_ACCESS_DUPLEX`，现仅单向推送）：

```
帧类型（管道 message 模式，UTF-8 文本帧；每条 WriteFile/ReadFile = 一条完整消息）：
- STATE 帧（现有，companion→widget）：二进制 "KDSP" 前缀 + 36/68B 快照 —— 字节布局不动
- 请求帧（widget→companion）：文本 "CMD|" 前缀
    CMD|GET_PRESETS
    CMD|PUT_PRESETS|<presets.json 全文>
- 应答帧（companion→widget）：文本 "RESP|" 前缀
    RESP|OK
    RESP|ERR|<错误消息>
    RESP|DATA|<presets.json 全文>
```

- **widget 侧改造点（InputStateReader.cs，现有 170 行独立文件）**：
  - `CreateFileW` 打开方式 `GENERIC_READ` → `GENERIC_READ | GENERIC_WRITE`（双工）
  - `FileStream` 构造 `FileAccess.Read` → `FileAccess.ReadWrite`
  - 读循环按消息前缀分派：`KDSP` → 现有快照解析（不动）；`RESP|` → 触发 `PresetResponse` 事件（`EventHandler<string>`，参数为 `OK`/`ERR:<msg>`/`DATA:<json>`）
  - 新增发送 API：`Task<string> RequestPresetAsync(string cmd, string payload, int timeoutMs = 2000)`——发送 `CMD|` 帧后等待 `PresetResponse`，**与快照读取共用同一管道但由读循环天然串行**（应答事件回调，无需额外锁）
  - companion 未连接/超时 → 返回 null，调用方降级（预设功能提示不可用，不影响主功能）
- **companion 侧改造点**：`pipe_server.py` 泵循环增加**读客户端消息**处理（管道已 DUPLEX）：收到 `CMD|GET_PRESETS` → 应答 `RESP|DATA|<json>`；`CMD|PUT_PRESETS|<json>` → 校验并写文件 → `RESP|OK`/`RESP|ERR|<msg>`；新增 `presets.py`（load/save/损坏备份 `.bak`，UTF-8 无 BOM，异常容错）；**STATE 帧推送逻辑与字节布局一律不动**
- **widget 侧行为**：
  - 启动时 `GET_PRESETS` 拉取预设列表（companion 未启动/请求失败 → 预设列表为空，功能降级但**不影响其他功能**）
  - 保存/删除预设时 `PUT_PRESETS` 全量写回（数据量小，全量即可）
  - 应用预设 = 把快照写入 LocalSettings 现有键 + 全量刷新 UI（复用 `ApplyTheme()` / 布局恢复逻辑）

### 4.3 兼容与降级

- companion 旧版本无预设协议：widget 请求超时（建议 2s）→ 预设功能显示「伴生进程不支持」；不影响主功能
- presets.json 损坏：companion 备份为 `.bak` 后重建空文件，不崩溃
- **新装/升级迁移**：0.6.0 用户的现有 Custom* 数据仍留在 LocalSettings（升级保留），**不做主动迁移**；首次保存预设时才把当前值写入预设文件

### 4.4 明确不做的

- 不迁移历史 LocalSettings 数据到包外（保持现状，正常升级本就保留）
- 不做云同步

---

## 5. 技术实现要点（开发时核对）

### 5.1 XAML（Widget1.xaml）

- **设置菜单入口**（SettingsMenu 的 StackPanel，`LockKeyBtn` 下方 Margin="0,8,0,0"）：
  - 新增一行 Grid（双列）：列0=`ThemePresetBtn`（文本「主题预设」）、列1=`LayoutPresetBtn`（文本「布局预设」），按钮样式与 `LockKeyBtn` 一致（圆角 Border + 居中文本，高 26）；并排两列各占一半宽（菜单宽 190，两列 ≈ 88/88）
  - 事件：`ThemePreset_Click` / `LayoutPreset_Click`（打开对应覆盖层）
- **新增两个覆盖层子菜单**（与 ThemeColorPanel/LockPanel 同级的新 Grid，可复用现有菜单外观：圆角 Border、遮罩 Grid、`Tapped` 收起）：
  - `ThemePresetPanel`：标题「主题预设」+ 关闭 ×；预设列表容器 `ThemePresetList`（动态 Border 项：预设名 + × 删除）；底部「添加预设」按钮 → 展开 `ThemePresetNameRow`（含 `ThemePresetNameInput` TextBox + 保存/取消）；状态提示 `ThemePresetMsg`（TextBlock，显示空名/重名/保存失败等错误，初始隐藏）
  - `LayoutPresetPanel`：同上，标题「布局预设」，动作按钮「添加布局预设」，列表 `LayoutPresetList`，输入行 `LayoutPresetNameRow` / `LayoutPresetNameInput`，状态提示 `LayoutPresetMsg`
- **布局注意**：新覆盖层与既有菜单（主题颜色/调色盘/自定义控件/关于）并存时**两两不重叠**（沿用 Grid 列布局，禁止绝对定位堆叠）

### 5.2 C#（Widget1.xaml.cs）

- 新字段：`_themePresets` / `_layoutPresets`（List<Preset>，启动拉取）
- 管道客户端新增发送方法：`SendPresetCommand(cmd, json)`（写管道 + 读应答，2s 超时；注意 UWP `NamedPipeClientStream` 用法与现有读取线程共存——用**独立请求锁**，不能与 60Hz 读取竞争）
- 主题预设快照：读 8 个 Custom* 键 + `_theme` 组装 JSON
- 布局预设快照：枚举 `_customKeys` + `Layout_*` + `Deleted_*` + 三个开关值组装 JSON（**保存前先调用现有 `SaveAllKeyLayout()` 保证 Layout_* 落盘为最新**）
- 应用主题预设：写 8 个 Custom* 键 + `_theme` + `ApplyTheme()` + `SyncPickerToColor` 相关刷新
- 应用布局预设：写 Layout_*/Custom_*/CustomPos_*/Deleted_*/LayoutLocked/KeyOpacity_/PadVisible_ → 重建/刷新按键（复用启动恢复路径，注意先清空现有 `_customKeys` 再按预设重建）
- 删除预设：列表项 × → 确认后移除并 `PUT_PRESETS`
- **编码纪律**：Widget1.xaml.cs 为 UTF-8 **无 BOM**，只能用 edit 工具或 Python(utf-8 无 BOM) 修改，**严禁 PowerShell Set-Content**（历史事故）

### 5.3 Companion（Python）

- `presets.py`：`load()` / `save(obj)` / 备份坏文件
- `pipe_server.py`：接收帧改为先判类型——`STATE` 帧（现有推送）与 `CMD` 帧（请求应答）分流；应答帧写回同一管道
- 管道 SDDL 已放行 UWP 包 SID，widget 读应答无需额外授权

---

## 6. 验收标准

1. **设置菜单**里「自定义控件」按钮**正下方**出现「主题预设」「布局预设」两个入口按钮，与设置菜单其他项对齐、不重叠
2. 点击「主题预设」→ 弹出主题预设子菜单；点击「布局预设」→ 弹出布局预设子菜单；遮罩点击收起
3. 子菜单内点「添加预设」/「添加布局预设」→ 输入名称 → 保存 → 列表出现该预设；空名/重名有提示
4. 切换：点击预设 → 配色/布局立即切换为该预设快照（主题预设改配色、布局预设改键位/位置/尺寸/开关）
5. **数据不丢（核心）**：
   - 升级安装（Remove+Add 重装 msix）后，预设列表仍在，可正常切换
   - companion 目录（%LOCALAPPDATA%\KeyDisplay\）在重装后保留 presets.json
6. 预设应用后重启 widget，状态保持（写入 LocalSettings 生效）
7. 遮罩收起、菜单并排不重叠、与 0.6.0 既有功能（调色盘/主题循环/自定义控件/关于）互不干扰
8. 所有任务完成弹测试窗口供验收（AGENT-PROCESS.md）

---

## 7. 发布配套

- VERSION.md / README.md / setup.iss（0.7.0）/ Package.appxmanifest（**1.4.0.0**）四处同步
- companion 更新 → 重新 PyInstaller 打包 → 纳入 Setup 发布（0.6.0 的 companion 不支持预设协议，需随包升级）
- 发布流程沿用 INHERIT：构建签名 → publish-check → ISCC → 归档 → 推送 → Release

---

## 8. 任务分解与执行建议（子代理分配）

### 8.1 任务清单与依赖

```
T1 管道协议扩展（companion/Python）        ──┐
                                              ├─→ T3 C# widget 逻辑（依赖 T1 协议契约）
T2 XAML 布局（4 按钮 + 2 子菜单）           ──┤
                                              ├─→ T5 构建/签名/部署/测试窗口
T4 companion 重新打包（PyInstaller）        ──┘        │
                                                       ├─→ T6 数据不丢验证（重装后预设仍在）
T5 构建部署（MSBuild + dsh-install）        ────────────┘        │
                                                                 v
T7 发布（版本同步 0.7.0/1.4.0.0 → publish-check → ISCC → 归档 → 推送 → Release）
```

### 8.2 任务详情与子代理分配

| 任务 | 内容 | 适合子代理? | 理由 |
|---|---|---|---|
| **T0 协议契约（先定）** | 定义管道 CMD 帧格式与 presets.json 结构（§3.3/§4.2），写进文档供 T1/T3 共同遵守 | 否（主 agent 定契约） | 接口必须先钉死，T1/T3 才能并行 |
| **T1 companion 协议扩展** | `pipe_server.py` 请求应答分流 + `presets.py`（load/save/损坏备份）；自带 `test_client.py`/`test_units.py` 自测 | ✅ **是** | 独立 Python 模块，有测试框架，契约明确即可并行 |
| **T2 XAML 布局** | Widget1.xaml：设置菜单 2 入口按钮 + 2 覆盖层子菜单；不改 C# 只加命名与事件名 | ✅ **是** | 静态文件、改动局部；需给出准确现有结构（SettingsMenu/LockKeyBtn 区域）与编码纪律（UTF-8 无 BOM） |
| **T3 C# 逻辑** | Widget1.xaml.cs：入口点击/子菜单开关/列表渲染/快照与应用/管道请求（2s 超时、与 60Hz 读取线程互斥） | ❌ **否（主 agent 做）** | 与 2742 行现有代码深度耦合（布局恢复/主题应用/线程竞争），历史事故多发区 |
| **T4 companion 打包** | PyInstaller 重打 `KeyDisplayCompanion.exe`，替换 dist/桌面/安装包依赖 | ✅ **是** | 独立流程，命令固定 |
| **T5 构建部署** | MSBuild 构建 1.4.0.0 → signtool 签名 → 提权安装 → 弹测试窗口 | ❌ 否 | 环境/提权/验收交互，主 agent 现场做 |
| **T6 数据不丢验证** | 卸载重装后验证 %LOCALAPPDATA%\KeyDisplay\presets.json 保留、预设可切换 | ❌ 否 | 需要完整安装链路（T5 产物），主 agent 做 |
| **T7 发布** | 版本同步 → 门禁 → ISCC → 归档 → 推送 → Release | ❌ 否 | 发布纪律由主 agent 把关 |

### 8.3 子代理执行要点（委派 T1/T2/T4 时的强制约束）

1. **编码纪律**：`Widget1.xaml`/`Widget1.xaml.cs` 均为 UTF-8 **无 BOM**——严禁 PowerShell `Set-Content` 重写，只能 edit 工具或 Python（`encoding='utf-8'` 无 BOM）；xaml 为 UTF-8 带 BOM（XML 声明）可整写
2. **契约先行**：T1/T2/T3 共享的协议与控件命名（`ThemePresetBtn`/`LayoutPresetBtn`/`ThemePresetPanel`/`LayoutPresetPanel`/`PresetNameInput` 等）以本文档为准，子代理不得自创命名
3. **不得动既有功能**：T2 只新增节点；T1 不得改变现有 60Hz 状态帧格式（STATE 帧字节布局保持不变）
4. **自测边界**：T1 用 `test_units.py` 跑通；T2 静态检查（XML 合法、闭合配对）；T4 打包后 `KeyDisplayCompanion.exe --help`/启动冒烟
5. 子代理交付物 = 代码 + 改动清单，由主 agent 集成与验收