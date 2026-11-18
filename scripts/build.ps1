param(
    [string]$OutputDirectory = "artifacts\app"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot "src\DinamikAda.App\DinamikAda.App.csproj"
if (-not (Test-Path -LiteralPath $project)) {
    throw "Bu betik kaynak proje klasorunden calistirilmalidir."
}

$output = if ([IO.Path]::IsPathRooted($OutputDirectory)) { [IO.Path]::GetFullPath($OutputDirectory) } else { [IO.Path]::GetFullPath((Join-Path $repoRoot $OutputDirectory)) }
$exePath = Join-Path $output 'DinamikAda.App.exe'
$runningFromOutput = Get-Process -Name "DinamikAda.App" -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -eq $exePath }
if ($runningFromOutput) {
    throw "Bu surum calisiyor. DinamikAda'yi bildirim alanindaki Cikis komutuyla kapatip tekrar deneyin."
}

& dotnet publish $project -c Release -p:Platform=x64 -p:UseSharedCompilation=false -m:1 --self-contained false -o $output
if ($LASTEXITCODE -ne 0) { throw "DinamikAda derlenemedi (ExitCode=$LASTEXITCODE)." }

$nowUtc = [DateTime]::UtcNow
$dllPath = Join-Path $output 'DinamikAda.App.dll'
if (Test-Path -LiteralPath $exePath) {
    [IO.File]::SetLastWriteTimeUtc($exePath, $nowUtc)
}
if (Test-Path -LiteralPath $dllPath) {
    [IO.File]::SetLastWriteTimeUtc($dllPath, $nowUtc)
}
Write-Output "Uygulama hazir: $exePath"
