# 构建指南

本仓库的开发环境无法编译 UWP/MSIX，因此分两部分构建：

- **伴生进程 EXE**：本机 Python + PyInstaller 即可。
- **UWP 小组件 APPX / Setup.exe**：需在装有 Visual Studio（含 UWP 工作负载）
  与 Windows SDK 的机器上执行。

## 0. 前置条件

| 组件 | 用途 |
|---|---|
| Python 3.12+（含 Pillow、PyInstaller） | 伴生进程、资源生成 |
| Visual Studio 2019/2022，勾选「通用 Windows 平台开发」 | 编译 UWP |
| Windows SDK（含 SignTool） | 签名 APPX |
| Inno Setup 6（可选） | 生成 Setup.exe |

## 1. 伴生进程 EXE

```powershell
cd KeyDisplay.Companion
powershell -ExecutionPolicy Bypass -File build.ps1
# 产物: dist\KeyDisplayCompanion.exe
```

脚本内部即 `pyinstaller --onefile --noconsole companion.py`。

冒烟测试：

```powershell
# 前台跑 15 秒，另开一个终端执行:
python test_client.py
# 应持续打印 20 字节快照，seq 递增
```

## 2. 生成 UWP 资源

```powershell
python tools\gen_assets.py
# 产物: KeyDisplay.Widget\Assets\*.png, KeyDisplay.Widget\GameBar\KeyDisplayMain.png
```

## 3. 编译 UWP 小组件

用 VS 打开 `KeyDisplay.Widget\KeyDisplay.Widget.sln`，选择 Release | x64，
`生成` 即可。或命令行：

```powershell
cd installer
.\build-msix.ps1 -Configuration Release -Arch x64
```

`build-msix.ps1` 会依次：

1. 运行 `gen_assets.py`；
2. 用 vswhere 定位 MSBuild，以侧载模式（`SideloadOnly`、不打 bundle）编译；
3. 定位 `KeyDisplay.Widget_*.appx` 产物；
4. 调用 `make-cert.ps1` 生成/复用自签名代码签名证书（导出 `.cer` / `.pfx`）；
5. 用 Windows SDK 的 SignTool 对 APPX 签名。

产物输出到 `dist\KeyDisplay.Install\`。

> 注意：包身份 `Publisher=CN=KeyDisplay, O=KeyDisplay, C=CN` 是固定的。
> 若改动 Publisher，必须同步更新 `Package.appxmanifest`、`make-cert.ps1`
> 与安装脚本中的主题匹配逻辑。

## 4. Setup.exe（Inno Setup）

1. 先完成第 1、3 步（得到 `KeyDisplayCompanion.exe` 与已签名的 `.appx`）。
2. 安装 Inno Setup 6。
3. 用 Inno 编译 `installer\setup.iss`（或 `iscc setup.iss`）。

产物：`dist\KeyDisplay.Setup\KeyDisplaySetup.exe`。

`setup.iss` 安装时会把伴生进程、证书、APPX 复制到 `%ProgramFiles%\KeyDisplay`，
然后调用 `install-msix.ps1` 完成证书信任 + `Add-AppxPackage` + `keydisplay`
协议注册 + 写 `config.json`；卸载时由 Inno 删除文件与注册表，并内联
PowerShell 移除 UWP 包与证书。

## 5. 端到端联调（在目标机器上）

```powershell
# 1) 启动伴生进程（也可先跳过，打开小组件时会自动拉起）
& "C:\Program Files\KeyDisplay\KeyDisplayCompanion.exe"

# 2) Win+G 打开 Game Bar，固定/打开「按键显示」
#    右键小组件 → 「启动数据采集」可手动拉起伴生进程
#    右键小组件 → 切换亮色/暗色模式
```

验证要点：

- 按 Q/W/E/R/A/S/D/F/Shift/Ctrl/Alt/空格，对应键帽应反色（白底黑字）。
- 移动鼠标：垫上的光标点实时跟随；左/中/右/侧键按下反色。
- 游戏前台运行时光标点同样跟随。