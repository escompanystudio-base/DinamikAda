@echo off
setlocal EnableExtensions

set "ROOT=%~dp0"
set "APP_DIR=%ROOT%artifacts\app"
set "PUBLISHED=%APP_DIR%\DinamikAda.App.exe"
set "BUILD_SCRIPT=%ROOT%scripts\build.ps1"
set "PROJECT=%ROOT%src\DinamikAda.App\DinamikAda.App.csproj"

set "FORCE_REBUILD=0"
set "APP_ARGS="

:parse_args
if "%~1"=="" goto args_done
if /I "%~1"=="--rebuild" (
    set "FORCE_REBUILD=1"
) else if /I "%~1"=="/rebuild" (
    set "FORCE_REBUILD=1"
) else if /I "%~1"=="-Rebuild" (
    set "FORCE_REBUILD=1"
) else (
    if defined APP_ARGS (
        set "APP_ARGS=%APP_ARGS% "%~1""
    ) else (
        set "APP_ARGS="%~1""
    )
)
shift
goto parse_args
:args_done

set "DA_ROOT=%ROOT%"
set "DA_PUBLISHED=%PUBLISHED%"
set "DA_FORCE_REBUILD=%FORCE_REBUILD%"

powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$ErrorActionPreference = 'Stop';" ^
  "$procs = Get-Process -Name 'DinamikAda.App' -ErrorAction SilentlyContinue;" ^
  "if ($procs) {" ^
  "  foreach ($p in $procs) { try { [void]$p.CloseMainWindow() } catch {} }" ^
  "  $deadline = [DateTime]::UtcNow.AddMilliseconds(450);" ^
  "  while ([DateTime]::UtcNow -lt $deadline) { if (-not (Get-Process -Name 'DinamikAda.App' -ErrorAction SilentlyContinue)) { break }; Start-Sleep -Milliseconds 50 }" ^
  "  Get-Process -Name 'DinamikAda.App' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue;" ^
  "  $deadline2 = [DateTime]::UtcNow.AddSeconds(3);" ^
  "  while ([DateTime]::UtcNow -lt $deadline2) { if (-not (Get-Process -Name 'DinamikAda.App' -ErrorAction SilentlyContinue)) { break }; Start-Sleep -Milliseconds 50 }" ^
  "}" ^
  "$root = $env:DA_ROOT;" ^
  "$exe = $env:DA_PUBLISHED;" ^
  "$dll = [IO.Path]::ChangeExtension($exe, '.dll');" ^
  "if ($env:DA_FORCE_REBUILD -eq '1' -or -not (Test-Path -LiteralPath $exe) -or -not (Test-Path -LiteralPath $dll)) { exit 2 }" ^
  "$srcDir = Join-Path $root 'src';" ^
  "if (-not (Test-Path -LiteralPath $srcDir)) { exit 0 }" ^
  "$exeTime = (Get-Item -LiteralPath $exe).LastWriteTimeUtc;" ^
  "$dllTime = (Get-Item -LiteralPath $dll).LastWriteTimeUtc;" ^
  "$refTime = if ($exeTime -gt $dllTime) { $exeTime } else { $dllTime };" ^
  "$watchRoots = @($srcDir, (Join-Path $root 'packaging'), (Join-Path $root 'scripts'));" ^
  "foreach ($w in $watchRoots) {" ^
  "  if (Test-Path -LiteralPath $w) {" ^
  "    foreach ($f in [IO.Directory]::EnumerateFiles($w, '*', [IO.SearchOption]::AllDirectories)) {" ^
  "      if ($f -match '\\(bin|obj)\\') { continue }" ^
  "      if ([IO.File]::GetLastWriteTimeUtc($f) -gt $refTime) { exit 2 }" ^
  "    }" ^
  "  }" ^
  "}" ^
  "foreach ($single in @('DinamikAda.sln', 'Directory.Build.props')) {" ^
  "  $sp = Join-Path $root $single;" ^
  "  if ((Test-Path -LiteralPath $sp) -and ([IO.File]::GetLastWriteTimeUtc($sp) -gt $refTime)) { exit 2 }" ^
  "}" ^
  "exit 0"

set "PREP_EXIT=%ERRORLEVEL%"

if "%PREP_EXIT%"=="2" (
    if not exist "%BUILD_SCRIPT%" (
        echo DinamikAda derleme betigi bulunamadi: "%BUILD_SCRIPT%"
        pause
        exit /b 1
    )
    if not exist "%PROJECT%" (
        echo DinamikAda proje dosyasi bulunamadi: "%PROJECT%"
        pause
        exit /b 1
    )
    echo DinamikAda guncelleniyor ve derleniyor...
    powershell -NoProfile -ExecutionPolicy Bypass -File "%BUILD_SCRIPT%"
    if errorlevel 1 (
        echo Derleme basarisiz oldu.
        pause
        exit /b 1
    )
) else if not "%PREP_EXIT%"=="0" (
    echo Baslatma hazirligi basarisiz oldu. Kod: %PREP_EXIT%
    pause
    exit /b 1
)

if not exist "%PUBLISHED%" (
    echo DinamikAda uygulamasi bulunamadi: "%PUBLISHED%"
    pause
    exit /b 1
)

if defined APP_ARGS (
    start "" /D "%APP_DIR%" "%PUBLISHED%" %APP_ARGS%
) else (
    start "" /D "%APP_DIR%" "%PUBLISHED%"
)
if errorlevel 1 (
    echo DinamikAda baslatilamadi.
    pause
    exit /b 1
)

endlocal
exit /b 0
