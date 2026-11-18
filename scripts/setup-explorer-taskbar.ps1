param(
    [string]$WindhawkDirectory = "$env:ProgramFiles\Windhawk",
    [switch]$CompileOnly
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$hostRoot = Join-Path $env:LOCALAPPDATA 'DinamikAda\ExplorerHost'
$existingHost = Get-Process -Name windhawk -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq (Join-Path $hostRoot 'windhawk.exe') }
if ($existingHost -and -not $CompileOnly) { throw 'Explorer bileseni calisiyor. Once DinamikAda uygulamasini kapatip scripts\restore-explorer-taskbar.ps1 dosyasini calistirin.' }
if (-not (Test-Path -LiteralPath (Join-Path $WindhawkDirectory 'windhawk.exe'))) {
    throw 'Windhawk bulunamadi. https://windhawk.net adresinden kurun veya -WindhawkDirectory ile konumunu belirtin.'
}
# Read the installed engine version rather than assuming the newest engine ABI.
$installedIni = Get-Content -LiteralPath (Join-Path $WindhawkDirectory 'windhawk.ini') -Raw
$engineRelative = [regex]::Match($installedIni, '(?m)^EnginePath=(.+)\r?$').Groups[1].Value.Trim()
if (-not $engineRelative) { throw 'Windhawk EnginePath bulunamadi.' }
$engineSource = Join-Path $WindhawkDirectory $engineRelative
$compilerRoot = Join-Path $WindhawkDirectory 'Compiler'
$compiler = Join-Path $compilerRoot 'bin\clang++.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'Windhawk Clang derleyicisi bulunamadi.' }

$modsRoot = Join-Path $hostRoot 'AppData\Engine\Mods'
$binRoot = Join-Path $modsRoot '64'
$sourceRoot = Join-Path $hostRoot 'AppData\ModsSource'
foreach ($folder in @($hostRoot, $binRoot, $sourceRoot, (Join-Path $hostRoot 'Engine'))) {
    New-Item -ItemType Directory -Path $folder -Force | Out-Null
}
if (-not $CompileOnly) {
foreach ($file in @('windhawk.exe', 'windhawk-x64-helper.exe')) {
    Copy-Item -LiteralPath (Join-Path $WindhawkDirectory $file) -Destination $hostRoot -Force
}
Copy-Item -LiteralPath (Join-Path $engineSource '64') -Destination (Join-Path $hostRoot 'Engine') -Recurse -Force
# The x86 loader is also used by Windhawk's process enumeration/injection helper.
Copy-Item -LiteralPath (Join-Path $engineSource '32') -Destination (Join-Path $hostRoot 'Engine') -Recurse -Force
@"
[Storage]
Portable=1
CompilerPath=$compilerRoot
EnginePath=Engine
UIPath=$(Join-Path $WindhawkDirectory 'UI')
AppDataPath=AppData
"@ | Set-Content -LiteralPath (Join-Path $hostRoot 'windhawk.ini') -Encoding Unicode
@'
[Storage]
Portable=1
AppDataPath=..\AppData\Engine
'@ | Set-Content -LiteralPath (Join-Path $hostRoot 'Engine\engine.ini') -Encoding Unicode
@'
[Settings]
DisableUpdateCheck=1
[Engine]
Include=explorer.exe|StartMenuExperienceHost.exe|ShellExperienceHost.exe|ShellHost.exe
'@ | Set-Content -LiteralPath (Join-Path $hostRoot 'AppData\settings.ini') -Encoding Unicode
}

# Unmodified upstream GPL-3.0 source, pinned and verified before compilation.
$commit = '6f3d126440ecd46f08e48bec2cd9c1e91d3e2919'
$sourceUrl = "https://raw.githubusercontent.com/ramensoftware/windhawk-mods/$commit/mods/windows-11-taskbar-styler.wh.cpp"
$expectedHash = '91FF9039DFB79DD0C838DDAB448329C7C5A8FEA48919F3E6363387D3E2380E23'
$sourcePath = Join-Path $sourceRoot 'windows-11-taskbar-styler.wh.cpp'
if (-not (Test-Path -LiteralPath $sourcePath) -or (Get-FileHash -LiteralPath $sourcePath).Hash -ne $expectedHash) {
    Write-Output 'Dogrulanmis gorev cubugu motoru indiriliyor...'
    Invoke-WebRequest -Uri $sourceUrl -UseBasicParsing -OutFile $sourcePath
}
if ((Get-FileHash -LiteralPath $sourcePath).Hash -ne $expectedHash) { throw 'Kaynak SHA256 dogrulamasi basarisiz.' }
$extensionPath = Join-Path $repoRoot 'native\ExplorerAnimations.hpp'
if (-not (Test-Path -LiteralPath $extensionPath)) { throw 'ExplorerAnimations.hpp bulunamadi.' }
Copy-Item -Path (Join-Path $repoRoot 'native\*.hpp') -Destination $sourceRoot -Force
$generatedPath = Join-Path $sourceRoot 'windows-11-taskbar-styler-dinamikada.generated.wh.cpp'
$generated = [IO.File]::ReadAllText($sourcePath)
$palettePatches = @(
    @{ Before = 'class XamlBlurBrush : public Media::XamlCompositionBrushBaseT<'
       After = '#include "DockPaletteBrush.hpp"' + [char]10 + 'class XamlBlurBrush : public Media::XamlCompositionBrushBaseT<' },
    @{ Before = '    wuc::Compositor m_compositor;'
       After = @'
    std::shared_ptr<DinamikAdaDockPalette::Surface> m_musicGlass;
    DinamikAdaDockPalette::Capsule m_musicCapsule = DinamikAdaDockPalette::Capsule::None;
    wuc::Compositor m_compositor;
'@ },
    @{ Before = 'm_fallbackThemeResourceKey(std::move(fallbackThemeResourceKey))' + [char]10 + '{'
       After = 'm_fallbackThemeResourceKey(std::move(fallbackThemeResourceKey))' + [char]10 + '{' + [char]10 +
               '    m_musicCapsule = DinamikAdaDockPalette::Identify(element);' },
    @{ Before = '    return m_compositor.CreateColorBrush(m_fallbackColor.value_or(m_tint));'
       After = @'
    if (m_musicCapsule != DinamikAdaDockPalette::Capsule::None) {
        m_musicGlass = DinamikAdaDockPalette::Create(m_compositor, m_musicCapsule, {255, 24, 25, 33}, true);
        return m_musicGlass->brush;
    }
    return m_compositor.CreateColorBrush(m_fallbackColor.value_or(m_tint));
'@ },
    @{ Before = '    auto factory = m_compositor.CreateEffectFactory(*compositeEffect);'
       After = @'
    if (m_musicCapsule != DinamikAdaDockPalette::Capsule::None)
        compositeEffect->Sources.push_back(wuc::CompositionEffectSourceParameter(L"musicGlass"));
    auto factory = m_compositor.CreateEffectFactory(*compositeEffect);
'@ },
    @{ Before = '    brush.SetSourceParameter(L"backdrop", backdropBrush);'
       After = @'
    brush.SetSourceParameter(L"backdrop", backdropBrush);
    if (m_musicCapsule != DinamikAdaDockPalette::Capsule::None) {
        m_musicGlass = DinamikAdaDockPalette::Create(m_compositor, m_musicCapsule, {}, false);
        brush.SetSourceParameter(L"musicGlass", m_musicGlass->brush);
    }
'@ },
    @{ Before = 'void XamlBlurBrush::OnDisconnected()' + [char]10 + '{'
       After = 'void XamlBlurBrush::OnDisconnected()' + [char]10 + '{' + [char]10 + '    m_musicGlass.reset();' }
)
$patches = @(
    @{ Before = 'void ApplyCustomizations(ElementId elementId,' + "`n" + '                         FrameworkElement element,' + "`n" + '                         PCWSTR fallbackClassName) {'
       After = '#include "ExplorerAnimations.hpp"' + "`n" + 'void ApplyCustomizations(ElementId elementId,' + "`n" + '                         FrameworkElement element,' + "`n" + '                         PCWSTR fallbackClassName) {' + "`n" + '    DinamikAdaAnimations::Attach(static_cast<uint64_t>(elementId), element);' },
    @{ Before = 'void CleanupCustomizations(ElementId elementId) {'; After = 'void CleanupCustomizations(ElementId elementId) {' + "`n" + '    DinamikAdaAnimations::Detach(static_cast<uint64_t>(elementId));' },
    @{ Before = 'void UninitializeForCurrentThread() {'; After = 'void UninitializeForCurrentThread() {' + "`n" + '    DinamikAdaAnimations::Clear();' },
    @{ Before = 'auto taskbarFrameRect = frameRect(taskbarFrame);'; After = 'auto taskbarFrameRect = frameRect(FindTaskbarFrameBackground(taskbarFrame));' + "`n" + '    if (!taskbarFrameRect) taskbarFrameRect = frameRect(taskbarFrame);' },
    @{ Before = 'void UpdateClickThroughRegion(ClickThroughTaskbarState& state) {'
       After = @'
namespace DinamikAdaTray {
inline std::optional<winrt::Windows::Foundation::Rect> ClipBounds(FrameworkElement const&, UIElement const&);
}

void UpdateClickThroughRegion(ClickThroughTaskbarState& state) {
'@ },
    @{ Before = '    auto systemTrayFrameRect = frameRect(systemTrayFrame);'
       After = @'
    auto systemTrayFrameRect = frameRect(systemTrayFrame);
    if (systemTrayFrame) {
        if (auto compact = DinamikAdaTray::ClipBounds(systemTrayFrame, content))
            systemTrayFrameRect = compact;
    }
'@ }
)
foreach ($patch in ($patches + $palettePatches)) {
    if ([regex]::Matches($generated, [regex]::Escape($patch.Before)).Count -ne 1) { throw 'Kaynak animasyon baglanti noktasi dogrulanamadi.' }
    $generated = $generated.Replace($patch.Before, $patch.After)
}
[IO.File]::WriteAllText($generatedPath, $generated, [Text.UTF8Encoding]::new($false))
$dllName = 'windows-11-taskbar-styler_1.11.2_dinamikada15.dll'
$dllPath = Join-Path $binRoot $dllName
$flags = @(
    '-std=c++23', '-O2', '-shared', '-DUNICODE', '-D_UNICODE',
    '-DWINVER=0x0A00', '-D_WIN32_WINNT=0x0A00', '-D_WIN32_IE=0x0A00',
    '-DNTDDI_VERSION=0x0A000008', '-D__USE_MINGW_ANSI_STDIO=0', '-DWH_MOD',
    '-DWH_MOD_ID=L\"windows-11-taskbar-styler\"', '-DWH_MOD_VERSION=L\"1.11.2\"',
    ('"' + (Join-Path $engineSource '64\windhawk.lib') + '"'),
    ('"' + $generatedPath + '"'), '-include', 'windhawk_api.h', '-target', 'x86_64-w64-mingw32',
    '-Wl,--export-all-symbols', '-o', ('"' + $dllPath + '"'),
    '-lcomctl32', '-lgdi32', '-lole32', '-loleaut32', '-lruntimeobject', '-lshlwapi'
)
$responsePath = Join-Path $hostRoot 'compile.rsp'
($flags | ForEach-Object { if ($_ -match '^"') { $_.Replace('\', '/') } else { $_ } }) | Set-Content -LiteralPath $responsePath -Encoding UTF8
Write-Output 'Explorer icin yerel x64 bilesen derleniyor...'
$previousManifestPath = Join-Path $hostRoot 'ready.json'
$previousManifest = if (Test-Path -LiteralPath $previousManifestPath) { Get-Content -LiteralPath $previousManifestPath -Raw | ConvertFrom-Json } else { $null }
$glassHash = (Get-FileHash -LiteralPath (Join-Path $repoRoot 'native\MusicGlass.hpp')).Hash
$dockPaletteHash = (Get-FileHash -LiteralPath (Join-Path $repoRoot 'native\DockPaletteBrush.hpp')).Hash
$trayHash = (Get-FileHash -LiteralPath (Join-Path $repoRoot 'native\ExplorerTray.hpp')).Hash
$reuseStyler = (Test-Path -LiteralPath $dllPath) -and $previousManifest.sha256 -eq $expectedHash -and
    $previousManifest.animationSha256 -eq (Get-FileHash -LiteralPath $extensionPath).Hash -and
    $previousManifest.motionSha256 -eq (Get-FileHash -LiteralPath (Join-Path $repoRoot 'native\DockMotion.hpp')).Hash -and
    $previousManifest.revealSha256 -eq (Get-FileHash -LiteralPath (Join-Path $repoRoot 'native\ExplorerDockReveal.hpp')).Hash -and
    $previousManifest.glassSha256 -eq $glassHash -and $previousManifest.dockPaletteSha256 -eq $dockPaletteHash -and
    $previousManifest.traySha256 -eq $trayHash
Push-Location $compilerRoot
try {
    if (-not $reuseStyler) {
        & $compiler ("@" + $responsePath)
        if ($LASTEXITCODE -ne 0) { throw "Yerel gorev cubugu derlenemedi ($LASTEXITCODE)." }
    }
} finally { Pop-Location }
if ($reuseStyler) { Write-Output 'Dogrulanmis stil ve animasyon bileseni yeniden kullaniliyor.' }

function Build-ShellStyler {
    param(
        [string]$ModId, [string]$Url, [string]$Hash, [string]$Library,
        [string]$PreviousHash, [hashtable[]]$Patches
    )
    $source = Join-Path $sourceRoot "$ModId.wh.cpp"
    if (-not (Test-Path -LiteralPath $source) -or (Get-FileHash -LiteralPath $source).Hash -ne $Hash) {
        Invoke-WebRequest -Uri $Url -UseBasicParsing -OutFile $source
    }
    if ((Get-FileHash -LiteralPath $source).Hash -ne $Hash) {
        throw "$ModId kaynagi SHA256 dogrulamasi basarisiz."
    }
    $output = Join-Path $binRoot $Library
    if ((Test-Path -LiteralPath $output) -and $PreviousHash -eq $Hash -and
        $previousManifest.startExtensionSha256 -eq $startExtensionHash -and $previousManifest.glassSha256 -eq $glassHash -and
        $previousManifest.dockPaletteSha256 -eq $dockPaletteHash) {
        return
    }
    $generatedSource = Join-Path $sourceRoot "$ModId-dinamikada.generated.wh.cpp"
    $code = [IO.File]::ReadAllText($source)
    foreach ($patch in $Patches) {
        if ([regex]::Matches($code, [regex]::Escape($patch.Before)).Count -ne 1) {
            throw "$ModId baglanti noktasi dogrulanamadi."
        }
        $code = $code.Replace($patch.Before, $patch.After)
    }
    [IO.File]::WriteAllText($generatedSource, $code, [Text.UTF8Encoding]::new($false))
    $shellFlags = foreach ($flag in $flags) {
        if ($flag.StartsWith('-DWH_MOD_ID=')) { '-DWH_MOD_ID=L\"' + $ModId + '\"' }
        elseif ($flag.StartsWith('-DWH_MOD_VERSION=')) { '-DWH_MOD_VERSION=L\"1.8\"' }
        elseif ($flag -eq ('"' + $generatedPath + '"')) { '"' + $generatedSource + '"' }
        elseif ($flag -eq ('"' + $dllPath + '"')) { '"' + $output + '"' }
        else { $flag }
    }
    $shellFlags += '-lversion'
    $shellResponse = Join-Path $hostRoot "compile-$ModId.rsp"
    ($shellFlags | ForEach-Object { if ($_ -match '^"') { $_.Replace('\', '/') } else { $_ } }) |
        Set-Content -LiteralPath $shellResponse -Encoding UTF8
    Push-Location $compilerRoot
    try {
        & $compiler ("@" + $shellResponse)
        if ($LASTEXITCODE -ne 0) { throw "$ModId bileseni derlenemedi ($LASTEXITCODE)." }
    } finally { Pop-Location }
}

$startUrl = "https://raw.githubusercontent.com/ramensoftware/windhawk-mods/$commit/mods/windows-11-start-menu-styler.wh.cpp"
$startHash = '23CC778D1A99802F0D1A1838176704A2EF9FD4DFBE9821E74AB25F8FA86EB0D5'
$notificationUrl = "https://raw.githubusercontent.com/ramensoftware/windhawk-mods/$commit/mods/windows-11-notification-center-styler.wh.cpp"
$notificationHash = 'BDE7D763E1D2E2F0BE98C6CA6D85D65796975D6005405C5BDB388F565075D1AC'
$startExtensionHash = (Get-FileHash -LiteralPath (Join-Path $repoRoot 'native\StartMusicGlass.hpp')).Hash
$hostingInclude = '#include <winrt/Windows.UI.Xaml.Hosting.h>'
$glassInclude = '#include "StartMusicGlass.hpp"'
$cleanupHook = 'void CleanupCustomizations(ElementId elementId) {'
$detach = $cleanupHook + [char]10 + '    DinamikAdaStartGlass::Detach(static_cast<uint64_t>(elementId));'
$startApply = '    // The app''s resources can be reset during initialization, even after the'
$notificationApply = '    // Merge resource dictionary on first element add. Merging it earlier on'
$attach = '    DinamikAdaStartGlass::Attach(static_cast<uint64_t>(elementId), element);'
$startUnload = 'void UninitializeSettingsAndTap() {'
$notificationUnload = 'void UninitializeForCurrentThread() {'
$clear = '    DinamikAdaStartGlass::Clear();'
Build-ShellStyler 'windows-11-start-menu-styler' $startUrl $startHash 'windows-11-start-menu-styler_1.8_dinamikada4.dll' $previousManifest.startSha256 @(
    @{ Before = $hostingInclude; After = $hostingInclude + [char]10 + $glassInclude }
    @{ Before = $startApply; After = $attach + [char]10 + $startApply }
    @{ Before = $cleanupHook; After = $detach }
    @{ Before = $startUnload; After = $startUnload + [char]10 + $clear }
)
Build-ShellStyler 'windows-11-notification-center-styler' $notificationUrl $notificationHash 'windows-11-notification-center-styler_1.8_dinamikada3.dll' $previousManifest.notificationSha256 (@(
    @{ Before = $hostingInclude; After = $hostingInclude + [char]10 + '#define DINAMIKADA_NOTIFICATION_GLASS' + [char]10 + $glassInclude }
    @{ Before = $notificationApply; After = $attach + [char]10 + $notificationApply }
    @{ Before = $cleanupHook; After = $detach }
    @{ Before = $notificationUnload; After = $notificationUnload + [char]10 + $clear }
) + $palettePatches)

$sizeCommit = '3188655e208afff19e3cd85523aaeb949df54315'
$sizeUrl = "https://raw.githubusercontent.com/ramensoftware/windhawk-mods/$sizeCommit/mods/taskbar-icon-size.wh.cpp"
$sizeHash = '0EF2201C39EC4E396503509B218768ABF25D98F09CAFA013D32E865A934BDB1F'
$sizeSource = Join-Path $sourceRoot 'taskbar-icon-size.wh.cpp'
if (-not (Test-Path -LiteralPath $sizeSource) -or (Get-FileHash -LiteralPath $sizeSource).Hash -ne $sizeHash) {
    Invoke-WebRequest -Uri $sizeUrl -UseBasicParsing -OutFile $sizeSource
}
if ((Get-FileHash -LiteralPath $sizeSource).Hash -ne $sizeHash) { throw 'Boyut motoru SHA256 dogrulamasi basarisiz.' }
$sizeDll = Join-Path $binRoot 'taskbar-icon-size_1.3.11_dinamikada.dll'
$sizeFlags = foreach ($flag in $flags) {
    if ($flag.StartsWith('-DWH_MOD_ID=')) { '-DWH_MOD_ID=L\"taskbar-icon-size\"' }
    elseif ($flag.StartsWith('-DWH_MOD_VERSION=')) { '-DWH_MOD_VERSION=L\"1.3.11\"' }
    elseif ($flag -eq ('"' + $generatedPath + '"')) { '"' + $sizeSource + '"' }
    elseif ($flag -eq ('"' + $dllPath + '"')) { '"' + $sizeDll + '"' }
    else { $flag }
}
$sizeFlags += @('-lshcore', '-lversion')
$sizeResponse = Join-Path $hostRoot 'compile-size.rsp'
($sizeFlags | ForEach-Object { if ($_ -match '^"') { $_.Replace('\', '/') } else { $_ } }) | Set-Content -LiteralPath $sizeResponse -Encoding UTF8
Push-Location $compilerRoot
try {
    $previousManifestPath = Join-Path $hostRoot 'ready.json'
    $previousManifest = if (Test-Path -LiteralPath $previousManifestPath) { Get-Content -LiteralPath $previousManifestPath -Raw | ConvertFrom-Json } else { $null }
    if (-not (Test-Path -LiteralPath $sizeDll) -or $previousManifest.sizeSha256 -ne $sizeHash) {
        & $compiler ("@" + $sizeResponse)
        if ($LASTEXITCODE -ne 0) { throw "Yerel Dock boyut motoru derlenemedi ($LASTEXITCODE)." }
    }
} finally { Pop-Location }

$overlayCommit = '2a7304911a79819683903696a3a7a34a1bb59633'
$overlayUrl = "https://raw.githubusercontent.com/ramensoftware/windhawk-mods/$overlayCommit/mods/taskbar-auto-hide-when-maximized.wh.cpp"
$overlayHash = 'ED429BF58070923B53CD52A6F14721FB210783DA70374EE5BDA5651939EDDAF9'
$overlaySource = Join-Path $sourceRoot 'taskbar-auto-hide-when-maximized.wh.cpp'
if (-not (Test-Path -LiteralPath $overlaySource) -or (Get-FileHash -LiteralPath $overlaySource).Hash -ne $overlayHash) {
    Invoke-WebRequest -Uri $overlayUrl -UseBasicParsing -OutFile $overlaySource
}
if ((Get-FileHash -LiteralPath $overlaySource).Hash -ne $overlayHash) { throw 'Yerlesim motoru SHA256 dogrulamasi basarisiz.' }
$overlayDll = Join-Path $binRoot 'taskbar-auto-hide-when-maximized_1.2.6_dinamikada.dll'
$overlayFlags = foreach ($flag in $flags) {
    if ($flag.StartsWith('-DWH_MOD_ID=')) { '-DWH_MOD_ID=L\"taskbar-auto-hide-when-maximized\"' }
    elseif ($flag.StartsWith('-DWH_MOD_VERSION=')) { '-DWH_MOD_VERSION=L\"1.2.6\"' }
    elseif ($flag -eq ('"' + $generatedPath + '"')) { '"' + $overlaySource + '"' }
    elseif ($flag -eq ('"' + $dllPath + '"')) { '"' + $overlayDll + '"' }
    else { $flag }
}
$overlayFlags += @('-ldwmapi', '-lversion')
$overlayResponse = Join-Path $hostRoot 'compile-overlay.rsp'
($overlayFlags | ForEach-Object { if ($_ -match '^"') { $_.Replace('\', '/') } else { $_ } }) | Set-Content -LiteralPath $overlayResponse -Encoding UTF8
Push-Location $compilerRoot
try {
    if (-not (Test-Path -LiteralPath $overlayDll) -or $previousManifest.overlaySha256 -ne $overlayHash) {
        & $compiler ("@" + $overlayResponse)
        if ($LASTEXITCODE -ne 0) { throw "Yerlesim motoru derlenemedi ($LASTEXITCODE)." }
    }
} finally { Pop-Location }
foreach ($pair in @(@('libc++.dll', 'libc++.whl'), @('libunwind.dll', 'libunwind.whl'), @('windhawk-mod-shim.dll', 'windhawk-mod-shim.dll'))) {
    if ($CompileOnly -and (Test-Path -LiteralPath (Join-Path $binRoot $pair[1]))) { continue }
    Copy-Item -LiteralPath (Join-Path $compilerRoot ('x86_64-w64-mingw32\bin\' + $pair[0])) -Destination (Join-Path $binRoot $pair[1]) -Force
}
$hostSettingsPath = Join-Path $hostRoot 'AppData\settings.ini'
if (Test-Path -LiteralPath $hostSettingsPath) {
    $hostSettings = [IO.File]::ReadAllText($hostSettingsPath)
    $upgradedSettings = [regex]::Replace($hostSettings, '(?m)^Include=explorer\.exe(?:\|StartMenuExperienceHost\.exe)?(?=\r?$)', 'Include=explorer.exe|StartMenuExperienceHost.exe|ShellExperienceHost.exe|ShellHost.exe')
    if ($upgradedSettings -ne $hostSettings) {
        [IO.File]::WriteAllText($hostSettingsPath, $upgradedSettings, [Text.Encoding]::Unicode)
    }
}

# The native notification panel runs as an AppContainer. Its package can read
# this portable profile, while the rest of DinamikAda's data keeps its existing ACL.
$notificationPackage = Get-AppxPackage -Name 'Microsoft.Windows.ShellExperienceHost'
if ($notificationPackage) {
    if (-not ('DinamikAdaShellPackage' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Security.Principal;
public static class DinamikAdaShellPackage {
    [DllImport("userenv.dll", CharSet = CharSet.Unicode)]
    private static extern int DeriveAppContainerSidFromAppContainerName(string name, out IntPtr sid);
    [DllImport("advapi32.dll")]
    private static extern IntPtr FreeSid(IntPtr sid);
    public static SecurityIdentifier GetSid(string family) {
        IntPtr sid;
        Marshal.ThrowExceptionForHR(DeriveAppContainerSidFromAppContainerName(family, out sid));
        try { return new SecurityIdentifier(sid); }
        finally { FreeSid(sid); }
    }
}
'@
    }
    $notificationSid = [DinamikAdaShellPackage]::GetSid($notificationPackage.PackageFamilyName)
    $profileAcl = Get-Acl -LiteralPath $hostRoot
    $profileRule = [Security.AccessControl.FileSystemAccessRule]::new(
        $notificationSid, 'ReadAndExecute', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
    $profileAcl.SetAccessRule($profileRule)
    Set-Acl -LiteralPath $hostRoot -AclObject $profileAcl
}
@{
    version = '1.11.2'; source = $sourceUrl; sha256 = $expectedHash; animationVersion = 10
    animationSha256 = (Get-FileHash -LiteralPath $extensionPath).Hash
    motionSha256 = (Get-FileHash -LiteralPath (Join-Path $repoRoot 'native\DockMotion.hpp')).Hash
    revealSha256 = (Get-FileHash -LiteralPath (Join-Path $repoRoot 'native\ExplorerDockReveal.hpp')).Hash
    glassSha256 = $glassHash
    dockPaletteSha256 = $dockPaletteHash
    traySha256 = $trayHash
    startSource = $startUrl; startSha256 = $startHash; startExtensionSha256 = $startExtensionHash
    notificationSource = $notificationUrl; notificationSha256 = $notificationHash
    sizeSource = $sizeUrl; sizeSha256 = $sizeHash
    overlaySource = $overlayUrl; overlaySha256 = $overlayHash
    license = 'GPL-3.0'; licenseUrl = 'https://www.gnu.org/licenses/gpl-3.0.html'
    installedUtc = [DateTime]::UtcNow.ToString('o')
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $hostRoot 'ready.json') -Encoding UTF8
Write-Output "Explorer bileseni hazir: $hostRoot"
Write-Output 'Etkinlestirmek icin DinamikAda-Baslat.bat --explorer-taskbar komutunu kullanin.'
