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
    $verifyApp = Join-Path $repoRoot "artifacts_verify\app"
    & (Join-Path $PSScriptRoot "build.ps1") -OutputDirectory $verifyApp
    $exe = Join-Path $verifyApp "DinamikAda.App.exe"
    if ($RenderAudit) {
        $auditDirectory = Join-Path $repoRoot "artifacts\render-audit"
        $auditArguments = '--render-audit "{0}"' -f $auditDirectory
        $auditProcess = Start-Process -FilePath $exe -ArgumentList $auditArguments -WindowStyle Hidden -PassThru -Wait
        if ($auditProcess.ExitCode -ne 0) { throw "Gorsel denetim basarisiz (ExitCode=$($auditProcess.ExitCode))." }
        Write-Output "Gorsel denetim tamamlandi: $auditDirectory"
    }
    if ($Smoke) {
        $smokeProcess = Start-Process -FilePath $exe -ArgumentList '--smoke-test --duration-sec 6' -WindowStyle Hidden -PassThru -Wait
        if ($smokeProcess.ExitCode -ne 0) { throw "Calisma zamani denetimi basarisiz (ExitCode=$($smokeProcess.ExitCode))." }
        Write-Output "Calisma zamani denetimi tamamlandi."
    }
}
