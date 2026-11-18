// SPDX-License-Identifier: GPL-3.0-or-later
// DinamikAda animation extension for m417z's Windows 11 Taskbar Styler.
// Included in a generated translation unit; upstream source stays intact on disk.
#pragma once
#include <winrt/Windows.UI.Xaml.Input.h>
#include <winrt/Windows.Devices.Input.h>
#include <winrt/Windows.UI.Input.h>
#include "DockMotion.hpp"
#include "DockPaletteBrush.hpp"
#include "ExplorerDockReveal.hpp"
#include "ExplorerTray.hpp"

namespace DinamikAdaAnimations {
namespace xaml = winrt::Windows::UI::Xaml;
namespace media = xaml::Media;
namespace comp = winrt::Windows::UI::Composition;
namespace hosting = xaml::Hosting;
namespace motion = DinamikAdaDockMotion;
namespace glass = DinamikAdaMusicGlass;
namespace paletteBrush = DinamikAdaDockPalette;
using winrt::Windows::Foundation::Numerics::float2;
using winrt::Windows::Foundation::Numerics::float3;

struct Button {
    winrt::weak_ref<xaml::FrameworkElement> element;
    comp::Visual icon{nullptr};
    winrt::weak_ref<xaml::FrameworkElement> iconElement;
    float3 originalScale{1, 1, 1};
    float3 originalCenter{};
    float3 originalTranslation{};
    bool translationWasEnabled = false;
    comp::CompositionEasingFunction enterCurve{nullptr}, followCurve{nullptr}, releaseCurve{nullptr};
    float2 pivotSize{-1, -1};
    float weight = 0, lastWeight = 0;
    winrt::event_token entered{}, exited{}, moved{};
};
struct Settings {
    bool magnify = true, animations = true;
    glass::Palette palette;
};
thread_local std::unordered_map<uint64_t, Button> buttons;
thread_local std::unordered_map<uint64_t, winrt::Windows::Foundation::IInspectable> handlers;
thread_local xaml::DispatcherTimer timer{nullptr};
thread_local xaml::DispatcherTimer releaseTimer{nullptr};
thread_local winrt::event_token timerTick{};
thread_local winrt::event_token releaseTick{};
thread_local Settings settings;
thread_local unsigned hoverEvents = 0, clickEvents = 0, auditRuns = 0, failures = 0;
thread_local unsigned auditButtons = 0, auditAuras = 0, auditPaletteStops = 0;
thread_local unsigned auditWaveNeighbors = 0, auditLiftControllers = 0, waveUpdates = 0;
thread_local uint64_t hoverOwner = 0;
thread_local uint64_t pendingRelease = 0;
thread_local ULONGLONG lastWaveTick = 0;
thread_local int auditRequest = 0;

void Status() noexcept {
    try {
        auto path = glass::HostPath(L"animation-status.ini");
        unsigned appBrushes = 0, trayBrushes = 0, overflowBrushes = 0;
        paletteBrush::ForEach([&](auto const& surface) {
            if (surface.capsule == paletteBrush::Capsule::Apps) appBrushes++;
            if (surface.capsule == paletteBrush::Capsule::Tray) trayBrushes++;
            if (surface.capsule == paletteBrush::Capsule::Overflow) overflowBrushes++;
        });
        std::wstring value = L"[Native]\nProcessId=" + std::to_wstring(GetCurrentProcessId()) +
            L"\nThreadId=" + std::to_wstring(GetCurrentThreadId()) +
            L"\nButtons=" + std::to_wstring(buttons.size()) +
            L"\nAuroras=" + std::to_wstring(appBrushes + trayBrushes) +
            L"\nAppPaletteBrushes=" + std::to_wstring(appBrushes) +
            L"\nTrayPaletteBrushes=" + std::to_wstring(trayBrushes) +
            L"\nOverflowPaletteBrushes=" + std::to_wstring(overflowBrushes) +
            L"\nMusicDriftMs=" + std::to_wstring(paletteBrush::driftDuration.count()) +
            L"\nHoverEvents=" + std::to_wstring(hoverEvents) +
            L"\nClickEvents=" + std::to_wstring(clickEvents) +
            L"\nAuditRuns=" + std::to_wstring(auditRuns) +
            L"\nAuditScaleControllers=" + std::to_wstring(auditButtons) +
            L"\nAuditAuraControllers=" + std::to_wstring(auditAuras) +
            L"\nAuditPaletteStops=" + std::to_wstring(auditPaletteStops) +
            L"\nAuditWaveNeighbors=" + std::to_wstring(auditWaveNeighbors) +
            L"\nAuditLiftControllers=" + std::to_wstring(auditLiftControllers) +
            L"\nWaveUpdates=" + std::to_wstring(waveUpdates) +
            L"\nDurationMs=" + std::to_wstring(motion::hoverMs) +
            L"\nIconLiftDip=" + std::to_wstring(int(motion::liftDip)) +
            L"\nMaxScalePercent=" + std::to_wstring(std::lround((1.f + motion::magnification) * 100.f)) + L"\n" +
            L"\nPlaying=" + std::to_wstring(settings.palette.playing) +
            L"\nAnimationsEnabled=" + std::to_wstring(settings.animations) +
            L"\nFailures=" + std::to_wstring(failures) + L"\n" + DinamikAdaDockReveal::Status() + DinamikAdaTray::Status();
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

xaml::FrameworkElement FindIcon(xaml::DependencyObject const& parent, int depth = 0) {
    if (depth > 10) return nullptr;
    int count = media::VisualTreeHelper::GetChildrenCount(parent);
    for (int i = 0; i < count; i++) {
        auto child = media::VisualTreeHelper::GetChild(parent, i);
        if (auto fe = child.try_as<xaml::FrameworkElement>()) {
            auto name = fe.Name();
            if (name == L"Icon" || name == L"IconImage" || name == L"AppIcon") return fe;
            if (auto found = FindIcon(child, depth + 1)) return found;
        }
    }
    return nullptr;
}

bool ResolveIcon(Button& button) {
    if (button.icon) return true;
    auto element = button.element.get();
    if (!element) return false;
    auto icon = FindIcon(element);
    if (!icon) return false; // Never scale the hit-test surface or the system tray.
    button.icon = hosting::ElementCompositionPreview::GetElementVisual(icon);
    button.iconElement = winrt::make_weak(icon);
    button.originalScale = button.icon.Scale();
    button.originalCenter = button.icon.CenterPoint();
    button.translationWasEnabled = button.icon.Properties().TryGetVector3(L"Translation", button.originalTranslation)
        == comp::CompositionGetValueStatus::Succeeded;
    if (!button.translationWasEnabled) hosting::ElementCompositionPreview::SetIsTranslationEnabled(icon, true);
    auto compositor = button.icon.Compositor();
    button.enterCurve = compositor.CreateCubicBezierEasingFunction({.20f, .68f}, {.25f, 1.f});
    button.followCurve = compositor.CreateCubicBezierEasingFunction({.23f, .55f}, {.32f, 1.f});
    button.releaseCurve = compositor.CreateCubicBezierEasingFunction({.22f, .61f}, {.36f, 1.f});
    return true;
}

void Animate(Button& button, bool force) {
    if (!ResolveIcon(button)) return;
    auto visual = button.icon;
    auto size = visual.Size();
    if (size.x != button.pivotSize.x || size.y != button.pivotSize.y) {
        visual.CenterPoint({size.x * .5f, size.y * .5f, 0});
        button.pivotSize = size;
    }
    auto base = button.originalScale;
    auto sample = motion::AtWeight(settings.magnify && settings.animations ? button.weight : 0.f);
    if (!settings.animations) {
        visual.StopAnimation(L"Scale");
        visual.StopAnimation(L"Translation.Y");
        visual.Scale(base);
        visual.Properties().InsertVector3(L"Translation", button.originalTranslation);
        button.lastWeight = 0;
        return;
    }
    if (button.lastWeight != sample.weight || force) {
        const bool returning = sample.weight == 0;
        const bool entering = button.lastWeight == 0;
        auto const& curve = returning ? button.releaseCurve : entering ? button.enterCurve : button.followCurve;
        int duration = returning ? motion::releaseMs : entering ? motion::hoverMs : motion::followMs;
        auto compositor = visual.Compositor();
        auto scale = compositor.CreateVector3KeyFrameAnimation();
        scale.InsertExpressionKeyFrame(0.f, L"this.StartingValue");
        scale.InsertKeyFrame(1.f, {base.x * sample.scale, base.y * sample.scale, base.z}, curve);
        scale.Duration(std::chrono::milliseconds(duration));
        scale.StopBehavior(comp::AnimationStopBehavior::LeaveCurrentValue);
        visual.StartAnimation(L"Scale", scale);
        if (button.lastWeight != sample.weight) {
            auto lift = compositor.CreateScalarKeyFrameAnimation();
            lift.InsertExpressionKeyFrame(0.f, L"this.StartingValue");
            lift.InsertKeyFrame(1.f, button.originalTranslation.y + sample.y, curve);
            lift.Duration(std::chrono::milliseconds(duration));
            lift.StopBehavior(comp::AnimationStopBehavior::LeaveCurrentValue);
            visual.StartAnimation(L"Translation.Y", lift);
        }
        button.lastWeight = sample.weight;
    }
}

void ResetWave() {
    hoverOwner = 0;
    for (auto& [id, button] : buttons) {
        if (button.weight == 0) continue;
        button.weight = 0;
        Animate(button, false);
    }
}

void WaveAt(xaml::FrameworkElement const& source, float pointerX) {
    auto root = source.XamlRoot();
    if (!root) return;
    auto content = root.Content();
    for (auto& [id, button] : buttons) {
        auto element = button.element.get();
        if (!element || !element.XamlRoot() || element.XamlRoot().Content() != content) continue;
        auto center = element.TransformToVisual(content).TransformPoint({float(element.ActualWidth() * .5), 0});
        auto sample = DinamikAdaDockMotion::Evaluate(center.X - pointerX);
        if (button.weight == sample.weight || (sample.weight != 0 && std::abs(button.weight - sample.weight) < .003f)) continue;
        button.weight = sample.weight;
        Animate(button, false);
    }
    waveUpdates++;
}

void PointerWave(uint64_t id, xaml::Input::PointerRoutedEventArgs const& args, bool force) {
    if (!settings.magnify || !settings.animations || args.Pointer().PointerDeviceType() != winrt::Windows::Devices::Input::PointerDeviceType::Mouse) return;
    auto it = buttons.find(id);
    if (it == buttons.end()) return;
    auto source = it->second.element.get();
    if (!source || !source.XamlRoot()) return;
    hoverOwner = id;
    if (pendingRelease) {
        pendingRelease = 0;
        releaseTimer.Stop();
    }
    auto now = GetTickCount64();
    if (!force && now - lastWaveTick < 8) return;
    lastWaveTick = now;
    WaveAt(source, args.GetCurrentPoint(source.XamlRoot().Content()).Position().X);
}

void Refresh() noexcept {
    try {
        auto path = glass::HostPath(L"animation-state.ini");
        auto read = [&](PCWSTR key, int fallback) { return GetPrivateProfileIntW(L"Animation", key, fallback, path.c_str()); };
        bool motionEnabled = winrt::Windows::UI::ViewManagement::UISettings().AnimationsEnabled();
        bool magnify = read(L"Magnification", 1);
        bool updateIcons = settings.animations != motionEnabled || settings.magnify != magnify;
        auto palette = glass::ReadPalette(path.c_str());
        bool updateGlass = settings.animations != motionEnabled || settings.palette != palette;
        settings.animations = motionEnabled;
        settings.magnify = magnify;
        settings.palette = palette;
        DinamikAdaDockReveal::smartHide = read(L"SmartHide", 1);
        if (updateGlass) {
            paletteBrush::ForEach([](auto& surface) { paletteBrush::Apply(surface, settings.palette, settings.animations); });
        }
        if (updateIcons) {
            for (auto& [id, button] : buttons) Animate(button, false);
        }
        if (updateGlass || updateIcons) Status();
        auto auditPath = glass::HostPath(L"animation-audit.ini");
        int requested = GetPrivateProfileIntW(L"Animation", L"Request", 0, auditPath.c_str());
        if (requested != auditRequest) {
            auditRequest = requested;
            auditButtons = auditAuras = auditPaletteStops = auditWaveNeighbors = auditLiftControllers = 0;
            if (!buttons.empty()) {
                auto source = buttons.begin()->second.element.get();
                if (source && source.XamlRoot()) {
                    auto center = source.TransformToVisual(source.XamlRoot().Content()).TransformPoint({float(source.ActualWidth() * .5), 0});
                    WaveAt(source, center.X);
                    for (auto& [id, button] : buttons) if (button.weight > .02f) auditWaveNeighbors++;
                }
            }
            for (auto& [id, button] : buttons) {
                // Exercise the same composition path as pointer input on the live Explorer tree.
                Animate(button, true);
                if (button.icon && button.icon.TryGetAnimationController(L"Scale")) auditButtons++;
                if (button.weight > .02f && button.icon && button.icon.TryGetAnimationController(L"Translation.Y")) auditLiftControllers++;
            }
            auto auditPalette = settings.palette;
            auditPalette.active = auditPalette.playing = true;
            for (auto& color : auditPalette.colors) color.R = 255 - color.R;
            paletteBrush::ForEach([&](auto& surface) {
                paletteBrush::Apply(surface, auditPalette, settings.animations);
                if (surface.brush.TryGetAnimationController(L"StartPoint")) auditAuras++;
                for (auto stop : surface.brush.ColorStops())
                    if (stop.TryGetAnimationController(L"Color")) auditPaletteStops++;
                paletteBrush::Apply(surface, settings.palette, settings.animations);
            });
            ResetWave();
            auditRuns++;
            Status();
        }
    } catch (winrt::hresult_error const& ex) { Wh_Log(L"DinamikAda Refresh: %s", ex.message().c_str()); failures++; Status(); }
      catch (...) { failures++; Status(); }
}

void EnsureTimer() {
    if (timer) return;
    releaseTimer = xaml::DispatcherTimer();
    releaseTimer.Interval(std::chrono::milliseconds(36));
    releaseTick = releaseTimer.Tick([](auto&&, auto&&) {
        releaseTimer.Stop();
        try { if (pendingRelease && hoverOwner == pendingRelease) ResetWave(); }
        catch (...) { failures++; Status(); }
        pendingRelease = 0;
    });
    timer = xaml::DispatcherTimer();
    auto auditPath = glass::HostPath(L"animation-audit.ini");
    auditRequest = GetPrivateProfileIntW(L"Animation", L"Request", 0, auditPath.c_str());
    timer.Interval(std::chrono::milliseconds(500));
    timerTick = timer.Tick([](auto&&, auto&&) { Refresh(); });
    timer.Start();
    Refresh();
}

void Attach(uint64_t id, xaml::FrameworkElement const& element) noexcept {
    try {
        DinamikAdaDockReveal::Attach(id, element);
        DinamikAdaTray::Attach(id, element);
        auto type = winrt::get_class_name(element);
        if ((type == L"Taskbar.TaskListButton" || type == L"Taskbar.TaskListLabeledButton") && !buttons.contains(id)) {
            // Insert first: XAML/Composition calls can re-enter the diagnostics callback.
            buttons.emplace(id, Button{winrt::make_weak(element)});
            auto& state = buttons.at(id);
            state.entered = element.PointerEntered([id](auto&&, xaml::Input::PointerRoutedEventArgs const& args) noexcept {
                try { PointerWave(id, args, true); hoverEvents++; Status(); }
                catch (...) { failures++; Status(); }
            });
            state.exited = element.PointerExited([id](auto&&, auto&&) noexcept {
                try { if (releaseTimer && hoverOwner == id) {
                    pendingRelease = id;
                    releaseTimer.Start();
                } }
                catch (...) { failures++; Status(); }
            });
            state.moved = element.PointerMoved([id](auto&&, xaml::Input::PointerRoutedEventArgs const& args) noexcept {
                try { PointerWave(id, args, false); }
                catch (...) { failures++; Status(); }
            });
            // Native Button controls can mark the routed press handled. handledEventsToo is essential.
            xaml::Input::PointerEventHandler pressed = [id](auto&&, auto&&) noexcept {
                try { if (buttons.contains(id)) { clickEvents++; Status(); } }
                catch (...) { failures++; Status(); }
            };
            auto boxed = winrt::box_value(pressed);
            element.AddHandler(xaml::UIElement::PointerPressedEvent(), boxed, true);
            // Retain the handler for RemoveHandler during unload.
            handlers.emplace(id, boxed);
            Wh_Log(L"DinamikAda: native taskbar button attached (%llu)", id);
            EnsureTimer();
            Status();
        }
        if (paletteBrush::Identify(element) != paletteBrush::Capsule::None) EnsureTimer();
    } catch (winrt::hresult_error const& ex) { Wh_Log(L"DinamikAda Attach: %s", ex.message().c_str()); failures++; Status(); }
      catch (...) { failures++; Status(); }
}

void Detach(uint64_t id) noexcept {
    try {
        DinamikAdaDockReveal::Detach(id);
        DinamikAdaTray::Detach(id);
        if (auto it = buttons.find(id); it != buttons.end()) {
            auto state = std::move(it->second);
            buttons.erase(it);
            if (auto element = state.element.get()) {
                element.PointerEntered(state.entered);
                element.PointerExited(state.exited);
                element.PointerMoved(state.moved);
                if (handlers.contains(id)) element.RemoveHandler(xaml::UIElement::PointerPressedEvent(), handlers.at(id));
            }
            handlers.erase(id);
            if (state.icon) {
                state.icon.StopAnimation(L"Scale");
                state.icon.StopAnimation(L"Translation.Y");
                state.icon.Scale(state.originalScale);
                state.icon.CenterPoint(state.originalCenter);
                if (state.translationWasEnabled)
                    state.icon.Properties().InsertVector3(L"Translation", state.originalTranslation);
                else if (auto icon = state.iconElement.get())
                    hosting::ElementCompositionPreview::SetIsTranslationEnabled(icon, false);
            }
        }
    } catch (...) { failures++; }
}

void Clear() noexcept {
    DinamikAdaTray::Clear();
    DinamikAdaDockReveal::Clear();
    if (timer) { timer.Stop(); timer.Tick(timerTick); timer = nullptr; }
    if (releaseTimer) { releaseTimer.Stop(); releaseTimer.Tick(releaseTick); releaseTimer = nullptr; }
    hoverOwner = pendingRelease = 0;
    while (!buttons.empty()) Detach(buttons.begin()->first);
    paletteBrush::surfaces.clear();
    handlers.clear();
    Status();
}
} // namespace DinamikAdaAnimations

inline void DinamikAdaDockReveal::Report() { DinamikAdaAnimations::Status(); }
inline void DinamikAdaTray::Report() { DinamikAdaAnimations::Status(); }
