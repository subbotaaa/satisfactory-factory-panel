@echo off
chcp 65001 >nul
title Big Ambitions Monitor
rem Собирает (при необходимости) и запускает внешний ридер сейвов.
rem Панель откроется на http://localhost:8113/

set "ROOT=%~dp0"
set "READER=%ROOT%reader\bin\Release\net8.0-windows\BigAmbitionsReader.exe"

if not exist "%READER%" (
  echo Сборка ридера...
  dotnet build "%ROOT%reader\BigAmbitionsReader.csproj" -c Release || goto :err
)

start "" http://localhost:8113/
"%READER%" %*
goto :eof

:err
echo.
echo Не удалось собрать ридер. Нужен .NET SDK 8+ (https://dotnet.microsoft.com/download).
pause
