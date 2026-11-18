// SPDX-License-Identifier: GPL-3.0-or-later
#pragma once

#include <memory>
#include <vector>
#include <winrt/Windows.UI.ViewManagement.h>
#include <winrt/Windows.UI.Xaml.Media.h>
#include "MusicGlass.hpp"

namespace DinamikAdaDockPalette {
namespace glass = DinamikAdaMusicGlass;
namespace comp = glass::comp;
namespace xaml = glass::xaml;
using glass::Color;
using glass::float2;
using namespace std::chrono_literals;

enum class Capsule { None, Apps, Tray };

inline Capsule Identify(xaml::UIElement const& element) {
    auto frame = element.try_as<xaml::FrameworkElement>();
    if (!frame) return Capsule::None;
    if (frame.Name() == L"SystemTrayFrameGrid") return Capsule::Tray;
    if (winrt::get_class_name(frame) != L"Windows.UI.Xaml.Controls.Grid") return Capsule::None;
    auto parent = xaml::Media::VisualTreeHelper::GetParent(frame).try_as<xaml::FrameworkElement>();
    return parent && parent.Name() == L"BackgroundControl" &&
        winrt::get_class_name(parent) == L"Taskbar.TaskbarBackground" ? Capsule::Apps : Capsule::None;
}

struct Surface {
    comp::CompositionLinearGradientBrush brush{nullptr};
    Capsule capsule = Capsule::None;
    Color fallback{};
    bool opaque = false;
    bool painted = false;
    bool moving = false;
    bool motion = false;
    glass::Palette previous;
};

inline thread_local std::vector<std::weak_ptr<Surface>> surfaces;

inline constexpr float2 startPoint{0.f, .18f};
inline constexpr float2 endPoint{1.f, .86f};
inline constexpr auto driftDuration = 8200ms;
inline constexpr float driftDistance = .29f;

inline void SetDrift(Surface& surface, bool move, bool animationEnabled) {
    auto brush = surface.brush;
    if (!animationEnabled) {
        brush.StopAnimation(L"StartPoint");
        brush.StopAnimation(L"EndPoint");
        brush.StartPoint(startPoint);
        brush.EndPoint(endPoint);
        return;
    }

    auto startController = brush.TryGetAnimationController(L"StartPoint");
    auto endController = brush.TryGetAnimationController(L"EndPoint");
    if (startController && endController) {
        if (move) {
            startController.Resume();
            endController.Resume();
        } else {
            startController.Pause();
            endController.Pause();
        }
        return;
    }
    if (!move) return;

    auto compositor = brush.Compositor();
    auto easeOut = compositor.CreateCubicBezierEasingFunction({.28f, .46f}, {.66f, 1.f});
    auto easeInOut = compositor.CreateCubicBezierEasingFunction({.36f, 0.f}, {.64f, 1.f});
    auto easeIn = compositor.CreateCubicBezierEasingFunction({.34f, 0.f}, {.72f, .54f});
    auto shift = [&](PCWSTR property, float2 center) {
        auto animation = compositor.CreateVector2KeyFrameAnimation();
        animation.InsertKeyFrame(0.f, center);
        animation.InsertKeyFrame(.25f, {center.x + driftDistance, center.y - .11f}, easeOut);
        animation.InsertKeyFrame(.75f, {center.x - driftDistance, center.y + .11f}, easeInOut);
        animation.InsertKeyFrame(1.f, center, easeIn);
        animation.Duration(driftDuration);
        animation.IterationBehavior(comp::AnimationIterationBehavior::Forever);
        brush.StartAnimation(property, animation);
    };
    shift(L"StartPoint", startPoint);
    shift(L"EndPoint", endPoint);
}

inline void Apply(Surface& surface, glass::Palette const& palette, bool motion) {
    if (surface.painted && surface.previous == palette && surface.motion == motion) return;
    auto const& colors = palette.colors;
    std::array<Color, 5> stops{
        colors[0], glass::Blend(colors[0], colors[2], .45f), colors[2],
        glass::Blend(colors[2], colors[1], .55f), colors[1]
    };
    const Color shade{255, 23, 23, 33};
    const BYTE alpha = !palette.active ? 0 : surface.capsule == Capsule::Apps
        ? (palette.playing ? 132 : 107) : (palette.playing ? 119 : 96);
    auto gradients = surface.brush.ColorStops();
    for (unsigned index = 0; index < stops.size(); index++) {
        auto color = glass::Blend(stops[index], shade, .16f);
        if (surface.opaque) color = glass::Blend(surface.fallback, color, alpha / 255.f);
        glass::SetStop(gradients.GetAt(index), color, surface.opaque ? 255 : alpha, surface.painted && motion);
    }

    bool moving = palette.active && palette.playing && motion;
    if (moving != surface.moving || motion != surface.motion) SetDrift(surface, moving, motion);
    surface.previous = palette;
    surface.painted = true;
    surface.moving = moving;
    surface.motion = motion;
}

inline std::shared_ptr<Surface> Create(comp::Compositor const& compositor, Capsule capsule,
                                       Color fallback, bool opaque) {
    auto surface = std::make_shared<Surface>();
    surface->brush = compositor.CreateLinearGradientBrush();
    surface->brush.StartPoint(startPoint);
    surface->brush.EndPoint(endPoint);
    surface->capsule = capsule;
    surface->fallback = fallback;
    surface->opaque = opaque;
    for (unsigned i = 0; i < 5; i++)
        surface->brush.ColorStops().Append(compositor.CreateColorGradientStop(i * .25f, {}));
    auto palette = glass::ReadPalette(glass::HostPath(L"animation-state.ini").c_str());
    Apply(*surface, palette, winrt::Windows::UI::ViewManagement::UISettings().AnimationsEnabled());
    surfaces.emplace_back(surface);
    return surface;
}

inline void ForEach(auto&& action) {
    std::erase_if(surfaces, [](auto const& entry) { return entry.expired(); });
    for (auto const& entry : surfaces)
        if (auto surface = entry.lock()) action(*surface);
}
} // namespace DinamikAdaDockPalette
