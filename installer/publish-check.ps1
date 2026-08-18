# publish-check.ps1 - Pre-publish gate check (run BEFORE building Setup.exe)
# Purpose: prevent the 0.5.2-beta release accident class from ever recurring:
#   (1) stale msix in dist\KeyDisplay.Install got packaged by *.msix wildcard
#       and installed as an OLD version (letter order picks 1.1.0.0 first)
#   (2) install-msix.ps1 lost its UTF-8 BOM -> elevated PS 5.1 read it as ANSI
#       -> mojibake -> syntax error -> installer script never actually ran
#   (3) msix signature was invalid -> package could not install at all
#   (4) disk file != committed git file (build used a dirty working copy)
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File installer\publish-check.ps1
# Exit code 0 = ALL PASS, safe to publish. Exit code 1 = DO NOT PUBLISH.
# This script contains ASCII only on purpose (BOM-independent).

param(
    [string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = "Stop"
$failCount = 0

function Check([string]$name, [bool]$ok, [string]$detail) {
    if ($ok) { Write-Host ("[PASS] " + $name + " - " + $detail) }
    else {
        Write-Host ("[FAIL] " + $name + " - " + $detail)
        $script:failCount++
    }
}

Write-Host "== publish-check: $ProjectRoot =="

# --- 1. dist\KeyDisplay.Install must contain exactly ONE msix -----------------
$dist = Join-Path $ProjectRoot "dist\KeyDisplay.Install"
$msixes = @(Get-ChildItem -Path $dist -Filter "*.msix" -ErrorAction SilentlyContinue)
if ($msixes.Count -eq 0) {
    Check "dist msix" $false "no msix found in $dist"
} else {
    Check "dist msix" ($msixes.Count -eq 1) ("found " + $msixes.Count + " msix files; must be exactly 1 (wildcard would embed all)")
    if ($msixes.Count -eq 1) {
        Write-Host ("      msix: " + $msixes[0].Name + " (" + $msixes[0].Length + " bytes)")
    } else {
        $msixes | ForEach-Object { Write-Host ("      found: " + $_.Name) }
    }
}

# --- 2. msix signature must be valid (signtool verify /pa) ---------------------
if ($msixes.Count -eq 1) {
    $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Recurse -Filter "signtool.exe" -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending | Select-Object -First 1
    if (-not $signtool) {
        Check "msix signature" $false "signtool.exe not found"
    } else {
        & $signtool.FullName verify /pa $msixes[0].FullName *> $null
        Check "msix signature" ($LASTEXITCODE -eq 0) ("signtool verify /pa exit=" + $LASTEXITCODE)
    }
}

# --- 3. installer\install-msix.ps1 must carry UTF-8 BOM ------------------------
$ps1 = Join-Path $ProjectRoot "installer\install-msix.ps1"
if (Test-Path $ps1) {
    $b = [System.IO.File]::ReadAllBytes($ps1)
    $hasBom = ($b.Length -ge 3 -and $b[0] -eq 0xEF -and $b[1] -eq 0xBB -and $b[2] -eq 0xBF)
    Check "install-msix.ps1 BOM" $hasBom ("first bytes " + $b[0].ToString("X2") + " " + $b[1].ToString("X2") + " " + $b[2].ToString("X2"))
} else {
    Check "install-msix.ps1 BOM" $false "file missing"
}

# --- 4. build inputs must match committed git (no dirty disk copies) -----------
$gitRoot = Join-Path $ProjectRoot ".git"
if (Test-Path $gitRoot) {
    $dirty = @(git -C $ProjectRoot status --porcelain -- "installer/install-msix.ps1" "installer/setup.iss" "installer/publish-check.ps1")
    Check "git clean (installer files)" ($dirty.Count -eq 0) ("uncommitted: " + ($dirty -join "; "))
} else {
    Write-Host "      (no .git found - git check skipped)"
}

# --- summary -------------------------------------------------------------------
if ($failCount -eq 0) {
    Write-Host ""
    Write-Host "ALL CHECKS PASSED - safe to build Setup.exe and publish."
    exit 0
} else {
    Write-Host ""
    Write-Host ("FAILED: " + $failCount + " check(s) - DO NOT PUBLISH. Fix and re-run.")
    exit 1
}