# Сборка и установка плагина BepInEx (монитор в реальном времени).
# Требует уже установленный BepInEx в папке игры.
# Запуск: powershell -ExecutionPolicy Bypass -File install-plugin.ps1
param(
    [string]$GameDir = "D:\Big.Ambitions.v1.0\Big Ambitions"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$managed = Join-Path $GameDir "Big Ambitions_Data\Managed"
$bepCore = Join-Path $GameDir "BepInEx\core"

if (-not (Test-Path $bepCore)) {
    throw "BepInEx не найден в '$bepCore'. Сначала установите BepInEx x64 в папку игры."
}

Write-Host "Сборка плагина..." -ForegroundColor Cyan
dotnet build "$root\bepinex-plugin\BigAmbitionsMonitorPlugin.csproj" -c Release `
    -p:GameManagedDir="$managed" -p:BepInExCoreDir="$bepCore"
if ($LASTEXITCODE -ne 0) { throw "Сборка не удалась" }

$target = Join-Path $GameDir "BepInEx\plugins\BigAmbitionsMonitor"
New-Item -ItemType Directory -Force $target | Out-Null
Copy-Item "$root\bepinex-plugin\bin\Release\BigAmbitionsMonitor.dll" $target -Force
Copy-Item "$root\dashboard.html" $target -Force

Write-Host "Плагин установлен: $target" -ForegroundColor Green
Write-Host "Запустите игру, загрузите сейв, откройте http://localhost:8113/"
