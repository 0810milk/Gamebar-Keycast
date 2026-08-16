# 构建并签名 UWP 小组件 APPX（侧载包），产物输出到 dist\KeyDisplay.Install。
#
# 依赖:
#   - Visual Studio（含 UWP 工作负载）
#   - Windows SDK（含 SignTool）
#   - Python 3 + Pillow（用于生成资源）
#
# 用法:
#   .\build-msix.ps1 [-Configuration Release] [-Arch x64]

param(
    [string]$Configuration = "Release",
    [ValidateSet("x86", "x64", "arm64")] [string]$Arch = "x64"
)

$ErrorActionPreference = "Stop"

$root = Split-Path $PSScriptRoot -Parent
$sln = Join-Path $root "KeyDisplay.Widget\KeyDisplay.Widget.sln"
$outDir = Join-Path $root "dist\KeyDisplay.Install"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

Write-Host "==> 生成 UWP 资源"
& python (Join-Path $root "tools\gen_assets.py")
if ($LASTEXITCODE -ne 0) { throw "gen_assets.py 失败" }

Write-Host "==> 定位 MSBuild"
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) { throw "未找到 vswhere，请安装 Visual Studio" }
$msbuild = & $vswhere -latest -requires Microsoft.Component.MSBuild `
    -find "MSBuild\**\Bin\MSBuild.exe" | Select-Object -First 1
if (-not $msbuild) { throw "未找到 MSBuild，请安装 Visual Studio（含 UWP 工作负载）" }
Write-Host "MSBuild: $msbuild"

Write-Host "==> MSBuild 构建 (侧载模式)"
& $msbuild $sln /t:Restore,Build `
    /p:Configuration=$Configuration `
    /p:Platform=$Arch `
    /p:AppxBundle=Never `
    /p:UapAppxPackageBuildMode=SideloadOnly `
    /p:AppxPackageSigningEnabled=false `
    /v:m
if ($LASTEXITCODE -ne 0) { throw "MSBuild 失败（错误码 $LASTEXITCODE）" }

Write-Host "==> 定位 APPX 产物"
$bin = Join-Path $root "KeyDisplay.Widget\bin\$Arch\$Configuration"
$appx = Get-ChildItem -Path $bin -Recurse -Filter "KeyDisplay.Widget_*.appx" -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $appx) { throw "未找到 KeyDisplay.Widget_*.appx 产物" }
Write-Host "APPX: $($appx.FullName)"

Write-Host "==> 生成并导出签名证书"
$certOut = & (Join-Path $PSScriptRoot "make-cert.ps1") -OutDir $outDir
$cerPath = ($certOut | Where-Object { $_ -like "CERT_CER=*" }).Replace("CERT_CER=", "")
$pfxPath = ($certOut | Where-Object { $_ -like "CERT_PFX=*" }).Replace("CERT_PFX=", "")
$pfxPwd  = ($certOut | Where-Object { $_ -like "CERT_PASSWORD=*" }).Replace("CERT_PASSWORD=", "")

Write-Host "==> 定位 SignTool"
$signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Recurse `
    -Filter "signtool.exe" -ErrorAction SilentlyContinue |
    Sort-Object FullName -Descending | Select-Object -First 1
if (-not $signtool) { throw "未找到 signtool，请安装 Windows SDK" }

Write-Host "==> 签名 APPX"
$dest = Join-Path $outDir $appx.Name
Copy-Item -Path $appx.FullName -Destination $dest -Force
$sec = ConvertTo-SecureString -String $pfxPwd -AsPlainText -Force
$tmpPwd = (New-Object System.Management.Automation.PSCredential "x", $sec).GetNetworkCredential().Password
& $signtool.FullName sign /fd SHA256 /f $pfxPath /p $tmpPwd $dest
if ($LASTEXITCODE -ne 0) { throw "签名失败" }

Write-Host ""
Write-Host "完成。安装包: $dest"
Write-Host "证书: $cerPath（安装时需导入到受信任的人）"
Write-Host "下一步: .\install.ps1 -Appx $dest -Cert $cerPath -CompanionExe <KeyDisplayCompanion.exe>"