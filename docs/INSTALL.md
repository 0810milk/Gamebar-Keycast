# 安装 / 卸载指南

## 方式一：Setup.exe（推荐，支持控制面板卸载）

由 `installer\setup.iss` 编译生成，见 [BUILD.md](BUILD.md)。

- 安装：运行 `KeyDisplaySetup.exe`，按向导完成。安装程序会自动：
  1. 复制伴生进程、证书、APPX 到 `%ProgramFiles%\KeyDisplay`；
  2. 将证书 `KeyDisplay.cer` 导入本机「受信任的人」；
  3. `Add-AppxPackage` 安装 UWP 小组件；
  4. 注册 `keydisplay://` 协议（指向伴生进程）；
  5. 写入 `config.json`（含包 Family Name，用于管道 DACL 放行）。
- 卸载：控制面板 → 程序 → 卸载「按键显示」，或运行
  `%ProgramFiles%\KeyDisplay\unins000.exe`。卸载会移除 UWP 包、协议注册、
  证书与程序文件。

## 方式二：脚本安装（无 Inno Setup 时）

先准备好产物：

```powershell
# 伴生进程 EXE
cd KeyDisplay.Companion
powershell -ExecutionPolicy Bypass -File build.ps1

# 签名后的 APPX（需要 VS + Windows SDK）
cd ..\installer
.\build-msix.ps1
```

然后安装（自动提权）：

```powershell
cd ..\installer
.\install.ps1 -Appx ..\dist\KeyDisplay.Install\KeyDisplay.Widget_x64.appx
```

脚本执行：复制伴生进程到 `%ProgramFiles%\KeyDisplay` →
调用 `install-msix.ps1`（证书 + APPX + 协议 + config.json）。

卸载：

```powershell
.\uninstall.ps1
```

## 使用

1. 按 `Win+G` 打开 Game Bar。
2. 打开「按键显示」小组件（或固定到 Game Bar）。
3. 小组件打开时会自动通过 `keydisplay://start` 拉起伴生进程；若未拉起，
   在小组件上右键 → 「启动数据采集」。
4. 小组件右上角显示「未连接」说明伴生进程未运行；正常时为空。

## 主题

小组件内右键 → 切换亮色 / 暗色模式，选择保存在
`ApplicationData\LocalSettings`，下次打开沿用。

## 故障排查

| 现象 | 处理 |
|---|---|
| 小组件显示「未连接」 | 手动运行 `%ProgramFiles%\KeyDisplay\KeyDisplayCompanion.exe`，或在小组件右键「启动数据采集」 |
| 键盘按键不响应 | 确认游戏为前台运行；全局钩子只对前台会话生效。侧键/修饰键需实体键测试 |
| 安装时提示证书不受信任 | 确认 `KeyDisplay.cer` 已导入本机「受信任的人」（安装脚本已自动完成） |
| 更新小组件 | 重新运行 Setup.exe 即可覆盖安装（`Add-AppxPackage -ForceApplicationShutdown`） |
| 卸载残留 | 运行 `installer\uninstall.ps1`，再手动删除 `%ProgramFiles%\KeyDisplay` |