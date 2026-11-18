// SPDX-License-Identifier: GPL-3.0-or-later
// Included after Taskbar Styler's click-through helpers in the generated source.
#pragma once
#include "DockMotion.hpp"

namespace DinamikAdaDockReveal {
namespace xaml = winrt::Windows::UI::Xaml;
namespace media = xaml::Media;
namespace anim = media::Animation;
namespace motion = DinamikAdaDockMotion;
struct Surface {
    winrt::weak_ref<xaml::FrameworkElement> element;
    winrt::Windows::Foundation::IInspectable previousLocal{nullptr};
    media::TranslateTransform offset{nullptr};
    anim::Storyboard animation{nullptr};
    winrt::Windows::Foundation::IInspectable previousOpacity{nullptr};
    double visibleOpacity = 1.0;
};
thread_local std::unordered_map<uint64_t, Surface> surfaces;
thread_local xaml::DispatcherTimer timer{nullptr};
thread_local winrt::event_token tick{};
thread_local HWND taskbar = nullptr;
thread_local HWND islandWindow = nullptr;
thread_local uint64_t backgroundId = 0;
thread_local bool shown = true;
thread_local bool smartHide = true;
thread_local ULONGLONG lastNear = 0, animationUntil = 0;
thread_local ULONGLONG nextAuditRead = 0;
thread_local std::wstring auditPath;
thread_local int forcedState = 0;
thread_local unsigned showEvents = 0, hideEvents = 0, errors = 0;
thread_local uint32_t lastErrorCode = 0;
thread_local DWORD topmostError = 0;
thread_local bool zOrderGuard = false;
inline void Report();

LRESULT CALLBACK KeepAboveApps(HWND window, UINT message, WPARAM wParam, LPARAM lParam, DWORD_PTR) {
    LRESULT result = DefSubclassProc(window, message, wParam, lParam);
    if (message == WM_WINDOWPOSCHANGING && lParam) {
        // Explorer changes an auto-hidden taskbar back to NOTOPMOST after app activation.
        // Keep the native root in the overlay band after its own handler has run.
        auto position = reinterpret_cast<WINDOWPOS*>(lParam);
        position->hwndInsertAfter = HWND_TOPMOST;
        position->flags &= ~SWP_NOZORDER;
        position->flags |= SWP_NOACTIVATE;
    }
    return result;
}

void Raise() {
    if (!taskbar) return;
    if (!zOrderGuard) zOrderGuard = WindhawkUtils::SetWindowSubclassFromAnyThread(taskbar, KeepAboveApps, 0);
    if (!SetWindowPos(taskbar, HWND_TOPMOST, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOSENDCHANGING | SWP_SHOWWINDOW))
        topmostError = GetLastError();
    else topmostError = 0;
}

std::wstring Path(PCWSTR name) {
    WCHAR base[32768];
    ExpandEnvironmentStringsW(L"%LOCALAPPDATA%\\DinamikAda\\ExplorerHost\\", base, ARRAYSIZE(base));
    return std::wstring(base) + name;
}

std::wstring Status() {
    return L"\nRevealShown=" + std::to_wstring(shown) + L"\nRevealSurfaces=" + std::to_wstring(surfaces.size()) +
        L"\nRevealEvents=" + std::to_wstring(showEvents) + L"\nHideEvents=" + std::to_wstring(hideEvents) +
        L"\nRevealErrors=" + std::to_wstring(errors) + L"\nTrayMoves=0\n" +
        L"RevealErrorCode=" + std::to_wstring(lastErrorCode) + L"\n" +
        L"RevealDurationMs=" + std::to_wstring(motion::revealMs) + L"\n" +
        L"ConcealDurationMs=" + std::to_wstring(motion::concealMs) + L"\n" +
        L"RevealTravelDip=" + std::to_wstring(int(motion::travelDip)) + L"\n" +
        L"RevealRoot=" + std::to_wstring(reinterpret_cast<uintptr_t>(taskbar)) +
        L"\nRevealTopmost=" + std::to_wstring(taskbar && (GetWindowLongPtrW(taskbar, GWL_EXSTYLE) & WS_EX_TOPMOST) ? 1 : 0) +
        L"\nRevealTopmostError=" + std::to_wstring(topmostError) + L"\n";
}

void SetShown(bool visible, ULONGLONG now) {
    if (visible == shown) return;
    shown = visible;
    if (visible) showEvents++; else hideEvents++;
    const bool animated = winrt::Windows::UI::ViewManagement::UISettings().AnimationsEnabled();
    const int duration = visible ? motion::revealMs : motion::concealMs;
    for (auto& [id, surface] : surfaces) {
        auto element = surface.element.get();
        if (!element) continue;
        double currentY = surface.offset.Y();
        double currentOpacity = element.Opacity();
        if (surface.animation) surface.animation.Stop();
        surface.animation = nullptr;
        if (!animated) {
            surface.offset.Y(visible ? 0.0 : motion::hiddenDip);
            element.Opacity(visible ? surface.visibleOpacity : 0.0);
            continue;
        }
        if (visible) currentY = motion::RevealStart(currentY, currentOpacity);
        surface.offset.Y(currentY);
        element.Opacity(currentOpacity);

        anim::DoubleAnimation movement;
        movement.To(visible ? 0.0 : motion::travelDip);
        movement.Duration({std::chrono::milliseconds(duration), xaml::DurationType::TimeSpan});
        anim::CubicEase movementCurve;
        movementCurve.EasingMode(visible ? anim::EasingMode::EaseOut : anim::EasingMode::EaseInOut);
        movement.EasingFunction(movementCurve);
        anim::Storyboard::SetTarget(movement, surface.offset);
        anim::Storyboard::SetTargetProperty(movement, L"Y");

        anim::DoubleAnimation fade;
        fade.To(visible ? surface.visibleOpacity : 0.0);
        fade.Duration({std::chrono::milliseconds(visible ? duration - 14 : duration), xaml::DurationType::TimeSpan});
        anim::SineEase opacityCurve;
        opacityCurve.EasingMode(visible ? anim::EasingMode::EaseOut : anim::EasingMode::EaseInOut);
        fade.EasingFunction(opacityCurve);
        anim::Storyboard::SetTarget(fade, element);
        anim::Storyboard::SetTargetProperty(fade, L"Opacity");

        surface.animation = anim::Storyboard();
        surface.animation.Children().Append(movement);
        surface.animation.Children().Append(fade);
        surface.animation.Begin();
    }
    animationUntil = now + (animated ? duration + 16 : 1);
    if (visible) Raise();
    Report();
}

void FinishTransition() {
    for (auto& [id, surface] : surfaces) {
        if (surface.animation) surface.animation.Stop();
        surface.animation = nullptr;
        // Keep the short visual travel independent of the final click-through position.
        surface.offset.Y(shown ? 0.0 : motion::hiddenDip);
        if (auto element = surface.element.get()) element.Opacity(shown ? surface.visibleOpacity : 0.0);
    }
}

void Poll() noexcept {
    try {
        auto found = surfaces.find(backgroundId);
        if (found == surfaces.end()) return;
        auto element = found->second.element.get();
        if (!element || !element.XamlRoot() || element.ActualWidth() <= 0) return;
        if (!islandWindow || !IsWindow(islandWindow)) {
            islandWindow = ResolveClickThroughIslandHwnd(element.XamlRoot());
            auto top = islandWindow ? GetAncestor(islandWindow, GA_ROOT) : nullptr;
            if (top && !IsTaskbarTopLevelWindow(top)) top = FindWindowW(L"Shell_TrayWnd", nullptr);
            if (taskbar != top) {
                if (taskbar && zOrderGuard) WindhawkUtils::RemoveWindowSubclassFromAnyThread(taskbar, KeepAboveApps);
                taskbar = top;
                zOrderGuard = false;
            }
        }
        if (!taskbar || !islandWindow) return;
        POINT cursor;
        if (!GetCursorPos(&cursor)) return;
        MONITORINFO monitor{sizeof(monitor)};
        if (!GetMonitorInfoW(MonitorFromWindow(taskbar, MONITOR_DEFAULTTONEAREST), &monitor)) return;
        auto root = element.XamlRoot().Content();
        auto bounds = element.TransformToVisual(root).TransformBounds({0, 0, float(element.ActualWidth()), float(element.ActualHeight())});
        double scale = GetDpiForWindow(taskbar) / 96.0;
        POINT origin{};
        ClientToScreen(islandWindow, &origin);
        double left = origin.x + bounds.X * scale;
        double right = left + bounds.Width * scale;
        double top = origin.y + (bounds.Y - found->second.offset.Y()) * scale;
        bool inSpan = cursor.x >= left - 8 * scale && cursor.x <= right + 8 * scale;
        bool atEdge = inSpan && cursor.y >= monitor.rcMonitor.bottom - 3 * scale && cursor.y < monitor.rcMonitor.bottom;
        bool overDock = shown && inSpan && cursor.y >= top - 6 * scale && cursor.y < monitor.rcMonitor.bottom;
        GUITHREADINFO gui{sizeof(gui)};
        bool menuOpen = GetGUIThreadInfo(GetCurrentThreadId(), &gui) && (gui.flags & (GUI_INMENUMODE | GUI_POPUPMENUMODE));
        auto now = GetTickCount64();
        if (atEdge || overDock || menuOpen) lastNear = now;
        bool visible = !smartHide || now - lastNear < 225;
        if (now >= nextAuditRead) {
            forcedState = GetPrivateProfileIntW(L"Reveal", L"State", 0, auditPath.c_str());
            nextAuditRead = now + 125;
        }
        if (forcedState == 1) visible = true;
        if (forcedState == 2) visible = false;
        SetShown(visible, now);
        // Keep the tray visible above maximized windows without activating Explorer.
        if (!(GetWindowLongPtrW(taskbar, GWL_EXSTYLE) & WS_EX_TOPMOST))
            Raise();
        if (animationUntil) {
            if (now >= animationUntil) {
                animationUntil = 0;
                FinishTransition();
            }
            ReapplyClickThroughForTopLevel(taskbar);
        }
    } catch (winrt::hresult_error const& ex) { errors++; lastErrorCode = uint32_t(ex.code()); if (errors < 4) { Wh_Log(L"Dock reveal: %s", ex.message().c_str()); Report(); } }
      catch (...) { errors++; if (errors < 4) Report(); }
}

void Attach(uint64_t id, xaml::FrameworkElement const& element) noexcept {
    try {
        if (surfaces.contains(id)) return;
        auto type = winrt::get_class_name(element);
        bool background = type == L"Taskbar.TaskbarBackground" && element.Name() == L"BackgroundControl";
        bool app = type == L"Taskbar.TaskListButton" || type == L"Taskbar.TaskListLabeledButton" ||
            type == L"Taskbar.ExperienceToggleButton" || type == L"Taskbar.OverflowToggleButton";
        if (!background && !app) return;
        auto parent = media::VisualTreeHelper::GetParent(element);
        bool inFrame = false;
        for (int depth = 0; parent && depth < 12; depth++, parent = media::VisualTreeHelper::GetParent(parent))
            if (winrt::get_class_name(parent) == L"Taskbar.TaskbarFrame") { inFrame = true; break; }
        if (!inFrame) return;
        Surface surface;
        surface.element = winrt::make_weak(element);
        surface.previousLocal = element.ReadLocalValue(xaml::UIElement::RenderTransformProperty());
        surface.previousOpacity = element.ReadLocalValue(xaml::UIElement::OpacityProperty());
        surface.visibleOpacity = element.Opacity();
        surface.offset = media::TranslateTransform();
        surface.offset.Y(shown ? 0.0 : motion::hiddenDip);
        auto group = media::TransformGroup();
        if (auto previous = element.RenderTransform()) group.Children().Append(previous);
        group.Children().Append(surface.offset);
        surfaces.emplace(id, std::move(surface));
        element.RenderTransform(group);
        if (!shown) element.Opacity(0.0);
        if (background) {
            backgroundId = id;
            islandWindow = nullptr;
        }
        if (!timer) {
            lastNear = GetTickCount64();
            auto state = Path(L"animation-state.ini");
            auditPath = Path(L"reveal-audit.ini");
            smartHide = GetPrivateProfileIntW(L"Animation", L"SmartHide", 1, state.c_str());
            timer = xaml::DispatcherTimer();
            timer.Interval(std::chrono::milliseconds(16));
            tick = timer.Tick([](auto&&, auto&&) { Poll(); });
            timer.Start();
        }
    } catch (...) { errors++; }
}

void Detach(uint64_t id) noexcept {
    try {
        auto it = surfaces.find(id);
        if (it == surfaces.end()) return;
        auto surface = std::move(it->second);
        surfaces.erase(it);
        if (surface.animation) surface.animation.Stop();
        if (auto element = surface.element.get()) {
            if (surface.previousLocal == xaml::DependencyProperty::UnsetValue()) element.ClearValue(xaml::UIElement::RenderTransformProperty());
            else element.SetValue(xaml::UIElement::RenderTransformProperty(), surface.previousLocal);
            if (surface.previousOpacity == xaml::DependencyProperty::UnsetValue()) element.ClearValue(xaml::UIElement::OpacityProperty());
            else element.SetValue(xaml::UIElement::OpacityProperty(), surface.previousOpacity);
        }
        if (backgroundId == id) backgroundId = 0;
    } catch (...) { errors++; }
}

void Clear() noexcept {
    try { if (timer) { timer.Stop(); timer.Tick(tick); timer = nullptr; } } catch (...) {}
    while (!surfaces.empty()) Detach(surfaces.begin()->first);
    if (taskbar && zOrderGuard) WindhawkUtils::RemoveWindowSubclassFromAnyThread(taskbar, KeepAboveApps);
    zOrderGuard = false;
    taskbar = nullptr;
    islandWindow = nullptr;
    shown = true;
    backgroundId = 0;
    animationUntil = nextAuditRead = 0;
    forcedState = 0;
}
} // namespace DinamikAdaDockReveal
