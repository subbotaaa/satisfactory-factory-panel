@echo off
chcp 65001 >nul
rem Запускает сервер панели (порт 8086) и открывает её в браузере.
rem Требуется Python 3 (https://python.org). Если сервер уже работает — просто открывает браузер.
cd /d "%~dp0"

netstat -ano | findstr /r ":8086 .*LISTENING" >nul
if errorlevel 1 (
  where pythonw >nul 2>&1
  if not errorlevel 1 (
    start "" pythonw "%~dp0serve.py"
  ) else (
    start "Панель фабрики (порт 8086)" /min python "%~dp0serve.py"
  )
  ping -n 2 127.0.0.1 >nul
)

start "" "http://localhost:8086/dashboard.html"
