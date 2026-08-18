# Agent 继承协议（主 Agent 卡顿 / 断联时，下一个 Agent 如何接手）

> 本文件是**主 Agent（总管）异常时的接班契约**。正常开发流程见 `docs/AGENT-PROCESS.md`（权威），
> 技术交接见 `docs/HANDOFF.md`（权威）。本文件只回答一件事：**"上一个 Agent 断了，我接手，从哪继续？"**
>
> 一句话铁律：**先核对，再动手，绝不重做。** 上一个 Agent 的成果（git 提交 / 安装包 / 文件）都已落盘，
> 对话断了也不会丢。新 Agent 必须先确认"做到哪一步"，而不是从头再来。

---

## 0. 触发场景（什么时候用本协议）

| 场景 | 你（用户）怎么做 |
|---|---|
| 主 Agent 回复卡顿 / 复读 / 空转 | 直接发一句话打断，再发"查看 docs/INHERIT.md 并按协议接手" |
| 主 Agent 断联 / 会话中断 | 新建会话，第一条发"按 docs/INHERIT.md 协议接手项目" |
| 不确定上次任务是否完成 | 让新 Agent 走第 1~4 步核对，再决定下一步 |
| 主动换人接手 | 同上 |

> 无论哪种场景，你都不必解释技术背景——新 Agent 会自己读交接文档。

---

## 1. 新 Agent 接手 4 步核对（照做，缺一不可）

### 第 1 步：读交接入口
按顺序读（**必须从头读**，这是唯一权威来源）：
1. `docs/HANDOFF.md` —— 完整技术交接，尤其看顶部「当前最新交接事项」与「未提交改动」提示
2. `docs/INHERIT.md` —— 本文件（接班契约）
3. `VERSION.md` —— 当前版本号与「进行中任务」
4. `docs/AGENT-PROCESS.md`（如需继续开发任务）——协作流程契约
5. 上次任务涉及的具体文档：`docs/ARCHITECTURE.md` / `docs/BUILD.md` / `docs/INSTALL.md` / `docs/ISSUE-PINNED-CHROME.md`

### 第 2 步：核对代码状态（git）
```powershell
git -C "C:\恐龙\项目\Game Bar 按键显示组件" status -s          # 未提交改动
git -C "C:\恐龙\项目\Game Bar 按键显示组件" log --oneline -8   # 最近提交，确认最新版本
```
- 若有未提交改动 → 先读 diff 看是什么，**问用户是否提交**，绝不自动丢弃或自动提交。
- 记录最新版本号（对应 VERSION.md 当前版本）。

### 第 3 步：核对产物状态（安装包 / release）
```powershell
Get-ChildItem "C:\恐龙\项目\Game Bar 按键显示组件\release" -Directory | Sort-Object LastWriteTime -Descending | Select-Object -First 3 Name
Get-Item "C:\Users\恐龙milk\Desktop\KeyDisplaySetup.exe" | Select-Object VersionInfo.ProductVersion, LastWriteTime
```
- 桌面安装包应是「最新版本号」；不是 → 说明上次发布没走完，报告用户。

### 第 4 步：核对运行状态（如上次任务涉及安装/测试）
```powershell
Get-AppxPackage -Name 'KeyDisplay.Widget' | Select-Object Version, PackageFullName   # 已装版本，应 = VERSION.md 版本
Get-Process | Where-Object { $_.Name -like 'KeyDisplay*' } | Select-Object Name, Id    # 进程是否在跑
Get-Content "$env:LOCALAPPDATA\Packages\KeyDisplay.Widget_hdjf4fqmxxv8g\LocalState\diag.txt" -Tail 3   # 诊断日志
```

---

## 2. 返回给用户的接手报告（证明核对完成）

新 Agent 核对后，**必须**回复用户这三行（缺一不可，让用户一眼确认"接住了"）：

```
1. 我核对了：git 最新提交 = <版本+commit短号>；工作区未提交改动 = <有/无>；已装版本 = <x.x.x.x>
2. 上次任务进度 = <做到哪一步，卡在哪>
3. 我建议下一步 = <做什么>，是否现在动手？
```

> 用户回复"做"才动手；回复"改/停"则按指示调整。**接班的第一步永远是"核对+报告"，不是直接改代码。**

---

## 3. 主 Agent 平时该做什么，让继承更省事（给接班 Agent 的参考）

上一任总管在正常工作时，应持续做到这些，接班的你也要沿用：

### 3.1 每完成一个里程碑就提交 + 归档
- 功能写一段 → 编译过 → `git commit`。
- 发布 → 归档 `release/<版本>/` + 刷新桌面 `KeyDisplaySetup.exe`。
- 这样用户**随时可断开换人**，成果已在硬盘。

### 3.2 派发子代理的固定写法（免费模型 + provider/model 钉死）
- 免费模型路由：provider = `opencode-zen`，model = `deepseek-v4-flash-free`。
- ❗ **不要**用 provider `opencode`（历史多次失败）。默认模型 `opencode-go/deepseek-v4-flash` 也可用。
- 并发上限 5，推荐 4（黄金并发）。
- 后台运行（`run_in_background: true`），不阻塞等待。

### 3.3 查看/验收子代理代码（总管亲自核对，不轻信汇报）
- 子代理返回后，总管**亲自读 diff、跑编译、跑测试**。
- 命令速查：
```powershell
# 编译 widget（C# 改动）
& 'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe' 'KeyDisplay.Widget\KeyDisplay.Widget.sln' /t:Restore,Build /p:Configuration=Release /p:Platform=x64 /p:AppxBundle=Never /p:UapAppxPackageBuildMode=SideloadOnly /p:AppxPackageSigningEnabled=false /p:VisualStudioVersion=17.0 /v:m
# 伴生进程单测（Python 改动）
python -m unittest test_units -v   # 工作目录 KeyDisplay.Companion，26 测试应全过
# 打安装包
& 'C:\Users\恐龙milk\AppData\Local\Programs\Inno Setup 6\ISCC.exe' 'installer\setup.iss'
```

### 3.4 构建打包完整链路（发布时）
```
build-msix.ps1（或直接 MSBuild 编译） → 签名 msix（signtool）→ 删旧 msix（避免通配符混入）
→ 更新 installer/setup.iss 三处版本号 → ISCC 打包 → 归档 release/<版本> + 桌面副本
→ VERSION.md / README.md 版本号同步 → git commit
```

### 3.5 每轮完了弹测试窗口
- 强制环节（见 AGENT-PROCESS.md §2）：UWP 窗口 `explorer.exe "shell:AppsFolder\KeyDisplay.Widget_hdjf4fqmxxv8g!App"`。
- 纯后端/文档改动则明确说明"无可视效果"，用测试输出代替。

---

## 4. 常见"脏现场"与处理（接班的你，别被这些绊住）

| 现象 | 含义 | 处理 |
|---|---|---|
| `git status` 大量 M 文件 | 上次任务改动未提交 | 读 diff 确认内容 → 问用户是否提交，勿自动丢弃 |
| 桌面安装包版本 ≠ VERSION.md | 上次发布没走完 | 报告用户，补全发布（§3.4）|
| `.ps1` 中文乱码 / 语法报错 | 脚本是 UTF-8 无 BOM | 用 PS 转 `UTF8Encoding($true)`（带 BOM），项目 `install-msix.ps1` 等已修，不要改回无 BOM |
| `Get-AppxPackage` 版本仍是旧包号 | 升级未完成 | 走 `install-msix.ps1`（它已含强制移除旧包 + 验证，见 HANDOFF）|
| 进程 `KeyDisplayCompanion` 停不掉（Access denied） | 它以系统级/单实例 mutex 运行 | 属正常，不强制杀；新启动的加载新 exe 即是新版 |

---

## 5. 安全原则（接班人必守）

1. **有未提交改动** → 先确认、再决定，绝不自动提交或丢弃。
2. **有新版本安装包** → 先让用户确认要不要装/发布。
3. **不确定的事** → 直接问用户，不猜；但能自主决定的小事（沿用 HANDOFF 纪律）直接做。
4. **不重复劳动** → 先核对再动手；上一个 Agent 已完成的部分（commit/产物）直接复用。
5. **失败必可视化** → 编译/测试/安装失败，直接贴错误摘要，不隐藏。

---

> 本协议 v1.0 于 2026-08-17 建立，与 `AGENT-PROCESS.md`、`HANDOFF.md` 配套使用。
