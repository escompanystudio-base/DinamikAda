using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DinamikAda.App.Services.DockWidgets;
using DinamikAda.App.Views.DockWidgets;
using DinamikAda.Core.Config;
using DinamikAda.Core.Localization;
using DinamikAda.Core.Widgets;

namespace DinamikAda.App.Views;

/// <summary>Dock widgets: live tiles between the apps and the island slot, their cards and the picker.</summary>
public partial class DockWindow
{
    private DockWidgetDataHub? _widgetHub;
    private DockWidgetContext? _widgetCtx;
    private readonly List<(IDockWidgetTile Tile, DockSlotVisual Slot)> _widgetTiles = new();
    private readonly HashSet<string> _pendingPopInWidgets = new(StringComparer.OrdinalIgnoreCase);
    private DockWidgetPickerWindow? _widgetPicker;
    private FrameworkElement? _widgetCardAnchor;
    private bool _widgetEditMode;

    private DockWidgetDataHub WidgetHub => _widgetHub ??= CreateWidgetHub();

    private DockWidgetDataHub CreateWidgetHub()
    {
        var hub = new DockWidgetDataHub(_orch);
        hub.Changed += (_, _) => UpdateWidgetTiles();
        return hub;
    }

    private DockWidgetContext WidgetContext
    {
        get
        {
            var accent = ParseAccentColor(_orch.Media.Current.AccentHex, Color.FromRgb(10, 132, 255));
            if (_widgetCtx is null)
            {
                _widgetCtx = new DockWidgetContext
                {
                    Orch = _orch,
                    Data = WidgetHub,
                    Palette = CreateWidgetPalette(accent),
                    ShowCard = ShowWidgetCard,
                    CloseCard = CloseWidgetCard,
                    IsCardOpenFor = anchor => WidgetCardPopup.IsOpen && ReferenceEquals(_widgetCardAnchor, anchor),
                    RequestRebuild = () => Dispatcher.BeginInvoke(DispatcherPriority.Background, () => RebuildVisualSlots(force: true))
                };
            }
            _widgetCtx.IsAuditMode = _isSmokeOrAuditMode;
            _widgetCtx.TileHeight = Math.Clamp(_orch.Config.DockIconSizeDip, 26, 48);
            _widgetCtx.Now = DateTime.Now;
            return _widgetCtx;
        }
    }

    private DockWidgetPalette CreateWidgetPalette(Color accent)
    {
        var p = DockWidgetPalette.Create(_orch.Config.DockStyle, accent);
        return new DockWidgetPalette
        {
            Style = p.Style,
            IsLight = p.IsLight,
            PillBackground = p.PillBackground,
            PillBorderColor = p.PillBorderColor,
            TileBackground = p.TileBackground,
            TileHoverBackground = p.TileHoverBackground,
            TileBorder = p.TileBorder,
            Foreground = p.Foreground,
            SecondaryForeground = p.SecondaryForeground,
            TertiaryForeground = p.TertiaryForeground,
            Track = p.Track,
            CardBackground = p.CardBackground,
            CardBorder = p.CardBorder,
            CardRowBackground = p.CardRowBackground,
            Accent = p.Accent,
            Positive = p.Positive,
            Negative = p.Negative,
            Warning = p.Warning,
            UiFont = (FontFamily)FindResource("UiFont"),
            TextFont = (FontFamily)FindResource("UiTextFont"),
            IconFont = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets, Segoe UI Symbol")
        };
    }

    /// <summary>Appended to the dock structure key so style, widget or edit-mode changes rebuild the slots.</summary>
    private string WidgetStructureKey() =>
        $"W:{string.Join(',', _orch.Config.DockWidgets)}|S:{_orch.Config.DockStyle}|E:{_widgetEditMode}|T:{IsDockAtTop}";

    /// <summary>Builds the widget slots in config order; returns how many were placed.</summary>
    private int AddWidgetSlots(int iconSize)
    {
        DetachWidgetTiles();
        var ids = _orch.Config.DockWidgets;
        if (ids.Count == 0) return 0;

        var ctx = WidgetContext;
        ctx.Palette = CreateWidgetPalette(ctx.Palette.Accent);
        int placed = 0;
        foreach (var id in ids)
        {
            var tile = DockWidgetTileFactory.Create(id, ctx);
            if (tile is null) continue;
            AddWidgetSlot(tile, iconSize, ctx, placed);
            placed++;
        }
        UpdateWidgetHubDemand();
        return placed;
    }

    private void AddWidgetSlot(IDockWidgetTile tile, int iconSize, DockWidgetContext ctx, int index)
    {
        var root = tile.Root;
        double tileW = Math.Max(root.Width, iconSize);
        var scale = new ScaleTransform(1.0, 1.0, tileW * 0.5, IsDockAtTop ? 0 : iconSize);
        var translate = new TranslateTransform(0.0, 0.0);
        var tg = new TransformGroup();
        tg.Children.Add(scale);
        tg.Children.Add(translate);

        var container = new Grid
        {
            Width = tileW,
            Height = iconSize + 9.0,
            Background = Brushes.Transparent,
            RenderTransform = tg,
            Margin = new Thickness(index == 0 ? 0 : 3, 0, 3, 0),
            Tag = tile.Id
        };
        root.VerticalAlignment = IsDockAtTop ? VerticalAlignment.Bottom : VerticalAlignment.Top;
        root.HorizontalAlignment = HorizontalAlignment.Center;
        container.Children.Add(root);

        var slot = new DockSlotVisual
        {
            Container = container,
            Scale = scale,
            Translate = translate,
            IconPlate = new Border(),
            RunningDot = null,
            RunningPill = null,
            TooltipLabel = tile.Descriptor.Name(ctx.Lang),
            Kind = SlotKind.Widget,
            WidgetId = tile.Id
        };

        if (_widgetEditMode) container.Children.Add(CreateWidgetRemoveBadge(tile.Id));

        container.MouseLeftButtonDown += (_, ev) =>
        {
            _pressedPinnedSlot = slot;
            _pressStartPoint = ev.GetPosition(DockItemsHost);
        };
        container.LostMouseCapture += (_, _) =>
        {
            if (_isReorderDragging && ReferenceEquals(_pressedPinnedSlot, slot)) EndReorderDrag(commit: false);
        };
        container.MouseLeftButtonUp += (_, ev) =>
        {
            ev.Handled = true;
            if (_isReorderDragging)
            {
                EndReorderDrag(commit: true);
                return;
            }
            _pressedPinnedSlot = null;
            HandleWidgetClick(tile);
        };
        container.MouseRightButtonUp += (_, ev) =>
        {
            ev.Handled = true;
            HandleSlotContextMenu(slot);
        };

        _slots.Add(slot);
        _widgetTiles.Add((tile, slot));
        DockItemsHost.Children.Add(container);

        tile.Update(ctx);
        tile.OnAttached(ctx);
        if (_pendingPopInWidgets.Remove(tile.Id) && !_isSmokeOrAuditMode) W.PopIn(root);
    }

    private Border CreateWidgetRemoveBadge(string widgetId)
    {
        var badge = new Border
        {
            Width = 16,
            Height = 16,
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A)),
            BorderBrush = Brushes.White,
            BorderThickness = new Thickness(1.2),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = IsDockAtTop ? VerticalAlignment.Bottom : VerticalAlignment.Top,
            Margin = new Thickness(-5, IsDockAtTop ? 0 : -5, 0, IsDockAtTop ? -5 : 0),
            Cursor = Cursors.Hand,
            Child = new TextBlock
            {
                Text = "",
                FontFamily = (FontFamily)FindResource("IconFont"),
                FontSize = 8,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        System.Windows.Automation.AutomationProperties.SetName(badge, Strings.Pick(_orch.Config.Language, "Widget'ı kaldır", "Remove widget"));
        badge.MouseLeftButtonDown += (_, e) => e.Handled = true;
        badge.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            RemoveDockWidget(widgetId);
        };
        // Wiggle like an editable home screen while the picker is open.
        var rt = new RotateTransform();
        badge.RenderTransformOrigin = new Point(0.5, 0.5);
        badge.RenderTransform = rt;
        if (!_isSmokeOrAuditMode)
        {
            rt.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(-8, 8, TimeSpan.FromMilliseconds(260))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = Motion.Gentle
            });
        }
        return badge;
    }

    private void DetachWidgetTiles()
    {
        foreach (var (tile, _) in _widgetTiles)
        {
            try { tile.OnDetached(); } catch { }
        }
        _widgetTiles.Clear();
    }

    private void UpdateWidgetTiles()
    {
        if (_isClosed || _widgetTiles.Count == 0) return;
        if (!_isSmokeOrAuditMode && (Visibility != Visibility.Visible || _isFullscreenHidden)) return;
        var ctx = WidgetContext;
        foreach (var (tile, _) in _widgetTiles)
        {
            try
            {
                tile.Update(ctx);
            }
            catch (Exception ex)
            {
                Services.CrashLogger.Log("DockWidget", ex, $"Widget '{tile.Id}' update failed.");
            }
        }
    }

    private void UpdateWidgetHubDemand()
    {
        if (_isSmokeOrAuditMode) return;
        bool visible = _orch.Config.UseCustomBottomDock && !_isFullscreenHidden;
        WidgetHub.SetActiveWidgets(_widgetTiles.Select(t => t.Tile.Id), visible);
    }

    private void HandleWidgetClick(IDockWidgetTile tile)
    {
        var ctx = WidgetContext;
        if (WidgetCardPopup.IsOpen && ReferenceEquals(_widgetCardAnchor, tile.Root))
        {
            CloseWidgetCard();
            return;
        }

        var card = tile.BuildCard(ctx);
        if (card is not null)
        {
            ShowWidgetCard(tile.Root, card);
            return;
        }
        CloseWidgetCard();
        if (tile is DockWidgetTileBase b) b.OnPrimaryClick(ctx);
    }

    // ---- Detail card popup -------------------------------------------------------------------

    private void ShowWidgetCard(FrameworkElement anchor, FrameworkElement content)
    {
        if (_isClosed) return;
        StartLaunchPopup.IsOpen = false;
        TrayOverflowPopup.IsOpen = false;
        ClockCalendarPopup.IsOpen = false;

        var p = WidgetContext.Palette;
        WidgetCardBorder.Background = p.CardBackground;
        WidgetCardBorder.BorderBrush = p.CardBorder;
        WidgetCardHost.Content = content;
        bool reopen = WidgetCardPopup.IsOpen;
        _widgetCardAnchor = anchor;
        WidgetCardPopup.Placement = IsDockAtTop ? PlacementMode.Bottom : PlacementMode.Top;
        WidgetCardPopup.VerticalOffset = IsDockAtTop ? 18 : -18;
        WidgetCardPopup.PlacementTarget = anchor;
        bool wasClosing = _cardClosing;
        _cardClosing = false;
        if (reopen && wasClosing)
        {
            PlayCardOpenAnimation();
        }
        else if (reopen)
        {
            // Re-anchor an already open card without a flicker and let the new content fade in softly.
            WidgetCardPopup.HorizontalOffset += 0.01;
            WidgetCardPopup.HorizontalOffset -= 0.01;
            PlayCardContentSwap();
        }
        else
        {
            WidgetCardPopup.IsOpen = true;
            PlayCardOpenAnimation();
        }
    }

    private bool _cardClosing;

    private void PlayCardOpenAnimation()
    {
        if (_isSmokeOrAuditMode) return;
        double rise = IsDockAtTop ? -10 : 10;
        var st = new ScaleTransform(0.96, 0.96);
        var tt = new TranslateTransform(0, rise);
        var tg = new TransformGroup();
        tg.Children.Add(st);
        tg.Children.Add(tt);
        WidgetCardBorder.RenderTransformOrigin = new Point(0.5, IsDockAtTop ? 0.0 : 1.0);
        WidgetCardBorder.RenderTransform = tg;
        st.BeginAnimation(ScaleTransform.ScaleXProperty, Motion.FromTo(0.96, 1, Motion.EnterMs));
        st.BeginAnimation(ScaleTransform.ScaleYProperty, Motion.FromTo(0.96, 1, Motion.EnterMs));
        tt.BeginAnimation(TranslateTransform.YProperty, Motion.FromTo(rise, 0, Motion.EnterMs));
        WidgetCardBorder.BeginAnimation(OpacityProperty, Motion.FromTo(0, 1, Motion.EnterMs * 0.65));
    }

    /// <summary>Another tile's card replaces the open one: crossfade the content instead of re-opening.</summary>
    private void PlayCardContentSwap()
    {
        if (_isSmokeOrAuditMode) return;
        WidgetCardBorder.BeginAnimation(OpacityProperty, Motion.To(1, 200));
        var tt = new TranslateTransform(0, IsDockAtTop ? -4 : 4);
        WidgetCardHost.RenderTransform = tt;
        tt.BeginAnimation(TranslateTransform.YProperty, Motion.To(0, Motion.EnterMs));
        WidgetCardHost.BeginAnimation(OpacityProperty, Motion.FromTo(0, 1, Motion.EnterMs * 0.7));
    }

    private void CloseWidgetCard()
    {
        if (!WidgetCardPopup.IsOpen || _cardClosing) return;
        if (_isSmokeOrAuditMode || _isClosed)
        {
            WidgetCardPopup.IsOpen = false;
            return;
        }
        // Fade and sink softly, then close the popup.
        _cardClosing = true;
        double sink = IsDockAtTop ? -6 : 6;
        var st = new ScaleTransform(1, 1);
        var tt = new TranslateTransform(0, 0);
        var tg = new TransformGroup();
        tg.Children.Add(st);
        tg.Children.Add(tt);
        WidgetCardBorder.RenderTransform = tg;
        st.BeginAnimation(ScaleTransform.ScaleXProperty, Motion.To(0.97, Motion.ExitMs, Motion.Exit));
        st.BeginAnimation(ScaleTransform.ScaleYProperty, Motion.To(0.97, Motion.ExitMs, Motion.Exit));
        tt.BeginAnimation(TranslateTransform.YProperty, Motion.To(sink, Motion.ExitMs, Motion.Exit));
        var fade = Motion.To(0, Motion.ExitMs, Motion.Exit);
        fade.Completed += (_, _) =>
        {
            if (!_cardClosing) return; // reopened meanwhile
            _cardClosing = false;
            WidgetCardPopup.IsOpen = false;
            WidgetCardBorder.BeginAnimation(OpacityProperty, null);
            WidgetCardBorder.Opacity = 1;
        };
        WidgetCardBorder.BeginAnimation(OpacityProperty, fade);
    }

    private void OnWidgetCardOpened(object? sender, EventArgs e)
    {
        if (_isClosed || _isSmokeOrAuditMode) return;
        StartPopupLightDismissWatcher();
    }

    private void OnWidgetCardClosed(object? sender, EventArgs e)
    {
        _widgetCardAnchor = null;
        WidgetCardHost.Content = null;
        StopPopupLightDismissWatcherIfIdle();
        ScheduleIntellihideEval();
    }

    private void OnWidgetCardPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CloseWidgetCard();
        }
    }

    // ---- Adding, removing, moving ------------------------------------------------------------

    public void AddDockWidget(string id, int? insertIndex = null)
    {
        var d = DockWidgetCatalog.Find(id);
        if (d is null) return;
        var list = new List<string>(_orch.Config.DockWidgets);
        if (list.Contains(d.Id, StringComparer.OrdinalIgnoreCase)) return;
        if (list.Count >= DockWidgetCatalog.MaxWidgetsOnDock) return;
        int at = Math.Clamp(insertIndex ?? list.Count, 0, list.Count);
        list.Insert(at, d.Id);
        _pendingPopInWidgets.Add(d.Id);
        _orch.ConfigStore.Update(_orch.Config, c => c.DockWidgets = list);
        RebuildVisualSlots(force: true);
        _widgetPicker?.RefreshSelection();
    }

    public void RemoveDockWidget(string id)
    {
        var list = _orch.Config.DockWidgets.Where(x => !string.Equals(x, id, StringComparison.OrdinalIgnoreCase)).ToList();
        if (list.Count == _orch.Config.DockWidgets.Count) return;
        if (_widgetCardAnchor is not null && Equals(_widgetCardAnchor.Tag, id)) CloseWidgetCard();

        var entry = _widgetTiles.FirstOrDefault(t => string.Equals(t.Tile.Id, id, StringComparison.OrdinalIgnoreCase));
        void Commit()
        {
            _orch.ConfigStore.Update(_orch.Config, c => c.DockWidgets = list);
            RebuildVisualSlots(force: true);
            _widgetPicker?.RefreshSelection();
        }

        if (entry.Slot is null || _isSmokeOrAuditMode)
        {
            Commit();
            return;
        }

        // Shrink + fade the tile out, then rebuild without it.
        var root = entry.Tile.Root;
        var st = new ScaleTransform(1, 1);
        root.RenderTransformOrigin = new Point(0.5, 0.5);
        root.RenderTransform = st;
        var dur = TimeSpan.FromMilliseconds(180);
        var shrink = new DoubleAnimation(1, 0.4, dur) { EasingFunction = Motion.Exit };
        shrink.Completed += (_, _) => Commit();
        st.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
        st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, 0.4, dur));
        root.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, dur));
    }

    private bool MoveDockWidget(string id, int newIndex)
    {
        var list = new List<string>(_orch.Config.DockWidgets);
        int from = list.FindIndex(x => string.Equals(x, id, StringComparison.OrdinalIgnoreCase));
        if (from < 0) return false;
        newIndex = Math.Clamp(newIndex, 0, list.Count - 1);
        if (newIndex == from) return false;
        var item = list[from];
        list.RemoveAt(from);
        list.Insert(newIndex, item);
        _orch.ConfigStore.Update(_orch.Config, c => c.DockWidgets = list);
        return true;
    }

    // ---- Picker -------------------------------------------------------------------------------

    private void OnOpenWidgetPickerRequested(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(() =>
        {
            if (!_orch.Config.UseCustomBottomDock) return;
            OpenWidgetPicker();
        });

    public void OpenWidgetPicker()
    {
        if (_isClosed) return;
        if (_widgetPicker is { IsLoaded: true })
        {
            _widgetPicker.Activate();
            return;
        }

        CloseWidgetCard();
        StartLaunchPopup.IsOpen = false;
        TrayOverflowPopup.IsOpen = false;
        ClockCalendarPopup.IsOpen = false;

        _widgetPicker = new DockWidgetPickerWindow(
            _orch,
            WidgetHub,
            () => CreateWidgetPalette(ParseAccentColor(_orch.Media.Current.AccentHex, Color.FromRgb(10, 132, 255))),
            id => AddDockWidget(id),
            RemoveDockWidget);
        _widgetPicker.Closed += (_, _) =>
        {
            _widgetPicker = null;
            SetWidgetEditMode(false);
        };
        SetWidgetEditMode(true);
        PositionWidgetPicker(_widgetPicker);
        _widgetPicker.Show();
        _widgetPicker.Activate();
    }

    private void PositionWidgetPicker(Window picker)
    {
        double scale = Math.Max(1.0, _activeMonitor.DpiScale);
        double monLeft = _activeMonitor.LeftPx / scale;
        double monTop = _activeMonitor.TopPx / scale;
        double monW = _activeMonitor.WidthPx / scale;
        double monH = _activeMonitor.HeightPx / scale;
        picker.Width = Math.Min(860, monW - 48);
        picker.Height = Math.Min(620, monH - 200);
        picker.Left = monLeft + (monW - picker.Width) / 2;
        picker.Top = IsDockAtTop
            ? monTop + HostHeightDip + 24
            : monTop + monH - HostHeightDip - picker.Height - 24;
    }

    private void SetWidgetEditMode(bool enabled)
    {
        if (_widgetEditMode == enabled) return;
        _widgetEditMode = enabled;
        if (!_isClosed) RebuildVisualSlots(force: true);
    }

    // ---- Context menu additions -----------------------------------------------------------------

    private void AppendWidgetAndStyleMenuItems(ContextMenu menu, DockSlotVisual? slot)
    {
        string lang = _orch.Config.Language;
        if (slot is { Kind: SlotKind.Widget, WidgetId: { } wid })
        {
            var d = DockWidgetCatalog.Find(wid);
            var remove = new MenuItem { Header = Strings.Pick(lang, $"“{d?.Name(lang)}” widget'ını kaldır", $"Remove “{d?.Name(lang)}”") };
            remove.Click += (_, _) => RemoveDockWidget(wid);
            menu.Items.Add(remove);
        }

        var widgets = new MenuItem { Header = Strings.Pick(lang, "Widget'lar…", "Widgets…") };
        widgets.Click += (_, _) => OpenWidgetPicker();
        menu.Items.Add(widgets);

        var style = new MenuItem { Header = Strings.Pick(lang, "Dock stili", "Dock style") };
        foreach (DockVisualStyle s in Enum.GetValues<DockVisualStyle>())
        {
            var item = new MenuItem { Header = DockStyleName(s, lang), IsCheckable = true, IsChecked = _orch.Config.DockStyle == s };
            var captured = s;
            item.Click += (_, _) => ApplyDockStyle(captured);
            style.Items.Add(item);
        }
        menu.Items.Add(style);

        var edge = new MenuItem
        {
            Header = IsDockAtTop
                ? Strings.Pick(lang, "Dock'u alta taşı", "Move dock to the bottom")
                : Strings.Pick(lang, "Dock'u üste taşı", "Move dock to the top")
        };
        edge.Click += (_, _) => SetDockEdge(IsDockAtTop ? DockScreenEdge.Bottom : DockScreenEdge.Top);
        menu.Items.Add(edge);
    }

    public static string DockStyleName(DockVisualStyle s, string lang) => s switch
    {
        DockVisualStyle.Black => Strings.Pick(lang, "Siyah", "Black"),
        DockVisualStyle.White => Strings.Pick(lang, "Beyaz", "White"),
        DockVisualStyle.LiquidGlass => Strings.Pick(lang, "Sıvı Cam", "Liquid Glass"),
        DockVisualStyle.IosGlass => Strings.Pick(lang, "iOS Cam", "iOS Glass"),
        _ => Strings.Pick(lang, "Gece Yarısı", "Midnight")
    };

    private void ApplyDockStyle(DockVisualStyle style)
    {
        if (_orch.Config.DockStyle == style) return;
        _orch.ConfigStore.Update(_orch.Config, c => c.DockStyle = style);
        ApplyDockStyleVisuals();
        RebuildVisualSlots(force: true);
    }

    /// <summary>Applies the capsule background/border for the configured style.</summary>
    private void ApplyDockStyleVisuals()
    {
        var p = DockWidgetPalette.Create(_orch.Config.DockStyle, Color.FromRgb(10, 132, 255));
        DockPillContainer.Background = p.PillBackground;
        // The album-glow overlay only suits the dark styles.
        DockAmbientOverlay.Visibility = p.IsLight ? Visibility.Collapsed : Visibility.Visible;
    }
}

public partial class DockWindow
{
    private bool IsLightDock => _orch.Config.DockStyle == DockVisualStyle.White;

    /// <summary>Translucent tint for plates/hover on the capsule: white on dark styles, black on White.</summary>
    private SolidColorBrush DockTint(byte alpha) => IsLightDock
        ? new SolidColorBrush(Color.FromArgb((byte)Math.Min(255, alpha + 6), 0, 0, 0))
        : new SolidColorBrush(Color.FromArgb(alpha, 0xFF, 0xFF, 0xFF));

    /// <summary>Glyph colour for dock slots; light glyphs turn dark on the White style.</summary>
    private SolidColorBrush DockGlyph(Color onDark) => IsLightDock
        ? new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x30))
        : new SolidColorBrush(onDark);

    private Brush DockTextBrush(bool primary) => IsLightDock
        ? new SolidColorBrush(primary ? Color.FromRgb(0x1C, 0x1C, 0x1E) : Color.FromRgb(0x5A, 0x5A, 0x62))
        : (Brush)FindResource(primary ? "TextPrimaryBrush" : "TextSecondaryBrush");

    private Color DockNeutralBorderColor() => IsLightDock
        ? Color.FromArgb(0x30, 0, 0, 0)
        : Color.FromArgb(0x32, 0xFF, 0xFF, 0xFF);
}
