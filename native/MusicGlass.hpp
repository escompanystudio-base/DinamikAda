// SPDX-License-Identifier: GPL-3.0-or-later
#pragma once

#include <array>
#include <chrono>
#include <winrt/Windows.UI.Composition.h>
#include <winrt/Windows.UI.Xaml.Hosting.h>

namespace DinamikAdaMusicGlass {
namespace comp = winrt::Windows::UI::Composition;
namespace xaml = winrt::Windows::UI::Xaml;
using winrt::Windows::Foundation::Numerics::float2;
using winrt::Windows::UI::Color;
using namespace std::chrono_literals;

inline constexpr std::array<float2, 3> centers{{{.25f, .55f}, {.66f, .50f}, {.49f, .24f}}};

struct Palette {
    bool active = false, playing = false;
    std::array<Color, 3> colors{{{255, 10, 132, 255}, {255, 94, 92, 230}, {255, 100, 210, 255}}};
    bool operator==(Palette const&) const = default;
};

inline std::wstring HostPath(PCWSTR name) {
    WCHAR root[32768];
    ExpandEnvironmentStringsW(L"%LOCALAPPDATA%\\DinamikAda\\ExplorerHost\\", root, ARRAYSIZE(root));
    return std::wstring(root) + name;
}

inline Color ReadColor(PCWSTR key, Color fallback, PCWSTR path) {
    WCHAR text[16]{};
    GetPrivateProfileStringW(L"Animation", key, L"", text, ARRAYSIZE(text), path);
    unsigned rgb;
    if (wcslen(text) != 7 || text[0] != L'#' || swscanf_s(text + 1, L"%x", &rgb) != 1) return fallback;
    return {255, BYTE(rgb >> 16), BYTE(rgb >> 8), BYTE(rgb)};
}

inline Color Blend(Color a, Color b, float amount) {
    auto mix = [amount](BYTE x, BYTE y) { return BYTE(x + (y - x) * amount + .5f); };
    return {255, mix(a.R, b.R), mix(a.G, b.G), mix(a.B, b.B)};
}

inline Palette ReadPalette(PCWSTR path) {
    Palette palette;
    palette.playing = GetPrivateProfileIntW(L"Animation", L"Playing", 0, path) != 0;
    palette.active = GetPrivateProfileIntW(L"Animation", L"Glow", 1, path) != 0 &&
        GetPrivateProfileIntW(L"Animation", L"HasSession", palette.playing, path) != 0;
    palette.colors[0] = ReadColor(L"Primary", palette.colors[0], path);
    palette.colors[1] = Blend(ReadColor(L"Secondary", palette.colors[1], path), palette.colors[0], .18f);
    palette.colors[2] = Blend(ReadColor(L"Highlight", palette.colors[2], path), palette.colors[0], .15f);
    return palette;
}

struct Surface {
    winrt::weak_ref<xaml::FrameworkElement> element;
    comp::ContainerVisual root{nullptr};
    std::array<comp::CompositionRadialGradientBrush, 3> orbs{nullptr, nullptr, nullptr};
    std::array<comp::CompositionColorGradientStop, 3> wash{nullptr, nullptr, nullptr};
    Palette previous;
    bool painted = false, motion = false, orbiting = false;
};

inline Surface Create(xaml::FrameworkElement const& element, float cornerRadius, bool menu = false) {
    auto host = xaml::Hosting::ElementCompositionPreview::GetElementVisual(element);
    auto compositor = host.Compositor();
    Surface surface;
    surface.element = winrt::make_weak(element);
    surface.root = compositor.CreateContainerVisual();
    surface.root.RelativeSizeAdjustment({1, 1});
    surface.root.Opacity(0);

    auto geometry = compositor.CreateRoundedRectangleGeometry();
    geometry.CornerRadius({cornerRadius, cornerRadius});
    auto size = compositor.CreateExpressionAnimation(L"host.Size");
    size.SetReferenceParameter(L"host", host);
    geometry.StartAnimation(L"Size", size);
    surface.root.Clip(compositor.CreateGeometricClip(geometry));

    auto washBrush = compositor.CreateLinearGradientBrush();
    washBrush.StartPoint({0, 0});
    washBrush.EndPoint({1, .85f});
    auto underlay = compositor.CreateSpriteVisual();
    underlay.RelativeSizeAdjustment({1, 1});
    underlay.Brush(washBrush);
    surface.root.Children().InsertAtBottom(underlay);
    for (unsigned i = 0; i < surface.orbs.size(); i++) {
        surface.wash[i] = compositor.CreateColorGradientStop(i * .5f, {0, 0, 0, 0});
        washBrush.ColorStops().Append(surface.wash[i]);
        auto brush = compositor.CreateRadialGradientBrush();
        brush.EllipseRadius(menu ? float2{.64f, .67f} : float2{.59f, 1.12f});
        brush.EllipseCenter(centers[i]);
        brush.ColorStops().Append(compositor.CreateColorGradientStop(0, {0, 0, 0, 0}));
        brush.ColorStops().Append(compositor.CreateColorGradientStop(.52f, {0, 0, 0, 0}));
        brush.ColorStops().Append(compositor.CreateColorGradientStop(1, {0, 0, 0, 0}));
        auto sprite = compositor.CreateSpriteVisual();
        sprite.RelativeSizeAdjustment({1, 1});
        sprite.Brush(brush);
        surface.root.Children().InsertAtTop(sprite);
        surface.orbs[i] = brush;
    }
    return surface;
}

inline void SetStop(comp::CompositionColorGradientStop const& stop, Color color, BYTE alpha, bool animate) {
    color.A = alpha;
    if (!animate) {
        stop.StopAnimation(L"Color");
        stop.Color(color);
        return;
    }
    auto animation = stop.Compositor().CreateColorKeyFrameAnimation();
    animation.InterpolationColorSpace(comp::CompositionColorSpace::Rgb);
    animation.InsertExpressionKeyFrame(0.f, L"this.StartingValue");
    animation.InsertKeyFrame(1.f, color);
    animation.Duration(440ms);
    stop.StartAnimation(L"Color", animation);
}

inline void Apply(Surface& surface, Palette const& palette, bool motion) {
    if (surface.painted && surface.previous == palette && surface.motion == motion) return;
    auto compositor = surface.root.Compositor();
    bool repaint = !surface.painted || surface.previous.colors != palette.colors || (surface.motion && !motion);
    if (repaint) {
        constexpr BYTE washAlpha[]{46, 36, 30};
        constexpr BYTE innerAlpha[]{90, 80, 68};
        constexpr BYTE midAlpha[]{46, 40, 34};
        for (unsigned i = 0; i < surface.orbs.size(); i++) {
            bool crossfade = surface.painted && motion;
            auto const& color = palette.colors[i];
            auto stops = surface.orbs[i].ColorStops();
            SetStop(surface.wash[i], color, washAlpha[i], crossfade);
            SetStop(stops.GetAt(0), color, innerAlpha[i], crossfade);
            SetStop(stops.GetAt(1), color, midAlpha[i], crossfade);
            SetStop(stops.GetAt(2), color, 0, crossfade);
        }
    }
    float opacity = palette.active ? (palette.playing ? .76f : .33f) : 0.f;
    if (!surface.painted || !motion) {
        surface.root.StopAnimation(L"Opacity");
        surface.root.Opacity(opacity);
    } else if (surface.previous.playing != palette.playing || surface.previous.active != palette.active) {
        auto fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertExpressionKeyFrame(0.f, L"this.StartingValue");
        fade.InsertKeyFrame(1.f, opacity);
        fade.Duration(350ms);
        surface.root.StartAnimation(L"Opacity", fade);
    }

    bool orbit = palette.active && palette.playing && motion;
    if (orbit != surface.orbiting) {
        for (unsigned i = 0; i < surface.orbs.size(); i++) {
            auto const& brush = surface.orbs[i];
            if (!orbit) {
                brush.StopAnimation(L"EllipseCenter");
                if (!motion) brush.EllipseCenter(centers[i]);
                continue;
            }
            float2 center = centers[i];
            auto path = compositor.CreateVector2KeyFrameAnimation();
            path.InsertKeyFrame(0.f, center);
            path.InsertKeyFrame(.33f, {center.x + .115f, center.y + .14f});
            path.InsertKeyFrame(.67f, {center.x - .10f, center.y - .08f});
            path.InsertKeyFrame(1.f, center);
            path.Duration(12s + 3s * i);
            path.IterationBehavior(comp::AnimationIterationBehavior::Forever);
            brush.StartAnimation(L"EllipseCenter", path);
        }
    }
    surface.previous = palette;
    surface.painted = true;
    surface.motion = motion;
    surface.orbiting = orbit;
}

inline void Restore(Surface const& surface) {
    if (auto element = surface.element.get())
        xaml::Hosting::ElementCompositionPreview::SetElementChildVisual(element, nullptr);
}
} // namespace DinamikAdaMusicGlass
