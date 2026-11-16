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
$buildOutput = (Join-Path $repoRoot "artifacts\build").TrimEnd('\', '/') + "/"
& dotnet publish $project -c Release -p:Platform=x64 -p:UseSharedCompilation=false "-p:OutDir=$buildOutput" -m:1 --self-contained false -o $output
if ($LASTEXITCODE -ne 0) { throw "DinamikAda derlenemedi (ExitCode=$LASTEXITCODE)." }
$exePath = Join-Path $output 'DinamikAda.App.exe'
if (Test-Path -LiteralPath $exePath) {
    # Ensure published exe timestamp advances even when Deterministic=true skips apphost overwrite
    [IO.File]::SetLastWriteTimeUtc($exePath, [DateTime]::UtcNow)
}
Write-Output "Uygulama hazir: $exePath"
