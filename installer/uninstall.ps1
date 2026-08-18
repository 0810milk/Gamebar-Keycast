# 卸载：
#   1. 结束残留进程（widget / 伴生 / GameBar 缓存）
#   2. 移除 UWP 小组件包
#   3. 删除 keydisplay 协议注册
#   4. 删除 %ProgramFiles%\KeyDisplay
#   5. 从"受信任的人"移除签名证书
#
# 用法（自动提权）:
#   .\uninstall.ps1

$ErrorActionPreference = "Stop"

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "请求管理员权限..."
    Start-Process powershell -Verb RunAs -ArgumentList @(
        "-NoProfile", "-ExecutionPolicy", "Bypass",
        "-File", "`"$PSCommandPath`""
    )
    exit
}

Write-Host "==> 0/5 结束残留进程（widget / 伴生 / Game Bar 缓存）"
Get-Process | Where-Object { $_.Name -like 'KeyDisplay*' } |
    Stop-Process -Force -ErrorAction SilentlyContinue
Get-Process | Where-Object { $_.Name -in @('GameBar', 'GameBarFTServer', 'GameBarPresenter') } |
    Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 800
Write-Host "残留进程已结束"

Write-Host "==> 1/5 移除 UWP 小组件包"
$pkg = Get-AppxPackage -Name "KeyDisplay.Widget"
if ($pkg) {
    $retries = 0
    do {
        try { Remove-AppxPackage -Package $pkg.PackageFullName -ErrorAction Stop } catch {
            Write-Host "移除尝试 $retries 失败: $($_.Exception.Message)"
        }
        Start-Sleep -Seconds 2
        $remaining = Get-AppxPackage -Name "KeyDisplay.Widget" -ErrorAction SilentlyContinue
        $retries++
    } while ($remaining -and $retries -lt 5)
    if ($remaining) {
        Write-Warning "包移除后仍有残留，请重启后再次卸载"
    }
    else {
        Write-Host "已移除: $($pkg.PackageFullName)"
    }
}
else {
    Write-Host "未安装 UWP 包，跳过"
}

Write-Host "==> 2/5 删除 keydisplay 协议注册"
Remove-Item -Path "HKCU:\Software\Classes\keydisplay" -Recurse -Force -ErrorAction SilentlyContinue

Write-Host "==> 3/5 删除伴生进程目录"
$dir = Join-Path $env:ProgramFiles "KeyDisplay"
if (Test-Path $dir) {
    Remove-Item -Path $dir -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "已删除: $dir"
}
else {
    Write-Host "未找到 $dir，跳过"
}

Write-Host "==> 4/5 移除受信任的人中的签名证书"
$c = Get-ChildItem Cert:\LocalMachine\TrustedPeople -ErrorAction SilentlyContinue |
    Where-Object { $_.Subject -like "*CN=KeyDisplay*" }
if ($c) {
    $c | Remove-Item -ErrorAction SilentlyContinue
    Write-Host "已移除证书: $($c.Subject)"
}
else {
    Write-Host "未找到相关证书，跳过"
}

# 清除 UWP 包遗留的本地配置目录（LocalSettings），避免重装后读到旧自定义布局
Write-Host "==> 5/5 清理本地配置残留"
$pkgData = Join-Path $env:LOCALAPPDATA "Packages\KeyDisplay.Widget_hdjf4fqmxxv8g"
if (Test-Path $pkgData) {
    Remove-Item -Path $pkgData -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "已清理: $pkgData"
}

Write-Host ""
Write-Host "卸载完成。建议重启一下 Game Bar（Win+G）或重启电脑以确保完全清除。"
