@echo off
setlocal
set "ROOT=%~dp0"
set "PUBLISHED=%ROOT%artifacts\app\DinamikAda.App.exe"
if exist "%PUBLISHED%" (
    start "" "%PUBLISHED%"
    exit /b 0
)
set "EXE=%ROOT%src\DinamikAda.App\bin\x64\Release\net8.0-windows10.0.26100.0\win-x64\DinamikAda.App.exe"
if not exist "%EXE%" (
    echo DinamikAda derleniyor...
    dotnet build "%ROOT%src\DinamikAda.App\DinamikAda.App.csproj" -c Release -p:Platform=x64 -m:1
    if errorlevel 1 (
        echo Derleme basarisiz oldu.
        pause
        exit /b 1
    )
)
start "" "%EXE%"
endlocal
