param(
    [switch]$ValidateOnly,
    [string]$OutputMsix = "artifacts\DinamikAda.Sparse.msix"
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot
$ManifestPath = Join-Path $RepoRoot "packaging\Package.appxmanifest"
$AssetsDir = Join-Path $RepoRoot "packaging\Assets"
$AppManifestPath = Join-Path $RepoRoot "src\DinamikAda.App\app.manifest"
if (-not (Test-Path -LiteralPath $AppManifestPath)) { $AppManifestPath = Join-Path $RepoRoot "app.manifest" }

if (-not (Test-Path $ManifestPath)) {
    throw "Manifest bulunamadi: $ManifestPath"
}

[xml]$xml = Get-Content -Path $ManifestPath -Raw -Encoding UTF8
$pub = $xml.Package.Identity.Publisher
$exe = $xml.Package.Applications.Application.Executable
$trust = $xml.Package.Applications.Application.GetAttribute("TrustLevel", "http://schemas.microsoft.com/appx/manifest/uap/windows10/10")

if ([string]::IsNullOrWhiteSpace($pub) -or [string]::IsNullOrWhiteSpace($exe)) {
    throw "Manifest Identity.Publisher veya Application.Executable gecersiz."
}
[xml]$appXml = Get-Content -LiteralPath $AppManifestPath -Raw -Encoding UTF8
$msixIdentity = $appXml.assembly.msix
if ($msixIdentity.publisher -ne $pub -or $msixIdentity.packageName -ne $xml.Package.Identity.Name -or $msixIdentity.applicationId -ne $xml.Package.Applications.Application.Id) {
    throw "Uygulama manifestindeki MSIX kimligi paket manifestiyle eslesmiyor."
}

$makeAppx = Get-ChildItem "C:\Program Files (x86)\Windows Kits\10\bin\*\x64\makeappx.exe" -ErrorAction SilentlyContinue |
    Sort-Object FullName -Descending |
    Select-Object -First 1 -ExpandProperty FullName

if (-not $makeAppx) {
    throw "Windows SDK makeappx.exe bulunamadi."
}

# Validate required logo files declared in manifest exist
foreach ($assetName in @("StoreLogo.png", "Square44x44Logo.png", "Square150x150Logo.png")) {
    $assetFile = Join-Path $AssetsDir $assetName
    if (-not (Test-Path $assetFile)) {
        throw "Eksik manifest varligi: $assetFile"
    }
}

$stageDir = Join-Path $RepoRoot "obj\sparse-stage"
$stageFull = [IO.Path]::GetFullPath($stageDir)
$expectedStage = [IO.Path]::GetFullPath((Join-Path $RepoRoot "obj\sparse-stage"))
if (-not $stageFull.Equals($expectedStage, [StringComparison]::OrdinalIgnoreCase)) { throw "Gecersiz paketleme gecici dizini." }
if (Test-Path -LiteralPath $stageFull) { Remove-Item -LiteralPath $stageFull -Recurse -Force }
New-Item -ItemType Directory -Path (Join-Path $stageDir "Assets") -Force | Out-Null

Copy-Item $ManifestPath (Join-Path $stageDir "AppxManifest.xml") -Force
Copy-Item (Join-Path $AssetsDir "*") (Join-Path $stageDir "Assets") -Force

$outFull = if ([IO.Path]::IsPathRooted($OutputMsix)) { [IO.Path]::GetFullPath($OutputMsix) } else { [IO.Path]::GetFullPath((Join-Path $RepoRoot $OutputMsix)) }
$outParent = Split-Path -Parent $outFull
New-Item -ItemType Directory -Path $outParent -Force | Out-Null
if (Test-Path $outFull) { Remove-Item $outFull -Force }

$packOut = & $makeAppx pack /d $stageDir /p $outFull /nv /o 2>&1
if ($LASTEXITCODE -ne 0 -or -not (Test-Path $outFull)) {
    throw "makeappx.exe Sparse MSIX olusturamadi (ExitCode=$LASTEXITCODE): $($packOut -join "`n")"
}

Write-Output "SPARSE_MSIX_VALIDATED: MakeAppx=$makeAppx; Output=$outFull; SizeBytes=$((Get-Item $outFull).Length); TrustLevel=$trust"

if (-not $ValidateOnly) {
    $signtool = Get-ChildItem "C:\Program Files (x86)\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending |
        Select-Object -First 1 -ExpandProperty FullName
    Write-Output "SignTool ready at: $signtool"
}
