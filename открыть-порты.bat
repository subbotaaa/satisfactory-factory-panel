@echo off
chcp 65001 >nul
rem Открывает порты 8085 (API мода) и 8086 (панель) в брандмауэре Windows,
rem чтобы панель открывалась с других устройств домашней сети.
rem Запустить ОДИН раз. Сам запросит права администратора.

net session >nul 2>&1
if errorlevel 1 (
  echo Запрашиваю права администратора...
  powershell -NoProfile -Command "Start-Process '%~f0' -Verb RunAs"
  exit /b
)

netsh advfirewall firewall add rule name="Satisfactory Panel (8086)" dir=in action=allow protocol=TCP localport=8086 profile=private,domain
netsh advfirewall firewall add rule name="Satisfactory FRM API (8085)" dir=in action=allow protocol=TCP localport=8085 profile=private,domain

echo.
echo Готово! Панель доступна с других устройств сети:
echo   http://IP-адрес-этого-ПК:8086/dashboard.html
echo (узнать IP: команда ipconfig, строка "IPv4-адрес")
pause
