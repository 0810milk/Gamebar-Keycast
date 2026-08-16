# 卸载：
#   1. 移除 UWP 小组件包
#   2. 删除 keydisplay 协议注册
#   3. 删除 %ProgramFiles%\KeyDisplay
#   4. 从"受信任的人"移除签名证书
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

Write-Host "==> 1/4 移除 UWP 小组件包"
$pkg = Get-AppxPackage -Name "KeyDisplay.Widget"
if ($pkg) {
    Remove-AppxPackage -Package $pkg.PackageFullName
    Write-Host "已移除: $($pkg.PackageFullName)"
}
else {
    Write-Host "未安装 UWP 包，跳过"
}

Write-Host "==> 2/4 删除 keydisplay 协议注册"
Remove-Item -Path "HKCU:\Software\Classes\keydisplay" -Recurse -Force -ErrorAction SilentlyContinue

Write-Host "==> 3/4 删除伴生进程目录"
$dir = Join-Path $env:ProgramFiles "KeyDisplay"
if (Test-Path $dir) {
    Remove-Item -Path $dir -Recurse -Force
    Write-Host "已删除: $dir"
}
else {
    Write-Host "未找到 $dir，跳过"
}

Write-Host "==> 4/4 移除受信任的人中的签名证书"
$c = Get-ChildItem Cert:\LocalMachine\TrustedPeople -ErrorAction SilentlyContinue |
    Where-Object { $_.Subject -like "*CN=KeyDisplay*" }
if ($c) {
    $c | Remove-Item
    Write-Host "已移除证书: $($c.Subject)"
}
else {
    Write-Host "未找到相关证书，跳过"
}

Write-Host ""
Write-Host "卸载完成。"