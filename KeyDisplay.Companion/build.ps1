# PyInstaller 打包脚本
# 用法：powershell -ExecutionPolicy Bypass -File build.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

# 检查 PyInstaller
if (-not (python -c "import PyInstaller" 2>$null)) {
    Write-Host "正在安装 PyInstaller..."
    pip install pyinstaller
}

Write-Host "正在构建 KeyDisplayCompanion.exe ..."
python -m PyInstaller `
    --onefile `
    --noconsole `
    --clean `
    --name KeyDisplayCompanion `
    --collect-all ctypes `
    companion.py

Write-Host "完成。产物：dist\KeyDisplayCompanion.exe"