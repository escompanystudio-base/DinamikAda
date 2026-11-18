param([switch]$RevealCycle)
$ErrorActionPreference = 'Stop'
$hostRoot = Join-Path $env:LOCALAPPDATA 'DinamikAda\ExplorerHost'
$statusPath = Join-Path $hostRoot 'animation-status.ini'
function Read-NativeStatus {
    $values = @{}
    if (Test-Path -LiteralPath $statusPath) {
        foreach ($line in (Get-Content -LiteralPath $statusPath)) {
            if ($line -match '^([^=]+)=(\d+)$') { $values[$matches[1]] = [long]$matches[2] }
        }
    }
    return $values
}
$readyDeadline = [DateTime]::UtcNow.AddSeconds(12)
do {
    $initial = Read-NativeStatus
    if ($initial.MusicDriftMs -eq 8200 -and $initial.AppPaletteBrushes -gt 0 -and $initial.TrayPaletteBrushes -gt 0 -and
        $initial.Buttons -gt 0 -and $initial.TrayToggles -gt 0) { break }
    Start-Sleep -Milliseconds 250
} while ([DateTime]::UtcNow -lt $readyDeadline)
if ($initial.MusicDriftMs -ne 8200 -or $initial.AppPaletteBrushes -lt 1 -or $initial.TrayPaletteBrushes -lt 1 -or
    $initial.Buttons -lt 1 -or $initial.TrayToggles -lt 1) {
    throw 'Guncel Dock animasyon motoru henuz hazir degil.'
}
$request = Get-Random -Minimum 1 -Maximum ([int]::MaxValue)
"[Animation]`nRequest=$request" | Set-Content -LiteralPath (Join-Path $hostRoot 'animation-audit.ini') -Encoding Unicode
$deadline = [DateTime]::UtcNow.AddSeconds(12)
do {
    Start-Sleep -Milliseconds 250
    $current = Read-NativeStatus
    if ($current.AuditRuns -gt $initial.AuditRuns) { break }
} while ([DateTime]::UtcNow -lt $deadline)
if ($current.AuditRuns -le $initial.AuditRuns) { throw 'Explorer animasyon motoru dogrulama istegine yanit vermedi.' }
$explorerProcess = Get-Process -Id $current.ProcessId -ErrorAction Stop
if ($explorerProcess.ProcessName -ne 'explorer') { throw 'Animasyonlar Explorer icinde calismiyor.' }
if ($current.Buttons -lt 1 -or $current.Auroras -lt 1) { throw 'Yerel gorev cubugunun simgeleri/arka plani bulunamadi.' }
if ($current.Failures -ne 0) { throw "Yerel animasyon hatalari: $($current.Failures)" }
if ($current.RevealErrors -ne 0) { throw "Dock acilma hatalari: $($current.RevealErrors) (HRESULT $($current.RevealErrorCode))" }
if ($current.TrayToggleErrors -ne 0) { throw "Sistem tepsisi hatalari: $($current.TrayToggleErrors)" }
if ($current.AnimationsEnabled -eq 1) {
    if ($current.AuditScaleControllers -lt 1) { throw 'Gercek simgelerde Scale animasyonu baslatilamadi.' }
    if ($current.AuditAuraControllers -lt 2 -or $current.AuditPaletteStops -lt 10) {
        throw 'Muzik paletinin renk gecisleri ve degrade katmanlari baslatilamadi.'
    }
    if ($current.AuditWaveNeighbors -lt 2) { throw 'Komşu simgelerde Dock dalgasi olusmadi.' }
    if ($current.AuditLiftControllers -lt 2) { throw 'Simge ve komsusunun yukari hareketi baslatilamadi.' }
    if ($current.MaxScalePercent -ne 106) { throw 'Simge buyumesi yuzde 6 ile sinirli olmali.' }
}
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class DinamikAdaDockProbe {
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindow(string c, string t);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out Rect r);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern bool GetMonitorInfo(IntPtr h, ref MonitorInfo info);
    [DllImport("user32.dll")] public static extern int GetWindowRgn(IntPtr h, IntPtr region);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateRectRgn(int l, int t, int r, int b);
    [DllImport("gdi32.dll")] public static extern bool PtInRegion(IntPtr region, int x, int y);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr value);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(Point p);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint flags);
}
'@
$tray = [DinamikAdaDockProbe]::FindWindow('Shell_TrayWnd', $null)
$rect = New-Object DinamikAdaDockProbe+Rect
if (-not [DinamikAdaDockProbe]::GetWindowRect($tray, [ref]$rect)) { throw 'Gorev cubugu boyutu okunamadi.' }
$dpi = [Math]::Max(96, [DinamikAdaDockProbe]::GetDpiForWindow($tray))
$heightDip = ($rect.Bottom - $rect.Top) * 96.0 / $dpi
if ($heightDip -gt 56) { throw "Dock hala buyuk: $heightDip DIP" }
$current.NativeHeightDip = $heightDip
$monitorInfo = New-Object DinamikAdaDockProbe+MonitorInfo
$monitorInfo.Size = [Runtime.InteropServices.Marshal]::SizeOf($monitorInfo)
if (-not [DinamikAdaDockProbe]::GetMonitorInfo([DinamikAdaDockProbe]::MonitorFromWindow($tray, 2), [ref]$monitorInfo)) { throw 'Calisma alani okunamadi.' }
$current.ReservedBottomPixels = $monitorInfo.Monitor.Bottom - $monitorInfo.Work.Bottom
if ($current.ReservedBottomPixels -ne 0) { throw "Gorev cubugu hala $($current.ReservedBottomPixels) piksel yer ayiriyor." }
$region = [DinamikAdaDockProbe]::CreateRectRgn(0, 0, 0, 0)
try {
    if ([DinamikAdaDockProbe]::GetWindowRgn($tray, $region) -eq 0) { throw 'Bos alanlari serbest birakan pencere bolgesi uygulanmadi.' }
    $included = 0
    for ($x=0; $x -lt ($rect.Right - $rect.Left); $x++) {
        if ([DinamikAdaDockProbe]::PtInRegion($region, $x, [int](($rect.Bottom - $rect.Top)/2))) { $included++ }
    }
    $current.InteractiveWidthPixels = $included
    if ($included -le 0 -or $included -gt ($rect.Right - $rect.Left) * .9) { throw 'Bos gorev cubugu seridi hala etkilesimi engelliyor.' }
} finally { [void][DinamikAdaDockProbe]::DeleteObject($region) }
if ($RevealCycle) {
    $auditPath = Join-Path $hostRoot 'reveal-audit.ini'
    $originalAudit = if (Test-Path -LiteralPath $auditPath) { [IO.File]::ReadAllText($auditPath) } else { "[Reveal]`nState=0" }
    try {
        foreach ($target in @(1, 2)) {
            "[Reveal]`nState=$target" | Set-Content -LiteralPath $auditPath -Encoding Unicode
            $until = [DateTime]::UtcNow.AddSeconds(4)
            do {
                Start-Sleep -Milliseconds 100
                $reveal = Read-NativeStatus
                if ($reveal.RevealShown -eq $(if ($target -eq 1) { 1 } else { 0 })) { break }
            } while ([DateTime]::UtcNow -lt $until)
            if ($reveal.RevealShown -ne $(if ($target -eq 1) { 1 } else { 0 }) -or $reveal.RevealErrors -ne 0) {
                throw "Dock acilis/kapanisinda hata olustu (HRESULT $($reveal.RevealErrorCode))."
            }
            Start-Sleep -Milliseconds ([Math]::Max($reveal.RevealDurationMs, $reveal.ConcealDurationMs) + 80)
            $region = [DinamikAdaDockProbe]::CreateRectRgn(0, 0, 0, 0)
            try {
                [void][DinamikAdaDockProbe]::GetWindowRgn($tray, $region)
                $appsPoint = New-Object DinamikAdaDockProbe+Point
                $appsPoint.X = [int](($rect.Left + $rect.Right)/2)
                $appsPoint.Y = [int](($rect.Top + $rect.Bottom)/2)
                $appsIncluded = [DinamikAdaDockProbe]::PtInRegion($region, $appsPoint.X-$rect.Left, $appsPoint.Y-$rect.Top)
                if ($target -eq 1 -and -not $appsIncluded) { throw 'Acilan uygulama kapsulu tiklama bolgesinde degil.' }
                if ($target -eq 2 -and $appsIncluded) { throw 'Gizli uygulama kapsulu alttaki pencereyi engelliyor.' }
                if ($target -eq 1 -and [DinamikAdaDockProbe]::GetAncestor([DinamikAdaDockProbe]::WindowFromPoint($appsPoint),2) -ne $tray) {
                    throw 'Dock buyutulmus pencerenin ustune gelemiyor.'
                }
                $trayX = $rect.Right - $rect.Left - [int](26 * $dpi / 96.0)
                if (-not [DinamikAdaDockProbe]::PtInRegion($region, $trayX, [int](($rect.Bottom-$rect.Top)/2))) { throw 'Saat/sistem tepsisi sabit kalmadi.' }
            } finally { [void][DinamikAdaDockProbe]::DeleteObject($region) }
        }
        $current.RevealCyclePassed = 1
    } finally { [IO.File]::WriteAllText($auditPath, $originalAudit, [Text.Encoding]::Unicode) }
}
$current | ConvertTo-Json -Compress
Write-Output 'Kompakt Dock, sifir alt yer rezervasyonu ve bos alan gecisi basarili.'
