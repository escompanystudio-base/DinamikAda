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
Write-Output "Uygulama hazir: $(Join-Path $output 'DinamikAda.App.exe')"
