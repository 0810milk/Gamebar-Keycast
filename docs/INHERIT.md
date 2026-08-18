# Agent 继承协议（主 Agent 卡顿 / 断联时，下一个 Agent 如何接手）v2.0

> 本文件是**主 Agent（总管）异常时的接班契约**。正常开发流程见 `docs/AGENT-PROCESS.md`（权威），
> 技术交接见 `docs/HANDOFF.md`（权威）。本文件只回答一件事：**"上一个 Agent 断了，我接手，从哪继续？"**
>
> 一句话铁律：**先核对，再动手，绝不重做。** 上一个 Agent 的成果（git 提交 / 安装包 / 文件）都已落盘，
> 对话断了也不会丢。新 Agent 必须先确认"做到哪一步"，而不是从头再来。

---

## 0. 项目快照（接手第一眼要知道的）

| 项 | 当前值（截至 2026-08-18 整理时） |
|---|---|
| 项目根目录 | `C:\恐龙\项目\Game Bar 按键显示组件`（git 仓库，分支 `master`，远程 `main`） |
| 语义版本 | **0.5.3（开发中，未发布）**；已发布最新 0.5.2 beta |
| UWP 包版本 | 1.2.2.0（对应 0.5.2/0.5.3；历史：0.4.0=1.0.0.0、0.4.1=1.1.0.0、0.5.0=1.2.0.0、0.5.1=1.2.1.0） |
| GitHub 仓库 | https://github.com/0810milk/Gamebar-Keycast（SSH: `git@github.com:0810milk/Gamebar-Keycast.git`） |
| 作者 | **恐龙milk**（版权人、GitHub owner 0810milk；QQ 反馈群 2152061189） |
| 已装 widget | `Get-AppxPackage -Name 'KeyDisplay.Widget'` → 应为 1.2.2.0 |
| 开发代理 | 长期子代理 `3b0079ac`（可 send_message 续用，它最熟代码） |
| 当前未提交 | 0.5.3「关于面板」代码（Widget1.xaml/.cs、csproj、Assets/Avatar.jpg、TASK-0.5.3-about.md）——**用户明确暂缓发布** |

---

## 1. 触发场景（什么时候用本协议）

| 场景 | 你（用户）怎么做 |
|---|---|
| 主 Agent 回复卡顿 / 复读 / 空转 | 直接发一句话打断，再发"查看 docs/INHERIT.md 并按协议接手" |
| 主 Agent 断联 / 会话中断 | 新建会话，第一条发"按 docs/INHERIT.md 协议接手项目" |
| 不确定上次任务是否完成 | 让新 Agent 走第 2~5 步核对，再决定下一步 |
| 主动换人接手 | 同上 |

> 无论哪种场景，你都不必解释技术背景——新 Agent 会自己读交接文档。

---

## 2. 新 Agent 接手 4 步核对（照做，缺一不可）

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
- 桌面安装包应是「最新已发布版本号」；注意 0.5.3 尚未发布，桌面可能仍是 0.5.2 属正常。

### 第 4 步：核对运行状态（如上次任务涉及安装/测试）
```powershell
Get-AppxPackage -Name 'KeyDisplay.Widget' | Select-Object Version, PackageFullName   # 已装版本
Get-Process | Where-Object { $_.Name -like 'KeyDisplay*' } | Select-Object Name, Id    # 进程是否在跑
Get-Content "$env:LOCALAPPDATA\Packages\KeyDisplay.Widget_hdjf4fqmxxv8g\LocalState\diag.txt" -Tail 3   # 诊断日志
```

---

## 3. 返回给用户的接手报告（证明核对完成）

新 Agent 核对后，**必须**回复用户这三行（缺一不可，让用户一眼确认"接住了"）：

```
1. 我核对了：git 最新提交 = <版本+commit短号>；工作区未提交改动 = <有/无>；已装版本 = <x.x.x.x>
2. 上次任务进度 = <做到哪一步，卡在哪>
3. 我建议下一步 = <做什么>，是否现在动手？
```

> 用户回复"做"才动手；回复"改/停"则按指示调整。**接班的第一步永远是"核对+报告"，不是直接改代码。**

---

## 4. 主 Agent 平时该做什么，让继承更省事（给接班 Agent 的参考）

上一任总管在正常工作时，应持续做到这些，接班的你也要沿用：

### 4.1 每完成一个里程碑就提交 + 归档
- 功能写一段 → 编译过 → `git commit`。
- 发布 → 归档 `release/<版本>/` + 刷新桌面 `KeyDisplaySetup.exe`。
- 这样用户**随时可断开换人**，成果已在硬盘。
- ⚠️ 例外：用户可能说"暂缓发布"（如当前 0.5.3）——此时改动留在工作区属正常，**不要擅自提交**，在 HANDOFF 顶部登记状态即可。

### 4.2 派发子代理的固定写法（免费模型 + provider/model 钉死）
- 免费模型路由：provider = `opencode-zen`，model = `deepseek-v4-flash-free`。
- ❗ **不要**用 provider `opencode`（历史多次失败）。默认模型 `opencode-go/deepseek-v4-flash` 也可用。
- 并发上限 5，推荐 4（黄金并发）。
- 后台运行（`run_in_background: true`），不阻塞等待。
- 本项目有**长期开发子代理 `3b0079ac`**：优先用 `send_message` 续用（它保留全部代码上下文，改得又快又准）；发新任务时让它从头读 `docs/TASK-*.md` 任务书。

### 4.3 查看/验收子代理代码（总管亲自核对，不轻信汇报）
- 子代理返回后，总管**亲自读 diff、跑编译、跑测试**。
- 命令速查：
```powershell
# 编译 widget（C# 改动）
& 'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe' 'C:\恐龙\项目\Game Bar 按键显示组件\KeyDisplay.Widget\KeyDisplay.Widget.sln' /t:Restore,Build /p:Configuration=Release /p:Platform=x64 /p:AppxBundle=Never /p:UapAppxPackageBuildMode=SideloadOnly /p:AppxPackageSigningEnabled=false /p:VisualStudioVersion=17.0 /v:m
# 伴生进程单测（Python 改动）
python -m unittest test_units -v   # 工作目录 KeyDisplay.Companion，26 测试应全过
# 打安装包
& 'C:\Users\恐龙milk\AppData\Local\Programs\Inno Setup 6\ISCC.exe' 'C:\恐龙\项目\Game Bar 按键显示组件\installer\setup.iss'
```

### 4.4 构建打包完整链路（发布时）
```
MSBuild 编译 → 签名 msix（signtool）→ 复制到 dist\KeyDisplay.Install（删旧 msix，避免通配符混入）
→ 三处版本号同步（VERSION.md / README.md / installer\setup.iss）+ UWP 包版本递增（Package.appxmanifest）
→ ISCC 打包 Setup.exe → 归档 release/<版本> + 刷新桌面副本
→ git commit → 【推到 GitHub：§4.6】（可选：发 Release）
```
- 版本号对照表：语义版本 0.x.y ↔ UWP 包 1.x.y.0（发布时两处都要递增）。

### 4.5 部署到本机（签名 + 提权安装，验收前必做）
```powershell
# 1) 签名（测试证书 KeyDisplayDev!）
& 'C:\Program Files (x86)\Windows Kits\10\bin\<sdk版本>\x64\signtool.exe' sign /fd SHA256 /f 'cert\KeyDisplay.pfx' /p 'KeyDisplayDev!' <msix路径>
# （signtool 可用 Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Recurse -Filter signtool.exe 定位）

# 2) 提权安装（停止旧进程 → 移除旧包 → 装新包；UAC 会弹窗）
$msix = "<完整msix路径>"
$cmd = "-NoProfile -ExecutionPolicy Bypass -Command `"Get-Process | Where-Object { `$_.Name -like 'KeyDisplay.Widget*' } | Stop-Process -Force -ErrorAction SilentlyContinue; Start-Sleep 1; Get-AppxPackage -Name 'KeyDisplay.Widget' | Remove-AppxPackage -ErrorAction SilentlyContinue; Start-Sleep 2; Add-AppxPackage -Path '$msix'; if (`$?) { 'INSTALL_OK' } else { 'INSTALL_FAIL' }`""
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName='powershell.exe'; $psi.Arguments=$cmd; $psi.Verb='runas'; $psi.UseShellExecute=$true
$p=[System.Diagnostics.Process]::Start($psi); $p.WaitForExit(90000)
```

### 4.6 GitHub 发布（版本更新直接推到 GitHub，含凭据与踩坑）

**本机 GitHub 凭据（仅本机可用，勿外传）：**
- 仓库：`git@github.com:0810milk/Gamebar-Keycast.git`（owner: 0810milk）
- 网页地址：https://github.com/0810milk/Gamebar-Keycast （Releases 页 /releases）
- 推送方式：**SSH**（不是 HTTPS），默认分支 **`main`**（本机 git 分支名是 `master`，推送用 `master:main`）。
- SSH key：`C:\Users\恐龙milk\.ssh\id_ed25519`（已加到 GitHub）。
- ❗ **中文用户名路径坑**（重要）：用户名含中文「恐龙milk」，git 内部的 Git Bash 路径会把 `.ssh` 解析成乱码
  （`/c/Users/\277\326\301\372milk/.ssh`），导致裸 `git push` 报 host key/权限错误。解决——**push/查远程时必须带 `GIT_SSH_COMMAND`**：
  ```powershell
  $env:GIT_SSH_COMMAND="ssh -i `"$env:USERPROFILE\.ssh\id_ed25519`" -o IdentitiesOnly=yes -o StrictHostKeyChecking=accept-new"
  git push origin master:main
  # 或 git push origin master  （origin 已指向该 SSH 地址）
  ```

**换人接手后第一次 push 前**：确认 `git remote -v` 输出是 `git@github.com:0810milk/Gamebar-Keycast.git`；
不是则 `git remote set-url origin git@github.com:0810milk/Gamebar-Keycast.git`。

**发布 Releases（可选，版本上线用 GitHub CLI）：**
- 机器已装 `gh`（GitHub CLI）。**gh 认证需要 HTTPS token，不认 SSH**；token 由用户提供（classic PAT、勾 `repo`，或 fine-grained 配该仓库 Contents 读写）。
- 通过环境变量临时注入（**不要把 token 写进任何仓库文件或 git 历史**）：
  ```powershell
  $env:GH_TOKEN="ghp_..."   # 用户提供，用完即忘
  gh auth status                          # 确认已登录 owner
  gh release create "0.5.3-beta" "release/0.5.3-beta/KeyDisplaySetup.exe" --repo 0810milk/Gamebar-Keycast --title "0.5.3 beta" --notes "更新说明..."
  ```
- 批量发旧版本：循环对每个 `release/<版本>/` 执行上面的 `gh release create`。
- ⚠️ **Latest 标记坑**：GitHub 按**创建时间倒序**展示 Releases，最后创建的那个会变成「Latest」。
  若按旧→新顺序逐个创建，最早版本反而成 Latest——**创建完必须把最新版本设为 Latest**：
  ```powershell
  gh release edit 0.5.2-beta --repo 0810milk/Gamebar-Keycast --latest=false   # 旧版取消
  gh release edit 0.5.3-beta --repo 0810milk/Gamebar-Keycast --latest          # 新版设 Latest
  ```

**权限/密钥红线（用户反复强调）：**
- ❌ **绝不上传**：大模型 API 密钥（settings.yaml 在 `~/.dsh/`，不在项目内；无 .env）、代码签名私钥 `cert/KeyDisplay.pfx`（cert/ 在 .gitignore，勿 `git add -f`）。
- 上传前自觉扫描：`git ls-files | findstr /i ".pfx .p12 .env settings.yaml config.yaml"` 应为空。
- Releases 完成后**提醒用户删除对话里出现过的 token**（GitHub → Settings → Tokens → Delete/Revoke）。

### 4.7 每轮完了弹测试窗口
- 强制环节（见 AGENT-PROCESS.md §2）。**用户 Game Bar 可能不可用，直接用独立窗口激活 widget（不依赖 Game Bar）**：
  ```powershell
  explorer.exe "shell:AppsFolder\KeyDisplay.Widget_hdjf4fqmxxv8g!App"
  ```
- 启动后核对：`Get-Process -Name 'KeyDisplay.Widget'` 有进程即成功。
- 备选：tkinter 开发预览 `python tools\preview.py`（布局/反色预览，无需安装）。
- 纯后端/文档改动则明确说明"无可视效果"，用测试输出代替。

---

## 5. 常见"脏现场"与处理（接班的你，别被这些绊住）

| 现象 | 含义 | 处理 |
|---|---|---|
| `git status` 大量 M 文件 | 上次任务改动未提交 | 读 diff 确认内容 → 问用户是否提交，勿自动丢弃（**若用户已说"暂缓发布"，属正常，别擅自提交**） |
| 桌面安装包版本 ≠ VERSION.md | 上次发布没走完 | 报告用户，补全发布（§4.4）|
| `.ps1` 中文乱码 / 语法报错 | 脚本是 UTF-8 无 BOM | 用 PS 转 `UTF8Encoding($true)`（带 BOM），项目 `install-msix.ps1` 等已修，不要改回无 BOM |
| `Get-AppxPackage` 版本仍是旧包号 | 升级未完成 | 走 `install-msix.ps1`（它已含强制移除旧包 + 验证，见 HANDOFF）|
| 进程 `KeyDisplayCompanion` 停不掉（Access denied） | 它以系统级/单实例 mutex 运行 | 属正常，不强制杀；新启动的加载新 exe 即是新版 |
| **主 Agent 输出空转**（反复说"调用工具/执行"却无实际动作、复读机式重复） | 模型输出层异常（本次会话最严重一次，连续数百行无效重复） | **用户直接打断**："继续 / 重新执行"；新 Agent 接手后**第一件事就是真实调用工具**，用结果说话，绝不模仿空转 |

---

## 6. 安全原则（接班人必守）

1. **有未提交改动** → 先确认、再决定，绝不自动提交或丢弃（含"用户明确暂缓"的暂存改动）。
2. **有新版本安装包** → 先让用户确认要不要装/发布。
3. **不确定的事** → 直接问用户，不猜；但能自主决定的小事（沿用 HANDOFF 纪律）直接做。
4. **不重复劳动** → 先核对再动手；上一个 Agent 已完成的部分（commit/产物）直接复用。
5. **失败必可视化** → 编译/测试/安装失败，直接贴错误摘要，不隐藏。
6. **用户个人信息保护** → 作者名/QQ 群/GitHub 等可公开；但**绝不上传** API 密钥、私钥、token、本机路径细节中的敏感项。

---

## 7. 总管的思维方式与思维链（整理给接班人，沿袭同一套打法）

> 这是总管与用户长期磨合出的工作方式。接班 Agent **照此思考与行动**，能无缝衔接。

### 7.1 六步思维链（每轮任务的内在流程）
1. **读现状**：不凭记忆猜。改代码前先 `read/grep` 相关文件、`git status`、查已装版本——一切以实际读到的为准。
2. **解析意图**：把用户的话翻译成"要实现的结果"（用户说的是目标，不是技术指令）；同时判断任务类型（界面/逻辑/bug/发布/文档）。
3. **建因果假设**：列出多个候选原因，按最可能排序去验证，不盯死一个。
4. **小步改动 + 立即验证**：改一处 → 编译/运行 → 看结果 → 不对就退回改。从不"凭感觉说写好了"。
5. **把技术结论翻译成人话**：用户非技术背景，用表格、"为什么"、大白话汇报。
6. **自检**：交付前过一遍——改动文件清单、副作用、要不要提交/发布、版本号是否同步。

### 7.2 任务分级与执行方式
| 任务 | 谁做 | 备注 |
|---|---|---|
| 架构设计 / 底层逻辑 / 协议 / 安装器 / 集成核对 | **总管自己** | 用户授权总管做"领导+主检查员" |
| 脏活累活（大批量代码实现、测试扩充、文档扫描） | **子代理**（免费模型 opencode-zen） | 先写 `docs/TASK-*.md` 自包含任务书再派发 |
| 小改动（几行的界面调整、版本号同步、文档更新） | **总管直接改** | 不派子代理，快 |
| 验收裁决 | **用户** | 每轮必须弹测试窗口给用户实测 |

### 7.3 防卡死机制（AGENT-PROCESS.md §3 的实践）
- 绝不无限重试：换模型重试 1 次 → 串行化 → 列问题清单给用户。任何分支最多 3 步。
- 疑问只问一次：能自主决定的小事直接做；只有"验收标准"和"提交/发布决策"必须问用户。
- 卡住必有状态：输出"当前卡在哪 + 你只需要做 X"。

### 7.4 最重要的自省教训（本次会话血泪）
**输出空转是唯一反复出现的严重事故**：表现 = 反复生成"我要调用工具/执行"之类的叙述，却始终不真正发出工具调用，页面像死循环。已两次发生，第二次长达数千行。
- **预防**：每次想输出"我要做 X"时，直接调用工具做 X；没有工具可调用时就给用户结论。
- **自救**：意识到在循环时，立即停止文字，立刻发出真实工具调用。
- **用户侧处理**：直接发"继续"或"你卡住了"，即可打断。
- **接班 Agent 接手后第一动作必须是真实工具调用**（`git status` 等），用结果证明活着。

---

> 本协议 v2.0 于 2026-08-18 更新（新增：项目快照、部署流程、独立测试窗口、思维链、防空转纪律、0.5.3 未发布状态），与 `AGENT-PROCESS.md`、`HANDOFF.md` 配套使用。
