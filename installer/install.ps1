# 手动安装（非 Inno Setup）：
#   1. 将伴生进程复制到 %ProgramFiles%\KeyDisplay
#   2. 调用 install-msix.ps1（证书 + APPX + 协议 + config.json）
#
# 用法（建议以管理员身份运行，脚本会自动提权）:
#   .\install.ps1 -Appx <签名后的 .appx> [-Cert <证书.cer>] [-CompanionExe <exe>]

param(
    [Parameter(Mandatory = $true)][string]$Appx,
    [string]$Cert = (Join-Path $PSScriptRoot "..\cert\KeyDisplay.cer"),
    [string]$CompanionExe = (Join-Path $PSScriptRoot "..\KeyDisplay.Companion\dist\KeyDisplayCompanion.exe")
)

$ErrorActionPreference = "Stop"

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "请求管理员权限..."
    Start-Process powershell -Verb RunAs -ArgumentList @(
        "-NoProfile", "-ExecutionPolicy", "Bypass",
        "-File", "`"$PSCommandPath`"",
        "-Appx", "`"$Appx`"",
        "-Cert", "`"$Cert`"",
        "-CompanionExe", "`"$CompanionExe`""
    )
    exit
}

if (-not (Test-Path $Appx)) { throw "找不到 APPX: $Appx" }
if (-not (Test-Path $Cert)) { throw "找不到证书: $Cert（请先运行 make-cert.ps1）" }
if (-not (Test-Path $CompanionExe)) { throw "找不到伴生进程: $CompanionExe（请先运行 KeyDisplay.Companion\build.ps1）" }

$targetDir = Join-Path $env:ProgramFiles "KeyDisplay"
New-Item -ItemType Directory -Force -Path $targetDir | Out-Null
Copy-Item -Path $CompanionExe -Destination $targetDir -Force
$installedExe = Join-Path $targetDir (Split-Path $CompanionExe -Leaf)
Write-Host "伴生进程已复制到: $installedExe"

& (Join-Path $PSScriptRoot "install-msix.ps1") `
    -AppxPath $Appx `
    -CertPath $Cert `
    -CompanionExe $installedExe
if ($LASTEXITCODE -ne 0) { throw "install-msix.ps1 失败" }