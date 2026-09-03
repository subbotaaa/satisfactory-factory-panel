# Сборка и установка мода BigAmbitionsMonitor в ModsLocal игры.
# Запуск: powershell -ExecutionPolicy Bypass -File install.ps1
param(
    [string]$GameManagedDir = "D:\Big.Ambitions.v1.0\Big Ambitions\Big Ambitions_Data\Managed"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

Write-Host "Сборка мода..." -ForegroundColor Cyan
dotnet build "$root\mod\BigAmbitionsMonitor.csproj" -c Release -p:GameManagedDir="$GameManagedDir"
if ($LASTEXITCODE -ne 0) { throw "Сборка не удалась" }

$localLow = "$env:USERPROFILE\AppData\LocalLow"
$modsLocal = Join-Path $localLow "Hovgaard Games\Big Ambitions\ModsLocal"
$target = Join-Path $modsLocal "BigAmbitionsMonitor"

New-Item -ItemType Directory -Force $target | Out-Null
Copy-Item "$root\mod\bin\Release\BigAmbitionsMonitor.dll" $target -Force
Copy-Item "$root\dashboard.html" $target -Force

Write-Host "Мод установлен в: $target" -ForegroundColor Green
Write-Host "Запустите игру, включите мод в меню модов и загрузите сейв."
Write-Host "Панель: http://localhost:8113/"
