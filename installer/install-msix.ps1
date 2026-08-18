# 注册已复制到目标目录的组件：
#   1. 将证书导入"受信任的人"
#   2. 安装/更新 UWP 小组件 APPX
#   3. 注册 keydisplay 协议 -> 伴生进程
#   4. 写入 config.json（packageFamilyName，用于管道 DACL 放行 UWP 包 SID）
#
# 用法（由 install.ps1 或 Inno Setup [Run] 调用）:
#   .\install-msix.ps1 -AppxPath <*.appx> -CertPath <*.cer> -CompanionExe <exe路径>

param(
    [Parameter(Mandatory = $true)][string]$AppxPath,
    [Parameter(Mandatory = $true)][string]$CertPath,
    [Parameter(Mandatory = $true)][string]$CompanionExe
)

$ErrorActionPreference = "Stop"

Write-Host "==> 0/4 解析 APPX 路径"
# ❗ 历史事故（0.5.2-beta 装成 0.4.1）：*.msix 通配符匹配到多个文件时，
#   旧实现按字母序取第一个（1.1.0.0 < 1.2.2.0）→ 装到残留的老版本。
#   现在：多文件直接报错（发布流程必须保证目录只含最新一个 msix）。
$resolved = @(Get-Item -Path $AppxPath -ErrorAction SilentlyContinue)
if ($resolved.Count -eq 0) { throw "找不到 APPX: $AppxPath" }
if ($resolved.Count -gt 1) {
    $names = ($resolved | ForEach-Object { $_.Name }) -join ", "
    throw "appx 目录存在多个 msix（$names）——请清理只保留最新一个后再安装，否则可能装到旧版本！"
}
$AppxPath = $resolved[0].FullName
Write-Host "APPX: $AppxPath"
Write-Host "APPX: $AppxPath"

Write-Host "==> 1/4 信任证书: certutil -addstore TrustedPeople"
& certutil -addstore "TrustedPeople" $CertPath | Out-Null
if ($LASTEXITCODE -ne 0) { throw "证书导入失败（错误码 $LASTEXITCODE）" }

Write-Host "==> 2/4 安装/更新 APPX（先确保旧包彻底移除，避免残留）: $AppxPath"

# 前置清理：结束可能占用包的进程（widget / 宿主缓存）
Write-Host "---- 结束残留进程（widget / Game Bar 宿主缓存）----"
Get-Process | Where-Object { $_.Name -like 'KeyDisplay.Widget*' } |
    Stop-Process -Force -ErrorAction SilentlyContinue
Get-Process | Where-Object { $_.Name -in @('GameBar','GameBarFTServer','GameBarPresenter') } |
    Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 800

# 强制移除现有包（无论版本，保证全新加载，避免旧包/旧块图残留导致"装完还在旧版"）
$existing = Get-AppxPackage -Name "KeyDisplay.Widget"
if ($existing) {
    Write-Host "---- 移除现有包: $($existing.PackageFullName) ----"
    $retries = 0
    do {
        try { Remove-AppxPackage -Package $existing.PackageFullName -ErrorAction Stop } catch {
            Write-Host "移除失败($retries): $($_.Exception.Message)"
        }
        Start-Sleep -Seconds 2
        # 移除后包可能仍在"待处理"状态，等待其真正消失
        $remaining = Get-AppxPackage -Name "KeyDisplay.Widget" -ErrorAction SilentlyContinue
        $retries++
    } while ($remaining -and $retries -lt 5)
    if ($remaining) {
        throw "无法完全移除旧包 $($existing.PackageFullName)，存在残留。请手动结束 GameBar 后重试。"
    }
    Write-Host "---- 旧包已完全移除 ----"
}

# 安装新包
Write-Host "---- 安装新包 ----"
Add-AppxPackage -Path $AppxPath
if (-not $?) { throw "APPX 安装失败（错误码 $LASTEXITCODE）" }

# 验证安装成功
$installed = Get-AppxPackage -Name "KeyDisplay.Widget"
if (-not $installed) { throw "APPX 安装后未找到包，安装失败" }
Write-Host "---- 安装成功: $($installed.PackageFullName) ----"

Write-Host "==> 3/4 注册 keydisplay 协议"
$regBase = "HKCU:\Software\Classes\keydisplay"
New-Item -Path $regBase -Force | Out-Null
Set-ItemProperty -Path $regBase -Name "(default)" -Value "URL:KeyDisplay"
New-ItemProperty -Path $regBase -Name "URL Protocol" -Value "" -PropertyType String -Force | Out-Null
$cmd = "`"$CompanionExe`" `"%1`""
New-Item -Path "$regBase\shell\open\command" -Force | Out-Null
Set-ItemProperty -Path "$regBase\shell\open\command" -Name "(default)" -Value $cmd
New-Item -Path "$regBase\DefaultIcon" -Force | Out-Null
Set-ItemProperty -Path "$regBase\DefaultIcon" -Name "(default)" -Value "`"$CompanionExe`,0`""

Write-Host "==> 4/4 写入 config.json"
$pkg = Get-AppxPackage -Name "KeyDisplay.Widget"
if (-not $pkg) { throw "未找到已安装的 KeyDisplay.Widget 包" }
$pfn = $pkg.PackageFamilyName
$cfgPath = Join-Path (Split-Path $CompanionExe -Parent) "config.json"
$cfg = @{ packageFamilyName = $pfn; fps = 240 } | ConvertTo-Json
# UTF-8 无 BOM：PowerShell 5.1 的 Set-Content -Encoding UTF8 会写 BOM，
# 导致 Python json 解析失败（fps 等配置不生效）。
[System.IO.File]::WriteAllText($cfgPath, $cfg, (New-Object System.Text.UTF8Encoding $false))
Write-Host "config.json: $cfgPath (packageFamilyName=$pfn, fps=240)"

Write-Host ""
Write-Host "安装完成。按 Win+G 打开 Game Bar，在小组件中固定/打开「按键显示」。"
Write-Host "（组件的图标名与 AppExtension Id 一致为 KeyDisplayMain）"