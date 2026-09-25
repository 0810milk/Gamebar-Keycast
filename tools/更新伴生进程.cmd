@echo off
chcp 65001 >nul
echo 正在以管理员身份更新 KeyDisplay 伴生进程...
powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process powershell -Verb RunAs -Wait -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-Command','Stop-Process -Name KeyDisplayCompanion -Force -ErrorAction SilentlyContinue; Start-Sleep -Milliseconds 600; Copy-Item -LiteralPath \"%~dp0..\KeyDisplay.Companion\dist\KeyDisplayCompanion.exe\" -Destination \"$env:ProgramFiles\KeyDisplay\KeyDisplayCompanion.exe\" -Force; Start-Sleep -Milliseconds 300; Start-Process \"$env:ProgramFiles\KeyDisplay\KeyDisplayCompanion.exe\" -WindowStyle Hidden; Write-Host 更新完成 -ForegroundColor Green; Start-Sleep -Seconds 2'"
echo 完成（若提示已完成即可回到 Game Bar 测试）
pause
