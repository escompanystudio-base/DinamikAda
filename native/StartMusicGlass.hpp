// SPDX-License-Identifier: GPL-3.0-or-later
#pragma once

#include <unordered_map>
#include <winrt/Windows.UI.ViewManagement.h>
#include <winrt/Windows.UI.Xaml.Controls.h>
#include <winrt/Windows.UI.Xaml.Media.h>
#include "MusicGlass.hpp"
#ifdef DINAMIKADA_NOTIFICATION_GLASS
#include "DockPaletteBrush.hpp"
#endif

namespace DinamikAdaStartGlass {
namespace glass = DinamikAdaMusicGlass;
namespace xaml = winrt::Windows::UI::Xaml;
namespace hosting = xaml::Hosting;

thread_local std::unordered_map<uint64_t, glass::Surface> surfaces;
thread_local xaml::DispatcherTimer timer{nullptr};
thread_local winrt::event_token timerTick{};
thread_local glass::Palette palette;
thread_local bool motion = true;
thread_local unsigned errors = 0;

inline void Report() noexcept {
    try {
#ifdef DINAMIKADA_NOTIFICATION_GLASS
        WCHAR executable[MAX_PATH]{};
        GetModuleFileNameW(nullptr, executable, ARRAYSIZE(executable));
        auto name = wcsrchr(executable, L'\\');
        auto path = glass::HostPath(name && _wcsicmp(name + 1, L"ShellHost.exe") == 0
            ? L"quick-settings-glass-status.ini" : L"notification-glass-status.ini");
#else
        auto path = glass::HostPath(L"start-glass-status.ini");
#endif
        unsigned layers = 0;
        for (auto const& [id, surface] : surfaces) if (surface.orbiting) layers += unsigned(surface.orbs.size());
        auto count = surfaces.size();
#ifdef DINAMIKADA_NOTIFICATION_GLASS
        DinamikAdaDockPalette::ForEach([&](auto const& surface) {
            ++count;
            if (surface.moving) ++layers;
        });
#endif
        std::wstring value = L"[StartGlass]\nProcessId=" + std::to_wstring(GetCurrentProcessId()) +
            L"\nSurfaces=" + std::to_wstring(count) +
            L"\nFailures=" + std::to_wstring(errors) +
            L"\nHasSession=" + std::to_wstring(palette.active) +
            L"\nPlaying=" + std::to_wstring(palette.playing) +
            L"\nOrbitingLayers=" + std::to_wstring(layers) + L"\n";
        HANDLE file = CreateFileW(path.c_str(), GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                                  nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (file != INVALID_HANDLE_VALUE) {
            DWORD written;
            WCHAR bom = 0xFEFF;
            WriteFile(file, &bom, sizeof(bom), &written, nullptr);
            WriteFile(file, value.data(), DWORD(value.size() * sizeof(wchar_t)), &written, nullptr);
            CloseHandle(file);
        }
    } catch (...) {}
}

inline void Refresh() noexcept {
    try {
        auto next = glass::ReadPalette(glass::HostPath(L"animation-state.ini").c_str());
        bool canAnimate = winrt::Windows::UI::ViewManagement::UISettings().AnimationsEnabled();
        if (next == palette && canAnimate == motion) return;
        palette = next;
        motion = canAnimate;
        for (auto& [id, surface] : surfaces) glass::Apply(surface, palette, motion);
#ifdef DINAMIKADA_NOTIFICATION_GLASS
        DinamikAdaDockPalette::ForEach([](auto& surface) { DinamikAdaDockPalette::Apply(surface, palette, motion); });
#endif
        Report();
    } catch (winrt::hresult_error const& ex) {
        errors++;
        Wh_Log(L"Start glass: %s", ex.message().c_str());
        Report();
    } catch (...) { errors++; Report(); }
}

inline void EnsureTimer() {
    if (timer) return;
    palette = glass::ReadPalette(glass::HostPath(L"animation-state.ini").c_str());
    motion = winrt::Windows::UI::ViewManagement::UISettings().AnimationsEnabled();
    timer = xaml::DispatcherTimer();
    timer.Interval(std::chrono::milliseconds(500));
    timerTick = timer.Tick([](auto&&, auto&&) { Refresh(); });
    timer.Start();
}

inline void Attach(uint64_t id, xaml::FrameworkElement const& element) noexcept {
    try {
#ifdef DINAMIKADA_NOTIFICATION_GLASS
        if (DinamikAdaDockPalette::Identify(element) == DinamikAdaDockPalette::Capsule::Panel) EnsureTimer();
#else
        if (surfaces.contains(id) || element.Name() != L"AcrylicBorder") return;
        auto border = element.try_as<xaml::Controls::Border>();
        if (!border) return;
        auto parent = xaml::Media::VisualTreeHelper::GetParent(element);
        for (int depth = 0; parent && depth < 10; depth++,
                parent = xaml::Media::VisualTreeHelper::GetParent(parent)) {
            if (auto ancestor = parent.try_as<xaml::FrameworkElement>(); ancestor && ancestor.Name() == L"CompanionRoot") return;
        }
        float radius = float(border.CornerRadius().TopLeft);
        if (hosting::ElementCompositionPreview::GetElementChildVisual(element)) return;
        surfaces.emplace(id, glass::Create(element, radius > 0 ? radius : 12.f, true));
        auto& surface = surfaces.at(id);
        hosting::ElementCompositionPreview::SetElementChildVisual(element, surface.root);
        EnsureTimer();
        glass::Apply(surface, palette, motion);
        Report();
#endif
    } catch (winrt::hresult_error const& ex) {
        errors++;
        Wh_Log(L"Start glass attach: %s", ex.message().c_str());
        Report();
    } catch (...) { errors++; Report(); }
}

inline void Detach(uint64_t id) noexcept {
    try {
        auto it = surfaces.find(id);
        if (it == surfaces.end()) return;
        auto surface = std::move(it->second);
        surfaces.erase(it);
        glass::Restore(surface);
        if (surfaces.empty() && timer) {
            timer.Stop();
            timer.Tick(timerTick);
            timer = nullptr;
        }
        Report();
    } catch (...) { errors++; Report(); }
}

inline void Clear() noexcept {
    while (!surfaces.empty()) Detach(surfaces.begin()->first);
    if (timer) {
        timer.Stop();
        timer.Tick(timerTick);
        timer = nullptr;
    }
}
} // namespace DinamikAdaStartGlass
