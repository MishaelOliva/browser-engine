@echo off
setlocal EnableDelayedExpansion
title MishaWeb All-in-One Installer

echo ==================================================================
echo              MISHAWEB ALL-IN-ONE ONE-CLICK INSTALLER              
echo ==================================================================
echo.

set SCRIPT_DIR=%~dp0
set LOCAL_SCRIPT=%SCRIPT_DIR%build\Install-MishaWeb.ps1

if exist "%LOCAL_SCRIPT%" (
    echo [INFO] Running installer from local repository package...
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%LOCAL_SCRIPT%" %*
) else (
    echo [INFO] Running standalone installer...
    echo [INFO] Downloading installation script from GitHub...
    powershell.exe -NoProfile -ExecutionPolicy Bypass -Command ^
        "$ErrorActionPreference = 'Stop';" ^
        "$tempDir = Join-Path $env:TEMP ('MishaWeb_Bootstrap_' + [System.Guid]::NewGuid().ToString('N'));" ^
        "New-Item -ItemType Directory -Path $tempDir -Force | Out-Null;" ^
        "$scriptPath = Join-Path $tempDir 'Install-MishaWeb.ps1';" ^
        "try {" ^
        "    [System.Net.ServicePointManager]::SecurityProtocol = [System.Net.SecurityProtocolType]::Tls12 -bor [System.Net.SecurityProtocolType]::Tls13;" ^
        "    try {" ^
        "        Invoke-WebRequest -Uri 'https://raw.githubusercontent.com/MishaelOliva/browser-engine/main/build/Install-MishaWeb.ps1' -OutFile $scriptPath -UseBasicParsing;" ^
        "    } catch {" ^
        "        Invoke-WebRequest -Uri 'https://raw.githubusercontent.com/MishaelOliva/browser-engine/master/build/Install-MishaWeb.ps1' -OutFile $scriptPath -UseBasicParsing;" ^
        "    }" ^
        "    & $scriptPath @args;" ^
        "} finally {" ^
        "    Remove-Item -Path $tempDir -Recurse -Force -ErrorAction SilentlyContinue;" ^
        "}"
)

if %ERRORLEVEL% NEQ 0 (
    echo.
    echo [ERROR] Installation exited with error code %ERRORLEVEL%.
    pause
    exit /b %ERRORLEVEL%
)

exit /b 0
