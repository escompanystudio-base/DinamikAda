$ErrorActionPreference = 'Stop'
$hostRoot = Join-Path $env:LOCALAPPDATA 'DinamikAda\ExplorerHost'
$configPath = Join-Path $env:APPDATA 'DinamikAda\config.json'
if (Get-Process -Name DinamikAda.App -ErrorAction SilentlyContinue) {
    throw 'Once DinamikAda uygulamasini bildirim alanindaki Cikis seceneginden kapatin.'
}
# Touch only DinamikAda's portable profile; leave the installed Windhawk service alone.
$exe = Join-Path $hostRoot 'windhawk.exe'
if (Test-Path -LiteralPath $exe) {
    $p = Start-Process -FilePath $exe -ArgumentList '-exit', '-wait' -WindowStyle Hidden -PassThru
    if (-not $p.WaitForExit(10000)) { throw 'Explorer bileseni kapanmadi. Gorev cubugu geri yukleme tamamlanmadi.' }
}
if (Test-Path -LiteralPath $configPath) {
    $config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
    Copy-Item -LiteralPath $configPath -Destination ($configPath + '.before-explorer-restore') -Force
    $config | Add-Member NoteProperty UseExplorerTaskbar $false -Force
    $config.EnableBottomDock = $false
    $config | ConvertTo-Json -Depth 40 | Set-Content -LiteralPath $configPath -Encoding UTF8
}
Write-Output 'Standart Windows gorev cubuguna donuldu. DinamikAda tekrar acildiginda ust ada calismaya devam eder.'
