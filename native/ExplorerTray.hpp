// SPDX-License-Identifier: GPL-3.0-or-later
#pragma once

#include <winrt/Windows.UI.Xaml.Automation.h>
#include <winrt/Windows.UI.Xaml.Controls.h>
#include <winrt/Windows.UI.Xaml.Controls.Primitives.h>

namespace DinamikAdaTray {
namespace xaml = winrt::Windows::UI::Xaml;
namespace controls = xaml::Controls;
namespace media = xaml::Media;
namespace comp = winrt::Windows::UI::Composition;
using winrt::Windows::Foundation::Rect;
using winrt::Windows::Foundation::IInspectable;

inline constexpr double miniWidth = 40;
inline constexpr ULONGLONG durationMs = 168;

struct Child {
    winrt::weak_ref<xaml::UIElement> element;
    IInspectable opacity{nullptr}, hitTest{nullptr};
};

struct Tray {
    winrt::weak_ref<controls::Panel> panel;
    controls::Button toggle{nullptr};
    controls::TextBlock glyph{nullptr};
    comp::Visual visual{nullptr};
    comp::CompositionClip originalClip{nullptr}, clip{nullptr};
    comp::CompositionRoundedRectangleGeometry shape{nullptr};
    std::vector<Child> hidden;
    winrt::event_token clicked{}, sizeChanged{};
    ULONGLONG startTime = 0;
    double amount = 0, from = 0;
    bool collapsed = false;
};

enum class IconKind { Other, Microphone, Volume };

struct Icon {
    winrt::weak_ref<controls::TextBlock> text;
    winrt::weak_ref<xaml::FrameworkElement> view;
    IInspectable visibility{nullptr};
    int64_t changed = 0;
    bool hidden = false;
    IconKind kind = IconKind::Other;
};

inline thread_local std::unordered_map<uint64_t, Tray> trays;
inline thread_local std::unordered_map<uint64_t, Icon> icons;
inline thread_local xaml::DispatcherTimer timer{nullptr};
inline thread_local winrt::event_token tick{};
inline thread_local unsigned changes = 0, errors = 0;
inline void Report();

inline void RestoreProperty(xaml::DependencyObject const& element, xaml::DependencyProperty const& property,
                            IInspectable const& value) {
    if (value == xaml::DependencyProperty::UnsetValue()) element.ClearValue(property);
    else element.SetValue(property, value);
}

inline IconKind Classify(winrt::hstring const& glyph) {
    if (glyph.size() != 1) return IconKind::Other;
    if (glyph[0] == L'\uE361' || glyph[0] == L'\uE720' || glyph[0] == L'\uEC71') return IconKind::Microphone;
    if (glyph[0] == L'\uE74F' || glyph[0] == L'\uEA85' || glyph[0] == L'\uEBC5' ||
        (glyph[0] >= L'\uE992' && glyph[0] <= L'\uE995')) return IconKind::Volume;
    return IconKind::Other;
}

inline void UpdateIcon(Icon& icon) {
    auto text = icon.text.get();
    auto view = icon.view.get();
    if (!text || !view) return;
    auto kind = Classify(text.Text());
    bool hide = kind != IconKind::Other;
    if (hide && !icon.hidden) {
        icon.visibility = view.ReadLocalValue(xaml::UIElement::VisibilityProperty());
        icon.hidden = true;
        view.Visibility(xaml::Visibility::Collapsed);
    } else if (!hide && icon.hidden) {
        icon.hidden = false;
        RestoreProperty(view, xaml::UIElement::VisibilityProperty(), icon.visibility);
    }
    icon.kind = kind;
}

inline void AttachIcon(uint64_t id, controls::TextBlock const& text) {
    xaml::FrameworkElement view{nullptr};
    auto ancestor = media::VisualTreeHelper::GetParent(text);
    for (int depth = 0; ancestor && depth < 24; depth++, ancestor = media::VisualTreeHelper::GetParent(ancestor)) {
        auto type = winrt::get_class_name(ancestor);
        if (!view && (type == L"SystemTray.IconView" || type == L"SystemTray.NotifyIconView"))
            view = ancestor.try_as<xaml::FrameworkElement>();
        auto parent = ancestor.try_as<xaml::FrameworkElement>();
        if (parent && (parent.Name() == L"ControlCenterButton" || parent.Name() == L"MainStack")) {
            if (!view) return;
            auto [it, added] = icons.try_emplace(id);
            if (!added) return;
            auto& icon = it->second;
            icon.text = winrt::make_weak(text);
            icon.view = winrt::make_weak(view);
            icon.changed = text.RegisterPropertyChangedCallback(controls::TextBlock::TextProperty(), [id](auto&&, auto&&) noexcept {
                try { if (auto found = icons.find(id); found != icons.end()) { UpdateIcon(found->second); Report(); } }
                catch (...) { errors++; Report(); }
            });
            UpdateIcon(icon);
            return;
        }
    }
}

inline void RestoreChildren(Tray& tray) {
    auto hidden = std::move(tray.hidden);
    for (auto const& child : hidden) {
        if (auto element = child.element.get()) {
            RestoreProperty(element, xaml::UIElement::OpacityProperty(), child.opacity);
            RestoreProperty(element, xaml::UIElement::IsHitTestVisibleProperty(), child.hitTest);
        }
    }
}

inline void HideChildren(Tray& tray, controls::Panel const& panel) {
    if (!tray.hidden.empty()) return;
    for (auto const& child : panel.Children()) {
        if (child == tray.toggle) continue;
        tray.hidden.push_back({winrt::make_weak(child), child.ReadLocalValue(xaml::UIElement::OpacityProperty()),
            child.ReadLocalValue(xaml::UIElement::IsHitTestVisibleProperty())});
        child.Opacity(0);
        child.IsHitTestVisible(false);
    }
}

inline double VisibleWidth(double width, double amount) {
    return width - std::max(0.0, width - miniWidth) * amount;
}

inline void Paint(Tray& tray) {
    auto panel = tray.panel.get();
    if (!panel || !panel.XamlRoot() || panel.ActualWidth() <= 0 || panel.ActualHeight() <= 0) return;
    if (tray.amount > 0) {
        float width = float(VisibleWidth(panel.ActualWidth(), tray.amount));
        tray.shape.Offset({float(panel.ActualWidth()) - width, 0});
        tray.shape.Size({width, float(panel.ActualHeight())});
        tray.shape.CornerRadius({14, 14});
        tray.visual.Clip(tray.clip);
    } else {
        tray.visual.Clip(tray.originalClip);
    }

    HWND island = ResolveClickThroughIslandHwnd(panel.XamlRoot());
    if (island) ReapplyClickThroughForTopLevel(GetAncestor(island, GA_ROOT));
}

inline std::optional<Rect> ClipBounds(xaml::FrameworkElement const& frame, xaml::UIElement const& content) {
    for (auto const& [id, tray] : trays) {
        if (tray.amount <= 0) continue;
        auto panel = tray.panel.get();
        if (!panel || panel.ActualWidth() <= 0 || panel.ActualHeight() <= 0) continue;
        auto ancestor = media::VisualTreeHelper::GetParent(panel);
        for (int depth = 0; ancestor && depth < 8; depth++, ancestor = media::VisualTreeHelper::GetParent(ancestor)) {
            if (ancestor != frame) continue;
            float width = float(VisibleWidth(panel.ActualWidth(), tray.amount));
            return panel.TransformToVisual(content).TransformBounds(
                {float(panel.ActualWidth()) - width, 0, width, float(panel.ActualHeight())});
        }
    }
    return std::nullopt;
}

inline void Finish(Tray& tray) {
    tray.startTime = 0;
    tray.amount = tray.collapsed ? 1 : 0;
    if (tray.collapsed) {
        if (auto panel = tray.panel.get()) HideChildren(tray, panel);
    }
    Paint(tray);
}

inline void Tick() noexcept {
    try {
        bool active = false, finished = false;
        ULONGLONG now = GetTickCount64();
        for (auto& [id, tray] : trays) {
            if (!tray.startTime) continue;
            double t = std::min(1.0, double(now - tray.startTime) / durationMs);
            if (t == 1) {
                Finish(tray);
                finished = true;
            } else {
                double eased = t * t * (3.0 - 2.0 * t);
                tray.amount = tray.from + ((tray.collapsed ? 1 : 0) - tray.from) * eased;
                Paint(tray);
                active = true;
            }
        }
        if (!active && timer) timer.Stop();
        if (finished) Report();
    } catch (...) { errors++; if (timer) timer.Stop(); Report(); }
}

inline void Toggle(Tray& tray) {
    tray.collapsed = !tray.collapsed;
    changes++;
    tray.glyph.Text(tray.collapsed ? L"\uE76B" : L"\uE76C");
    auto label = tray.collapsed ? L"Sistem tepsisini aç" : L"Sistem tepsisini küçült";
    xaml::Automation::AutomationProperties::SetName(tray.toggle, label);
    controls::ToolTipService::SetToolTip(tray.toggle, winrt::box_value(winrt::hstring(label)));
    if (!tray.collapsed) RestoreChildren(tray);
    if (!winrt::Windows::UI::ViewManagement::UISettings().AnimationsEnabled()) Finish(tray);
    else {
        tray.from = tray.amount;
        tray.startTime = GetTickCount64();
        if (!timer) {
            timer = xaml::DispatcherTimer();
            timer.Interval(std::chrono::milliseconds(12));
            tick = timer.Tick([](auto&&, auto&&) { Tick(); });
        }
        timer.Start();
    }
    Report();
}

inline void Install(uint64_t id) {
    auto it = trays.find(id);
    if (it == trays.end() || it->second.toggle) return;
    auto& tray = it->second;
    auto panel = tray.panel.get();
    if (!panel) return;
    tray.toggle = controls::Button();
    tray.toggle.Name(L"DinamikAdaTrayToggle");
    xaml::Automation::AutomationProperties::SetAutomationId(tray.toggle, L"DinamikAdaTrayToggle");
    xaml::Automation::AutomationProperties::SetName(tray.toggle, L"Sistem tepsisini küçült");
    controls::ToolTipService::SetToolTip(tray.toggle, winrt::box_value(winrt::hstring(L"Sistem tepsisini küçült")));
    tray.toggle.Width(27);
    tray.toggle.Height(28);
    tray.toggle.MinWidth(0);
    tray.toggle.MinHeight(0);
    tray.toggle.Padding({0, 0, 0, 0});
    tray.toggle.BorderThickness({0, 0, 0, 0});
    tray.toggle.CornerRadius({9, 9, 9, 9});
    tray.toggle.Background(media::SolidColorBrush({20, 255, 255, 255}));
    controls::Canvas::SetZIndex(tray.toggle, 1);
    tray.toggle.HorizontalAlignment(xaml::HorizontalAlignment::Right);
    tray.toggle.VerticalAlignment(xaml::VerticalAlignment::Center);
    if (auto grid = panel.try_as<controls::Grid>()) {
        controls::Grid::SetColumnSpan(tray.toggle, std::max(1u, grid.ColumnDefinitions().Size()));
        controls::Grid::SetRowSpan(tray.toggle, std::max(1u, grid.RowDefinitions().Size()));
        tray.toggle.Margin({0, 0, -28, 0});
    }
    tray.glyph = controls::TextBlock();
    tray.glyph.FontFamily(media::FontFamily(L"Segoe Fluent Icons"));
    tray.glyph.FontSize(11);
    tray.glyph.Text(L"\uE76C");
    tray.toggle.Content(tray.glyph);
    tray.visual = xaml::Hosting::ElementCompositionPreview::GetElementVisual(panel);
    tray.originalClip = tray.visual.Clip();
    tray.shape = tray.visual.Compositor().CreateRoundedRectangleGeometry();
    tray.clip = tray.visual.Compositor().CreateGeometricClip(tray.shape);
    tray.clicked = tray.toggle.Click([id](auto&&, auto&&) noexcept {
        try { if (auto found = trays.find(id); found != trays.end()) Toggle(found->second); }
        catch (...) { errors++; Report(); }
    });
    tray.sizeChanged = panel.SizeChanged([id](auto&&, auto&&) noexcept {
        try { if (auto found = trays.find(id); found != trays.end() && found->second.amount > 0) Paint(found->second); }
        catch (...) { errors++; Report(); }
    });
    panel.Children().Append(tray.toggle);
    Report();
}

inline void Attach(uint64_t id, xaml::FrameworkElement const& element) noexcept {
    try {
        if (element.Name() == L"InnerTextBlock") {
            if (auto text = element.try_as<controls::TextBlock>()) AttachIcon(id, text);
        }
        if (element.Name() != L"SystemTrayFrameGrid") return;
        auto panel = element.try_as<controls::Panel>();
        if (!panel || !trays.try_emplace(id).second) return;
        trays.at(id).panel = winrt::make_weak(panel);
        element.Dispatcher().RunAsync(winrt::Windows::UI::Core::CoreDispatcherPriority::Normal, [id] {
            try { Install(id); } catch (...) { errors++; Report(); }
        });
    } catch (...) { errors++; Report(); }
}

inline void Detach(uint64_t id) noexcept {
    try {
        if (auto it = icons.find(id); it != icons.end()) {
            auto icon = std::move(it->second);
            icons.erase(it);
            if (auto text = icon.text.get()) text.UnregisterPropertyChangedCallback(controls::TextBlock::TextProperty(), icon.changed);
            if (auto view = icon.view.get(); view && icon.hidden)
                RestoreProperty(view, xaml::UIElement::VisibilityProperty(), icon.visibility);
        }
        if (auto it = trays.find(id); it != trays.end()) {
            auto tray = std::move(it->second);
            trays.erase(it);
            RestoreChildren(tray);
            if (tray.visual) tray.visual.Clip(tray.originalClip);
            if (tray.toggle) {
                tray.toggle.Click(tray.clicked);
                if (auto panel = tray.panel.get()) {
                    panel.SizeChanged(tray.sizeChanged);
                    uint32_t index;
                    auto children = panel.Children();
                    if (children.IndexOf(tray.toggle, index)) children.RemoveAt(index);
                }
            }
        }
    } catch (...) { errors++; }
}

inline std::wstring Status() {
    unsigned microphones = 0, volumes = 0, installed = 0, compact = 0;
    for (auto const& [id, icon] : icons) {
        if (!icon.hidden) continue;
        if (icon.kind == IconKind::Microphone) microphones++;
        if (icon.kind == IconKind::Volume) volumes++;
    }
    for (auto const& [id, tray] : trays) {
        if (tray.toggle) installed++;
        if (tray.amount == 1) compact++;
    }
    return L"\nTrayToggles=" + std::to_wstring(installed) +
        L"\nTrayCompact=" + std::to_wstring(compact) +
        L"\nTrayToggleEvents=" + std::to_wstring(changes) +
        L"\nTrayToggleErrors=" + std::to_wstring(errors) +
        L"\nTrayMicrophonesHidden=" + std::to_wstring(microphones) +
        L"\nTrayVolumesHidden=" + std::to_wstring(volumes) + L"\n";
}

inline void Clear() {
    if (timer) {
        timer.Stop();
        timer.Tick(tick);
        timer = nullptr;
    }
    while (!trays.empty()) Detach(trays.begin()->first);
    while (!icons.empty()) Detach(icons.begin()->first);
}
} // namespace DinamikAdaTray
