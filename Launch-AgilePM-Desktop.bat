@echo off
title Launching AgilePM Desktop App...
cd /d "%~dp0"

if exist "AutoPM_Desktop\bin\Debug\net10.0-windows\AutoPM_Desktop.exe" (
    start "" "AutoPM_Desktop\bin\Debug\net10.0-windows\AutoPM_Desktop.exe"
    exit /b
)

echo Building and starting AgilePM Desktop...
dotnet run --project "%~dp0AutoPM_Desktop\AutoPM_Desktop.csproj"
