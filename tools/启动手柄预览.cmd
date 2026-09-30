@echo off
rem 手柄按键显示 —— 预览窗口（不需要 Game Bar）
rem 依赖：仅需 Python 3（标准库 tkinter），无需安装任何第三方包
setlocal
set "PYW=%LOCALAPPDATA%\Programs\Python\Python312\pythonw.exe"
set "PY=%LOCALAPPDATA%\Programs\Python\Python312\python.exe"
if exist "%PYW%" (
  start "" "%PYW%" "%~dp0pad_preview.py" %*
) else if exist "%PY%" (
  start "" "%PY%" "%~dp0pad_preview.py" %*
) else (
  start "" pythonw "%~dp0pad_preview.py" %*
)
endlocal