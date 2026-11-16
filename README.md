<div align="center">

# DinamikAda

**Native Windows 11 Dynamic Island Control Deck — built with C# 12, .NET 8, WPF & Direct Win32/COM/WinRT Interop.**

![.NET 8](https://img.shields.io/badge/.NET_8-net8.0--windows10.0.26100.0-0D1117?style=flat-square&logo=dotnet&logoColor=58A6FF)
![C# 12](https://img.shields.io/badge/C%23_12-x64_Native_Win32%2FCOM-0D1117?style=flat-square&logoColor=A855F7)
![License: MIT](https://img.shields.io/badge/License-MIT-0D1117?style=flat-square)
![Tests: 124 Passed](https://img.shields.io/badge/xUnit-124%20Passing-0D1117?style=flat-square&logoColor=30D158)

<br />

<img src="docs/screenshots/now-feed.png" alt="DinamikAda Expanded Feed Hub" width="88%" />

<br /><br />

<img src="docs/screenshots/dock-live-aurora.png" alt="DinamikAda Floating Bottom Dock & Flowing Album Aurora" width="88%" />

</div>

---

## Overview

**DinamikAda** is a zero-WebView, ultra-low-overhead Windows 11 top-notch / floating island workspace utility (`WPF` + `Win32` + `COM` + `WinRT`). It merges live media controls, macOS-style dynamic album ambient aura, per-app CoreAudio mixing, hardware & Night-Light-safe software brightness dimming, real OS telemetry, clipboard encryption vault, LAN QR file drops, region OCR, and a keyboard-driven command palette into a 240Hz-aligned layered island shell.

### Measured Runtime Characteristics (`--smoke-test`)
- **Zero WebView / Electron**: Pure WPF shell (`WebView2=False`, `RenderMode=SoftwareOnly`) with native Win32/COM/WinRT hardware bridges (`PerMonitorV2=True`).
- **Idle CPU**: **`0.000% – 0.051%`** (`WorkingSetMB=8.34`, `PrivateMB=65.34`; composition render loop automatically halts when settled or paused).
- **Motion Engine**: **`225.2 FPS`** measured render burst on 240Hz displays using pure `RenderTransform` / `PathGeometry` (zero layout invalidation during motion).
- **Automated Coverage**: **`124 / 124`** xUnit unit & integration tests + headless smoke checks (`--smoke-test`) + **33-frame** automated render audit (`--render-audit`).

---

## Visual Showcase

<table>
  <tr>
    <td width="50%" align="center">
      <img src="docs/screenshots/compact-media.png" alt="Compact Media Pill" width="100%" />
      <sub><b>S2 Compact Pill</b> — Soft album-derived ambient aura &amp; audio bars</sub>
    </td>
    <td width="50%" align="center">
      <img src="docs/screenshots/command-palette.png" alt="Command Palette" width="100%" />
      <sub><b>S5 Command Hub</b> — Window switcher (@), vault (#), calc (=), shell (&gt;)</sub>
    </td>
  </tr>
  <tr>
    <td width="50%" align="center">
      <img src="docs/screenshots/audio-mixer.png" alt="CoreAudio Mixer &amp; Voice Note" width="100%" />
      <sub><b>Audio Tab</b> — WASAPI per-app mixer, device routing &amp; 48 kHz WAV voice notes</sub>
    </td>
    <td width="50%" align="center">
      <img src="docs/screenshots/control-center.png" alt="System Control Center" width="100%" />
      <sub><b>Control Tab</b> — DDC/CI + software dimmer, caffeine, power plans &amp; scheduled sleep</sub>
    </td>
  </tr>
  <tr>
    <td width="50%" align="center">
      <img src="docs/screenshots/system-telemetry.png" alt="System Telemetry &amp; Process Monitor" width="100%" />
      <sub><b>System Tab</b> — Live CPU, RAM, GPU VRAM, disk I/O, latency &amp; process monitor</sub>
    </td>
    <td width="50%" align="center">
      <img src="docs/screenshots/widgets-ai-quota.png" alt="Widgets &amp; Multi-AI Quota Tracker" width="100%" />
      <sub><b>Widgets &amp; AI Quota</b> — Focus Pulse, weather &amp; multi-account ChatGPT / Claude timers</sub>
    </td>
  </tr>
  <tr>
    <td width="50%" align="center">
      <img src="docs/screenshots/bottom-dock.png" alt="Floating Curved Bottom Dock" width="100%" />
      <sub><b>Floating Curved Bottom Dock</b> — Full-height work area, running dots/active pill, Island Sync &amp; album aurora</sub>
    </td>
    <td width="50%" align="center">
      <img src="docs/screenshots/dock-live-aurora.png" alt="Live Desktop Dock Aurora" width="100%" />
      <sub><b>Live Flowing Dock Aurora</b> — Multi-layer drifting radial ambient glow &amp; border rim shimmer</sub>
    </td>
  </tr>
  <tr>
    <td width="50%" align="center">
      <img src="docs/screenshots/dock-tray-popup.png" alt="Tray Overflow Flyout" width="100%" />
      <sub><b>Tray Overflow Flyout</b> — 5-column background apps menu with light-dismiss &amp; single-instance Chromium/Electron wake</sub>
    </td>
    <td width="50%" align="center">
      <img src="docs/screenshots/dock-calendar-popup.png" alt="Clock &amp; Calendar Pop-up" width="100%" />
      <sub><b>Clock &amp; Calendar Pop-up</b> — Live clock (HH:mm:ss) &amp; interactive monthly calendar</sub>
    </td>
  </tr>
</table>

### Floating Bottom Dock & AI Hub Highlights
- **Floating Curved Dock (`DockWindow`)**: Cleanly hides the Windows taskbar (`ABM_SETSTATE`) so applications expand across the full monitor height (`WorkArea = 1920×1080`) with a curved obsidian capsule, Start Menu launcher, Trash slot, Island sync pill, **Intellihide** auto-hide with bottom-edge peek, and live running dots (`5×5 px`) / foreground active pill (`14×3.5 px`).
- **Synced Flowing Album Aurora**: Multi-layer 3-orb radial glow (`DockAuraOrbPrimaryBorder`, `DockAuraOrbSecondaryBorder`, `DockAuraOrbHighlightBorder`) and rim shimmer (`DockBorderGlowBrush`) mirror the top island's active album palette while dropping to `0%` CPU when paused or hidden.
- **Reliable Tray Overflow &amp; Calendar Pop-ups**: Non-activating (`WS_EX_NOACTIVATE`) background tray (`^`) and clock/calendar pop-ups support click-anywhere light-dismiss (`WH_MOUSE_LL`) and restore minimized/tray-hidden Electron &amp; Chromium apps cleanly without window-maximize loss.
- **Multi-AI Subscription &amp; Quota Tracker**: Monitor multiple ChatGPT, Claude, Gemini, or custom quota countdown timers side-by-side inside the **Araçlar** tab.

---

## Core Capabilities

- **Media & Dynamic Album Aura (`Modules.Media`)**: Windows GSMTC multi-session switcher, synced `.lrc` lyrics strip, CoreAudio mix-format probing (`SampleRate` / `BitDepth` switcher), per-app volume mixer & endpoint router, loopback spectrum visualizer, and instant WAV voice notes.
- **System & Display Control (`Modules.SystemControl` / `Platform.Win32`)**: DDC/CI hardware brightness + Night-Light-safe pure Win32 `WS_POPUP` (`UpdateLayeredWindow`) dimmer clamped to `0–75%`, Caffeine wake lock, scheduled sleep/shutdown timer, USB drive arrival/eject HUD, and monitor/virtual desktop window controls.
- **Real OS Telemetry & Maintenance (`Modules.Telemetry`)**: PDH / DXGI / AMD ADL real-time CPU, GPU engine/VRAM/temp, RAM, disk I/O, network rates, `1.1.1.1` ICMP latency, hung process watchdog, safe `%TEMP%` cleanup, memory trim, and idle background app scanner.
- **Productivity & Capture (`Modules.Productivity`)**: File shelf with drag-out & non-destructive EXIF strip/compress, DPAPI-encrypted clipboard vault, region screenshot + `Windows.Media.Ocr`, screen eyedropper color picker, local LAN Drop HTTP QR file transfer, and `.ics`/webcal meeting countdown.
- **Command Palette & Developer Bridge (`Modules.CommandHub`)**: Global hotkey launcher (`Ctrl+Shift+Space`), fuzzy search prefixes (`@`, `#`, `=`, `>`, `/`), C# widget plugin host, and local HTTP (`127.0.0.1:47821`) / Named Pipe automation bridge.

---

## Modular Solution Architecture

```text
DinamikAda.sln
├── src/
│   ├── DinamikAda.Core                  # Shared contracts, arbitration state machine, localization, config
│   ├── DinamikAda.Platform.Win32        # Low-level P/Invoke, hotkeys, DPI & dock layout math, WS_POPUP dimmer, fullscreen & restore detection
│   ├── DinamikAda.Modules.Media         # GSMTC, CoreAudio COM/WASAPI, spectrum FFT, LRC lyrics, WAV notes
│   ├── DinamikAda.Modules.SystemControl # DDC/CI brightness, network/power state, USB arrival/eject
│   ├── DinamikAda.Modules.Telemetry     # Real CPU/GPU/VRAM/Disk/Net counters, ICMP ping, OS cleanup
│   ├── DinamikAda.Modules.Productivity  # File shelf, EXIF cleaner, DPAPI clipboard, OCR, QR LAN transfer, iCalendar
│   ├── DinamikAda.Modules.Notifications # Windows Action Center notification mirroring & modal viewer
│   ├── DinamikAda.Modules.Widgets       # Isolated custom widget host with circuit breaker & multi-AI quota tracker
│   ├── DinamikAda.Modules.CommandHub    # Fuzzy command palette, window snapping/desktop control, localhost Developer Bridge
│   └── DinamikAda.App                   # WPF layered island & floating bottom dock shell, tray/calendar pop-ups, 240Hz aurora motion & orchestrator
└── tests/
    └── DinamikAda.Tests                 # 124 xUnit unit & integration tests
```

---

## Build, Run & Verification

### Requirements
- Windows 11 (`x64`)
- .NET 8 SDK (`net8.0-windows10.0.26100.0` targeting)

### Commands
```powershell
# Restore & build Release|x64
dotnet build DinamikAda.sln -c Release -p:Platform=x64 -m

# Run unit tests (124 tests)
dotnet test tests\DinamikAda.Tests\DinamikAda.Tests.csproj -c Release -p:Platform=x64 --no-build --verbosity normal

# Publish standalone output to .\artifacts\app\
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\build.ps1

# Launch application
.\artifacts\app\DinamikAda.App.exe
```

### Automated Verification Harnesses
```powershell
# Headless GPU & software smoke checks (IdleCpuPct < 0.5%)
.\artifacts\app\DinamikAda.App.exe --smoke-test --duration-sec 6 --trace
.\artifacts\app\DinamikAda.App.exe --smoke-test --sw --duration-sec 5 --trace

# Offline 33-frame WPF render audit
.\artifacts\app\DinamikAda.App.exe --render-audit .\render_audit
```

### Optional Sparse MSIX Notification Identity
To mirror Windows Action Center notifications via `UserNotificationListener`, register the optional local Sparse MSIX identity using Windows SDK (`makeappx.exe` & `signtool.exe`):
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\register-sparse.ps1 -BinDir .\artifacts\app
```

---

## License

Licensed under the [MIT License](LICENSE).
