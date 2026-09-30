# 把仓库里新构建的伴生进程替换到 Program Files（需要管理员权限，会自动提权）。
#
# 用法：
#   双击 tools\更新伴生进程.cmd                    正常更新
#   powershell -File tools\update-companion.ps1 -Check   只检查、不改动（不需要管理员）
#
# 为什么要单独做这个脚本：
#   小组件用的伴生进程装在 C:\Program Files\KeyDisplay\，而且是以管理员权限常驻的
#   （安装器注册的计划任务以最高权限拉起）。普通权限下既杀不掉它、也覆盖不了那个文件，
#   所以升级伴生进程必须提权。这个脚本会把每一步写进日志，便于事后核对。

param([switch]$Check)

$ErrorActionPreference = 'Stop'
$src = (Resolve-Path (Join-Path $PSScriptRoot '..\KeyDisplay.Companion\dist\KeyDisplayCompanion.exe')).Path
$dst = Join-Path $env:ProgramFiles 'KeyDisplay\KeyDisplayCompanion.exe'
$log = Join-Path $env:TEMP 'keydisplay-companion-update.log'

function Write-Log([string]$msg) {
    $line = "{0}  {1}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $msg
    Write-Host $line
    try { Add-Content -LiteralPath $log -Value $line -Encoding UTF8 } catch { }
}

function Get-Info($path) {
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    $f = Get-Item -LiteralPath $path
    return [pscustomobject]@{ Path = $path; Size = $f.Length; Time = $f.LastWriteTime }
}

if ($Check) {
    Write-Host "==== 伴生进程更新检查（只读，不做任何改动）===="
    $s = Get-Info $src
    $d = Get-Info $dst
    if ($s) { Write-Host ("源（仓库新版）: {0}  {1:N2} MB" -f $s.Time, ($s.Size / 1MB)) }
    else { Write-Host "源（仓库新版）: 不存在 —— 需要先构建伴生进程" }
    if ($d) { Write-Host ("目标（已安装）: {0}  {1:N2} MB" -f $d.Time, ($d.Size / 1MB)) }
    else { Write-Host "目标（已安装）: 不存在" }
    if ($s -and $d) {
        if ($s.Time -gt $d.Time) { Write-Host "结论: 需要更新（仓库里的更新）" }
        else { Write-Host "结论: 已是最新（或目标比源更新）" }
    }
    $procs = @(Get-Process -Name 'KeyDisplayCompanion' -ErrorAction SilentlyContinue)
    Write-Host ("正在运行的伴生进程: {0} 个" -f $procs.Count)
    $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    Write-Host ("当前会话是否管理员: {0}（更新需要管理员）" -f $isAdmin)
    exit 0
}

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "需要管理员权限，正在请求提权（请在弹出的窗口点「是」）..."
    $args = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`""
    try {
        Start-Process powershell -Verb RunAs -Wait -ArgumentList $args
    } catch {
        Write-Host "提权被取消或失败：$($_.Exception.Message)"
        Write-Host "（也可以改为运行桌面的 KeyDisplaySetup.exe 安装包）"
        Read-Host "按回车关闭"
    }
    exit 0
}

Remove-Item -LiteralPath $log -Force -ErrorAction SilentlyContinue
Write-Log "==== 开始更新伴生进程（管理员会话）===="
Write-Log "源  : $src"
Write-Log "目标: $dst"

if (-not (Test-Path -LiteralPath $src)) {
    Write-Log "失败：源文件不存在（请先在仓库里构建伴生进程）"
    Read-Host "按回车关闭"; exit 1
}

# 1) 结束正在运行的伴生进程（最多重试 10 次，每次 400ms）
for ($i = 1; $i -le 10; $i++) {
    $p = @(Get-Process -Name 'KeyDisplayCompanion' -ErrorAction SilentlyContinue)
    if ($p.Count -eq 0) { break }
    if ($i -eq 1) { Write-Log ("结束现有伴生进程 {0} 个" -f $p.Count) }
    foreach ($x in $p) { try { Stop-Process -Id $x.Id -Force -ErrorAction Stop } catch { } }
    Start-Sleep -Milliseconds 400
}
& taskkill.exe /F /IM KeyDisplayCompanion.exe 2>&1 | Out-Null
Start-Sleep -Milliseconds 500
$left = @(Get-Process -Name 'KeyDisplayCompanion' -ErrorAction SilentlyContinue).Count
if ($left -gt 0) { Write-Log ("警告：仍有 {0} 个伴生进程在运行，可能覆盖失败" -f $left) }

# 2) 复制并校验
try {
    $s = Get-Info $src
    Copy-Item -LiteralPath $src -Destination $dst -Force
    $d = Get-Info $dst
    if ($d.Size -ne $s.Size) { throw ("复制后大小不一致：源 {0} 字节 / 目标 {1} 字节" -f $s.Size, $d.Size) }
    Write-Log ("已覆盖：{0:N2} MB，时间 {1}" -f ($d.Size / 1MB), $d.Time)
} catch {
    Write-Log "失败：$($_.Exception.Message)"
    Write-Log "（若提示文件被占用，请先关闭 Game Bar 再试；或运行桌面的 KeyDisplaySetup.exe）"
    Read-Host "按回车关闭"; exit 1
}

# 3) 重启
try {
    Start-Process -FilePath $dst -WindowStyle Hidden
    Start-Sleep -Seconds 2
    $now = @(Get-Process -Name 'KeyDisplayCompanion' -ErrorAction SilentlyContinue)
    Write-Log ("已重新启动，当前进程数 {0}" -f $now.Count)
} catch {
    Write-Log "警告：重启失败 $($_.Exception.Message)（打开 Game Bar 时会自动拉起）"
}

Write-Log "完成。日志已保存到：$log"
Write-Host ""
Write-Host "现在回到 Game Bar 里看一眼小组件：手柄组应出现在面板右下角（无需重开 Game Bar）。" -ForegroundColor Green
Read-Host "按回车关闭"
