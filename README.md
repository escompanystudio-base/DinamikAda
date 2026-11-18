<div align="center">

# DinamikAda

**A desktop island and an optional native taskbar theme for Windows 11.**

[English quick start](#quick-start) · [Türkçe kullanım kılavuzu](docs/kullanim.md)

<img src="docs/screenshots/explorer-taskbar.png" alt="DinamikAda’s default Explorer taskbar with separate app row and system tray" />

</div>

## Quick start

DinamikAda runs on **Windows 11 x64**. Building from source requires the **.NET 8 SDK**.

1. Clone the repository or download and extract the ZIP from the **Code** menu.
2. Run [DinamikAda-Baslat.bat](DinamikAda-Baslat.bat). The launcher builds the app on the first run and rebuilds it when source files change.
3. Click the top island to open media, audio, system and **AI Quota** controls. Press **Ctrl+Shift+Space** to open the command palette.
4. Open **Settings → Bottom Dock** to enable the standalone dock or follow the Explorer setup below.

### Explorer taskbar

1. Install [Windhawk](https://windhawk.net/) with its bundled compiler. Exit DinamikAda from its notification-area icon and run [DinamikAda-Explorer-Kur.bat](DinamikAda-Explorer-Kur.bat).
2. Open **Settings → Bottom Dock** and enable **Smart Auto-Hide** to hide the app row when it overlaps a window. Move the pointer to the **bottom center** to reveal it over a maximized window.
3. Hover over an app icon for a small lift. The rightmost arrow toggles the system tray between its regular size and a compact button. The theme hides the microphone and volume glyphs. **Win+A** opens Windows Quick Settings.
4. Play music in an app that shares album artwork with Windows. The top island, app row, system tray and Start menu follow the album colors. The taskbar colors drift during playback.
5. To restore the standard Windows taskbar, exit DinamikAda and run [DinamikAda-Windows-Barina-Don.bat](DinamikAda-Windows-Barina-Don.bat).

The work area extends to the bottom edge of the screen. Clicks in the gaps pass through to the window underneath. Explorer provides the Start menu, grouped app icons, previews and system flyouts.

| App row reveal and icon motion | Right tray toggle |
| --- | --- |
| ![The center app row rises from the bottom edge and icons lift on hover](docs/screenshots/explorer-dock.gif) | ![The system tray animates into a compact button and opens again](docs/screenshots/explorer-tray.gif) |

[Full taskbar screenshot](docs/screenshots/explorer-taskbar.png) · [Native setup and update instructions in Turkish](docs/kullanim.md#explorer-modunu-güncelleme)

### AI Quota

Open **AI Quota** in the expanded island and use the add button for your provider. Select the intended account during sign-in. If a session expires, matches another card or belongs to another account, use **Reconnect** on the affected card.

| Provider | What the card shows |
| --- | --- |
| ChatGPT / Codex | Codex usage and reset times for the connected account, including 5-hour and weekly windows when available. |
| Claude | Supported Claude Code usage windows and reset times. The usage button opens the account usage page in the browser. |
| Antigravity | Usage windows for available model pools. Compatible local Antigravity Tools data can provide saved readings. |
| Gemini, custom and API | Source-dependent details. **Custom / API** supports manual counters and supported API connections. |

Cards indicate when they show a saved reading, are waiting for quota data, or need a new sign-in.

## Other tools

The top island also includes a clipboard vault, file shelf, OCR, LAN file sharing, hardware and software brightness controls, lyrics and per-app audio routing.

## Build and verify

~~~powershell
dotnet build DinamikAda.sln -c Release -p:Platform=x64
dotnet test tests\DinamikAda.Tests\DinamikAda.Tests.csproj -c Release -p:Platform=x64
.\DinamikAda-Baslat.bat --verify
~~~

The verification launcher runs tests, publishes an isolated build, generates sample screenshots under **artifacts/render-audit**, and runs a runtime smoke check. With the Explorer profile active, run the live check:

~~~powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-explorer-taskbar.ps1 -RevealCycle
~~~

For optional Windows Action Center mirroring, register the sparse MSIX identity with the Windows SDK:

~~~powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\register-sparse.ps1 -BinDir .\artifacts\app
~~~

## Source and license

- [src](src) contains the .NET solution, Windows integration and WPF interface.
- [native](native) contains the Explorer taskbar and Start menu styling extensions.
- [tests](tests) contains .NET tests and the native dock motion tests.

The .NET application is licensed under the [MIT License](LICENSE). The native extensions are licensed under [GPL-3.0-or-later](native/COPYING). The [setup script](scripts/setup-explorer-taskbar.ps1) compiles pinned upstream mod sources and retains the original sources, generated source and hash manifest in an isolated Windhawk profile under **%LOCALAPPDATA%\DinamikAda\ExplorerHost**. Windows updates can affect compatibility with Explorer styling.
