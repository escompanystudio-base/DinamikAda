param(
    [switch]$RenderAudit,
    [switch]$Smoke
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$tests = Join-Path $repoRoot "tests\DinamikAda.Tests\DinamikAda.Tests.csproj"
& dotnet test $tests -c Release -p:Platform=x64 -p:UseSharedCompilation=false -m:1 --logger "trx;LogFileName=verification.trx"
if ($LASTEXITCODE -ne 0) { throw "Testler basarisiz (ExitCode=$LASTEXITCODE)." }

if ($RenderAudit -or $Smoke) {
    & (Join-Path $PSScriptRoot "build.ps1")
    $exe = Join-Path $repoRoot "artifacts\app\DinamikAda.App.exe"
    if ($RenderAudit) {
        & $exe --render-audit (Join-Path $repoRoot "artifacts\render-audit")
        if ($LASTEXITCODE -ne 0) { throw "Gorsel denetim basarisiz." }
    }
    if ($Smoke) {
        & $exe --smoke-test --duration-sec 12
        if ($LASTEXITCODE -ne 0) { throw "Calisma zamani denetimi basarisiz." }
    }
}
