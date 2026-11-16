<#
.SYNOPSIS
    DinamikAda Sparse MSIX Paketi ve Kimlik Kaydı (Kullanıcı Onaylı / İsteğe Bağlı)
.DESCRIPTION
    Windows 11 Action Center bildirim aynalama (UserNotificationListener) yetkisi için
    DinamikAda.App.exe ikili klasörünü ExternalLocation olarak Appx kimliğine bağlar.
#>
param(
    [string]$BinDir = "",
    [switch]$Uninstall
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot

if ($Uninstall) {
    Get-AppxPackage -Name "MuhammetEmreAkyuz.DinamikAda" -ErrorAction SilentlyContinue | Remove-AppxPackage
    Write-Output "DinamikAda Sparse MSIX kaydi kaldirildi."
    exit 0
}

if ([string]::IsNullOrWhiteSpace($BinDir)) {
    $BinDir = Join-Path $RepoRoot "src\DinamikAda.App\bin\x64\Release\net8.0-windows10.0.26100.0\win-x64"
}

if (-not (Test-Path (Join-Path $BinDir "DinamikAda.App.exe"))) {
    throw "Derlenmis DinamikAda.App.exe bulunamadi: $BinDir"
}
$BinDir = (Resolve-Path -LiteralPath $BinDir).Path

& (Join-Path $PSScriptRoot "package-sparse.ps1")

$msixPath = Join-Path $RepoRoot "artifacts\DinamikAda.Sparse.msix"
$certSubject = "CN=DinamikAdaDev"
$cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $certSubject -and $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date) } | Sort-Object NotAfter -Descending | Select-Object -First 1

if (-not $cert) {
    $cert = New-SelfSignedCertificate -Type Custom -Subject $certSubject -KeyUsage DigitalSignature -FriendlyName "DinamikAda Dev Signing" -CertStoreLocation "Cert:\CurrentUser\My" -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}")
}

$signtool = Get-ChildItem "C:\Program Files (x86)\Windows Kits\10\bin\*\x64\signtool.exe" | Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
if (-not $signtool) { throw "Windows SDK signtool.exe bulunamadi." }

& $signtool sign /fd SHA256 /sha1 $cert.Thumbprint /s My $msixPath
if ($LASTEXITCODE -ne 0) { throw "signtool imzalama basarisiz oldu." }

$cerPath = Join-Path $RepoRoot "artifacts\DinamikAdaDev.cer"
Export-Certificate -Cert $cert -FilePath $cerPath -Force | Out-Null
if (-not (Test-Path -LiteralPath ("Cert:\CurrentUser\TrustedPeople\" + $cert.Thumbprint))) {
    Import-Certificate -FilePath $cerPath -CertStoreLocation "Cert:\CurrentUser\TrustedPeople" | Out-Null
}

Write-Host "Kullanici kimlik kaydi uygulaniyor (ExternalLocation: $BinDir)..."
Write-Host "NOT: Test sertifikasinin guvenilir sayilmasi icin yonetici izniyle TrustedPeople deposuna eklenmesi gerekebilir."
Add-AppxPackage -Path $msixPath -ExternalLocation $BinDir
Write-Output "Sparse MSIX basariyla kaydedildi. Tam Windows Bildirim Aynalama sonraki acilista aktif olur."
