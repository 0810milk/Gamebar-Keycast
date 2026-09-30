@echo off
rem Update the installed companion exe (self-elevating).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0update-companion.ps1"
