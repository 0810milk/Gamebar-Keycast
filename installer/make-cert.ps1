# 生成自签名代码签名证书（用于 MSIX/APPX 侧载签名），并导出 .cer 与 .pfx。
#
# 用法:
#   .\make-cert.ps1 [-OutDir <dir>] [-PfxPassword <密码>] [-Subject <主题>]
#
# 说明:
#   - 证书只安装到当前用户的 "我的" 存储。
#   - .cer 需在安装时导入到 "受信任的人"，Windows 才会接受侧载包的签名。
#   - .pfx 用于 SignTool 对 .appx 签名。

param(
    [string]$OutDir = (Join-Path $PSScriptRoot "..\dist\KeyDisplay.Install"),
    [string]$PfxPassword = "KeyDisplayDev!",
    [string]$Subject = "CN=KeyDisplay, O=KeyDisplay, C=CN"
)

$ErrorActionPreference = "Stop"

$root = Split-Path $PSScriptRoot -Parent
$certDir = Join-Path $root "cert"

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
New-Item -ItemType Directory -Force -Path $certDir | Out-Null

$existing = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert |
    Where-Object { $_.Subject -eq $Subject } |
    Select-Object -First 1

if ($existing) {
    $cert = $existing
    Write-Host "使用已有证书: $($cert.Subject) (有效期至 $($cert.NotAfter))"
}
else {
    Write-Host "生成新自签名代码签名证书: $Subject"
    $cert = New-SelfSignedCertificate `
        -Type CodeSigningCert `
        -Subject $Subject `
        -CertStoreLocation Cert:\CurrentUser\My `
        -KeyExportPolicy Exportable `
        -KeySpec Signature `
        -NotAfter (Get-Date).AddYears(3)
}

$cerPath = Join-Path $certDir "KeyDisplay.cer"
Export-Certificate -Cert $cert -FilePath $cerPath -Force | Out-Null
Write-Host "已导出: $cerPath"

$pfxPath = Join-Path $certDir "KeyDisplay.pfx"
if (-not (Test-Path $pfxPath)) {
    $sec = ConvertTo-SecureString -String $PfxPassword -AsPlainText -Force
    Export-PfxCertificate -Cert $cert -FilePath $pfxPath -Password $sec -Force | Out-Null
    Write-Host "已导出: $pfxPath"
}

# 供其他脚本读取的路径信息
Write-Output "CERT_CER=$cerPath"
Write-Output "CERT_PFX=$pfxPath"
Write-Output "CERT_PASSWORD=$PfxPassword"