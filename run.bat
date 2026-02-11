@echo off
cd /d "%~dp0"
chcp 65001 >nul
cls
echo.
echo ===============================================================
echo    NOICE - The Digital Void (C# Speed Up Edition)
echo ===============================================================
echo.

dotnet --version >nul 2>&1
if %ERRORLEVEL% neq 0 (
    echo [ERROR] .NET SDK not found
    echo Please install .NET 8 SDK: https://dotnet.microsoft.com/download
    echo.
    pause
    exit /b 1
)

echo [OK] .NET found
echo.

if not exist uploads mkdir uploads
if not exist processed_videos mkdir processed_videos

echo [INFO] Building C# server...
dotnet build NoiceServer\NoiceServer.csproj -c Release -v q
if %ERRORLEVEL% neq 0 (
    echo [ERROR] Build failed
    pause
    exit /b 1
)

echo.
echo ===============================================================
echo    Starting server...
echo    Open http://127.0.0.1:8000 in your browser
echo    (Download: ffmpeg must be in PATH for audio muxing)
echo ===============================================================
echo.

dotnet run --project NoiceServer\NoiceServer.csproj -c Release --no-build

echo.
echo Server stopped
pause
