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
$resolved = Get-Item -Path $AppxPath -ErrorAction SilentlyContinue |
    Select-Object -First 1
if (-not $resolved) { throw "找不到 APPX: $AppxPath" }
$AppxPath = $resolved.FullName
Write-Host "APPX: $AppxPath"

Write-Host "==> 1/4 信任证书: certutil -addstore TrustedPeople"
& certutil -addstore "TrustedPeople" $CertPath | Out-Null
if ($LASTEXITCODE -ne 0) { throw "证书导入失败（错误码 $LASTEXITCODE）" }

Write-Host "==> 2/4 安装/更新 APPX: $AppxPath"
$existing = Get-AppxPackage -Name "KeyDisplay.Widget"
if ($existing) {
    Add-AppxPackage -Path $AppxPath -ForceApplicationShutdown
}
else {
    Add-AppxPackage -Path $AppxPath
}
if (-not $?) { throw "APPX 安装失败" }

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