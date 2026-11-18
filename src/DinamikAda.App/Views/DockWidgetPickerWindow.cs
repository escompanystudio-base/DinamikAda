using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DinamikAda.App.Services;
using DinamikAda.App.Services.DockWidgets;
using DinamikAda.App.Views.DockWidgets;
using DinamikAda.Core.Widgets;

namespace DinamikAda.App.Views;

/// <summary>
/// Widget gallery: categories on the left, search on top, and live previews. Clicking a preview
/// adds the widget to the dock (or removes it when it is already there).
/// </summary>
public sealed class DockWidgetPickerWindow : Window
{
    private readonly AppOrchestrator _orch;
    private readonly DockWidgetDataHub _hub;
    private readonly Func<DockWidgetPalette> _paletteFactory;
    private readonly Action<string> _add;
    private readonly Action<string> _remove;
    private readonly DispatcherTimer _previewTimer;
    private readonly List<(IDockWidgetTile Tile, Border Card, Border Check)> _previews = new();
    private readonly StackPanel _categoryList = new();
    private readonly WrapPanel _grid = new() { Orientation = Orientation.Horizontal };
    private readonly TextBox _search;
    private readonly TextBlock _countText;
    private DockWidgetCategory? _category;
    private DockWidgetPalette _palette;
    private DockWidgetContext _previewCtx;

    private string Lang => _orch.Config.Language;
    private string T(string tr, string en) => Lang == "en" ? en : tr;

    public DockWidgetPickerWindow(AppOrchestrator orch, DockWidgetDataHub hub, Func<DockWidgetPalette> paletteFactory,
        Action<string> add, Action<string> remove)
    {
        _orch = orch;
        _hub = hub;
        _paletteFactory = paletteFactory;
        _add = add;
        _remove = remove;
        _palette = paletteFactory();
        _previewCtx = CreatePreviewContext();

        Title = T("Widget'lar", "Widgets");
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

        try
        {
            Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/DinamikAda.App;component/Themes/ObsidianGlassTheme.xaml", UriKind.Absolute)
            });
        }
        catch (Exception ex)
        {
            CrashLogger.Log("DockWidget", ex, "Picker theme could not be loaded.");
        }

        var p = _palette;
        _search = W.Input(p, T("Widget ara…", "Search widgets…"), 260);
        _search.TextChanged += (_, _) => RebuildGrid();
        _countText = W.Text(p, "", 12, p.SecondaryForeground, ui: false);

        Content = BuildLayout();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; Close(); }
        };
        Deactivated += (_, _) => { /* stays open until closed so the user can watch the dock change */ };

        _previewTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _previewTimer.Tick += (_, _) => UpdatePreviews();
        Loaded += (_, _) =>
        {
            RebuildCategories();
            RebuildGrid();
            _previewTimer.Start();
            PlayOpenAnimation();
            _search.Focus();
        };
        Closed += (_, _) =>
        {
            _previewTimer.Stop();
            DetachPreviews();
        };
    }

    private DockWidgetContext CreatePreviewContext() => new()
    {
        Orch = _orch,
        Data = _hub,
        Palette = _palette,
        ShowCard = (_, _) => { },
        CloseCard = () => { },
        IsCardOpenFor = _ => false,
        RequestRebuild = () => { },
        // Previews show sample data and never touch the OS.
        IsAuditMode = true,
        TileHeight = 34
    };

    private FrameworkElement BuildLayout()
    {
        var p = _palette;
        var root = new Border
        {
            CornerRadius = new CornerRadius(22),
            Background = p.CardBackground,
            BorderBrush = p.CardBorder,
            BorderThickness = new Thickness(1),
            Margin = new Thickness(14),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 28, ShadowDepth = 6, Opacity = 0.4 }
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // Left: categories
        var left = new Border
        {
            Background = p.CardRowBackground,
            CornerRadius = new CornerRadius(22, 0, 0, 22),
            Padding = new Thickness(12, 18, 12, 18)
        };
        var leftStack = new StackPanel();
        var title = W.Text(p, T("Widget'lar", "Widgets"), 20, weight: FontWeights.SemiBold);
        title.Margin = new Thickness(8, 0, 0, 4);
        leftStack.Children.Add(title);
        var sub = W.Text(p, T("Tıkla: ekle / kaldır · Dock'ta sürükle: sırala", "Click to add or remove · drag on the dock to reorder"), 11, p.TertiaryForeground, ui: false);
        sub.TextWrapping = TextWrapping.Wrap;
        sub.TextTrimming = TextTrimming.None;
        sub.Margin = new Thickness(8, 0, 0, 14);
        leftStack.Children.Add(sub);
        leftStack.Children.Add(_categoryList);
        left.Child = leftStack;
        grid.Children.Add(left);

        // Right: search + grid
        var right = new DockPanel { Margin = new Thickness(20, 18, 18, 18), LastChildFill = true };
        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };
        var close = W.IconButton(p, "", 30, Close, p.CardRowBackground);
        System.Windows.Automation.AutomationProperties.SetName(close, T("Kapat", "Close"));
        DockPanel.SetDock(close, Dock.Right);
        top.Children.Add(close);
        var placeholder = W.Text(p, T("Widget ara…", "Search widgets…"), 13, p.TertiaryForeground, ui: false);
        placeholder.Margin = new Thickness(10, 0, 0, 0);
        placeholder.IsHitTestVisible = false;
        _search.TextChanged += (_, _) => placeholder.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var searchHost = new Grid { Children = { _search, placeholder } };
        DockPanel.SetDock(searchHost, Dock.Left);
        top.Children.Add(searchHost);
        _countText.Margin = new Thickness(12, 0, 0, 0);
        top.Children.Add(_countText);
        DockPanel.SetDock(top, Dock.Top);
        right.Children.Add(top);

        var scroller = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _grid
        };
        if (TryFindResource("ObsidianScrollViewerStyle") is Style sv) scroller.Style = sv;
        right.Children.Add(scroller);
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);

        root.Child = grid;

        // Drag the window by its empty area.
        root.MouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource == root || e.OriginalSource == left || e.OriginalSource == grid)
            {
                try { DragMove(); } catch (InvalidOperationException) { }
            }
        };
        return root;
    }

    private void RebuildCategories()
    {
        _categoryList.Children.Clear();
        AddCategoryButton(null, T("Tümü", "All"), "", DockWidgetCatalog.All.Count);
        foreach (DockWidgetCategory c in Enum.GetValues<DockWidgetCategory>())
        {
            int count = DockWidgetCatalog.InCategory(c).Count(d => DockWidgetTileFactory.IsImplemented(d.Id));
            if (count == 0) continue;
            string glyph = DockWidgetCatalog.InCategory(c).First().Glyph;
            AddCategoryButton(c, DockWidgetCatalog.CategoryName(c, Lang), glyph, count);
        }
    }

    private void AddCategoryButton(DockWidgetCategory? category, string name, string glyph, int count)
    {
        var p = _palette;
        bool selected = _category == category;
        var countText = W.Text(p, count.ToString(), 11, p.TertiaryForeground);
        DockPanel.SetDock(countText, Dock.Right);
        var g = W.Glyph(p, glyph, 13, selected ? p.AccentBrush : p.SecondaryForeground);
        g.Margin = new Thickness(0, 0, 10, 0);
        DockPanel.SetDock(g, Dock.Left);
        var row = new DockPanel();
        row.Children.Add(countText);
        row.Children.Add(g);
        row.Children.Add(W.Text(p, name, 13, selected ? p.Foreground : p.SecondaryForeground, selected ? FontWeights.SemiBold : FontWeights.Normal));
        var b = new Border
        {
            Child = row,
            Padding = new Thickness(10, 7, 10, 7),
            CornerRadius = new CornerRadius(10),
            Margin = new Thickness(0, 1, 0, 1),
            Background = selected ? W.Brush(p.Accent, 0x26) : Brushes.Transparent,
            Cursor = Cursors.Hand
        };
        b.MouseEnter += (_, _) => { if (_category != category) b.Background = p.TileHoverBackground; };
        b.MouseLeave += (_, _) => { if (_category != category) b.Background = Brushes.Transparent; };
        b.MouseLeftButtonUp += (_, _) =>
        {
            _category = category;
            RebuildCategories();
            RebuildGrid();
        };
        _categoryList.Children.Add(b);
    }

    private void RebuildGrid()
    {
        DetachPreviews();
        _grid.Children.Clear();
        var items = DockWidgetCatalog.Search(_search.Text, Lang)
            .Where(d => (_category is null || d.Category == _category) && DockWidgetTileFactory.IsImplemented(d.Id))
            .ToList();
        _countText.Text = T($"{items.Count} widget", $"{items.Count} widgets");

        int i = 0;
        foreach (var d in items)
        {
            var card = BuildPreviewCard(d, i++);
            if (card is not null) _grid.Children.Add(card);
        }
        if (items.Count == 0)
        {
            var empty = W.Text(_palette, T("Eşleşen widget yok", "No matching widgets"), 13, _palette.SecondaryForeground, ui: false);
            empty.Margin = new Thickness(4, 20, 0, 0);
            _grid.Children.Add(empty);
        }
    }

    private Border? BuildPreviewCard(DockWidgetDescriptor d, int index)
    {
        var p = _palette;
        IDockWidgetTile? tile;
        try
        {
            tile = DockWidgetTileFactory.Create(d.Id, _previewCtx);
        }
        catch
        {
            tile = null;
        }
        if (tile is null) return null;

        // Preview stage that looks like a slice of the dock.
        var stage = new Border
        {
            Height = 60,
            CornerRadius = new CornerRadius(14),
            Background = p.PillBackground,
            BorderBrush = W.Brush(p.PillBorderColor),
            BorderThickness = new Thickness(0.8),
            Padding = new Thickness(10, 0, 10, 0),
            Child = new Grid { Children = { tile.Root } },
            IsHitTestVisible = false
        };
        tile.Root.HorizontalAlignment = HorizontalAlignment.Center;
        tile.Root.VerticalAlignment = VerticalAlignment.Center;

        var name = W.Text(p, d.Name(Lang), 13, weight: FontWeights.SemiBold);
        name.Margin = new Thickness(2, 10, 0, 2);
        var desc = W.Text(p, d.Description(Lang), 11.5, p.SecondaryForeground, ui: false);
        desc.TextWrapping = TextWrapping.Wrap;
        desc.TextTrimming = TextTrimming.CharacterEllipsis;
        desc.MaxHeight = 32;
        desc.Margin = new Thickness(2, 0, 0, 0);

        var check = new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(11),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, -6, -6, 0),
            Child = W.Glyph(p, "", 11, Brushes.White),
            Background = W.Brush(p.Positive),
            IsHitTestVisible = false,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(0, 0)
        };

        var body = new Grid();
        var col = new StackPanel();
        col.Children.Add(stage);
        col.Children.Add(name);
        col.Children.Add(desc);
        body.Children.Add(col);
        body.Children.Add(check);

        var card = new Border
        {
            Width = 178,
            Margin = new Thickness(0, 0, 12, 12),
            Padding = new Thickness(10),
            CornerRadius = new CornerRadius(16),
            Background = p.CardRowBackground,
            BorderBrush = p.TileBorder,
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            Child = body,
            Tag = d.Id,
            RenderTransform = new TranslateTransform(),
            Opacity = 0
        };
        System.Windows.Automation.AutomationProperties.SetName(card, d.Name(Lang));
        card.MouseEnter += (_, _) => AnimateLift(card, -3);
        card.MouseLeave += (_, _) => AnimateLift(card, 0);
        card.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            W.Pulse(card, 0.96);
            if (IsOnDock(d.Id)) _remove(d.Id);
            else _add(d.Id);
            RefreshSelection();
        };

        // Staggered entrance.
        var begin = TimeSpan.FromMilliseconds(Math.Min(index, 18) * 22);
        card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { BeginTime = begin });
        ((TranslateTransform)card.RenderTransform).BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(280)) { BeginTime = begin, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });

        _previews.Add((tile, card, check));
        try
        {
            tile.Update(_previewCtx);
            tile.OnAttached(_previewCtx);
        }
        catch (Exception ex)
        {
            CrashLogger.Log("DockWidget", ex, $"Preview '{d.Id}' failed.");
        }
        SetChecked(card, check, IsOnDock(d.Id), animate: false);
        return card;
    }

    private static void AnimateLift(Border card, double y)
    {
        if (card.RenderTransform is TranslateTransform tt)
        {
            tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(y, TimeSpan.FromMilliseconds(160))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        }
    }

    private bool IsOnDock(string id) => _orch.Config.DockWidgets.Contains(id, StringComparer.OrdinalIgnoreCase);

    /// <summary>Refreshes the "on the dock" checks (called by the dock after it adds or removes a widget).</summary>
    public void RefreshSelection()
    {
        foreach (var (_, card, check) in _previews)
        {
            SetChecked(card, check, card.Tag is string id && IsOnDock(id), animate: true);
        }
    }

    private void SetChecked(Border card, Border check, bool on, bool animate)
    {
        card.BorderBrush = on ? W.Brush(_palette.Accent, 0xC0) : _palette.TileBorder;
        var st = (ScaleTransform)check.RenderTransform;
        double target = on ? 1 : 0;
        if (!animate)
        {
            st.ScaleX = st.ScaleY = target;
            return;
        }
        var anim = new DoubleAnimation(target, TimeSpan.FromMilliseconds(on ? 380 : 160))
        {
            EasingFunction = on ? new ElasticEase { Oscillations = 1, Springiness = 5 } : new QuadraticEase()
        };
        st.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        st.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
    }

    private void UpdatePreviews()
    {
        _previewCtx.Now = DateTime.Now;
        foreach (var (tile, _, _) in _previews)
        {
            try { tile.Update(_previewCtx); } catch { }
        }
    }

    private void DetachPreviews()
    {
        foreach (var (tile, _, _) in _previews)
        {
            try { tile.OnDetached(); } catch { }
        }
        _previews.Clear();
    }

    private void PlayOpenAnimation()
    {
        if (Content is not FrameworkElement root) return;
        var st = new ScaleTransform(0.96, 0.96);
        root.RenderTransformOrigin = new Point(0.5, 1.0);
        root.RenderTransform = st;
        var ease = new BackEase { Amplitude = 0.3, EasingMode = EasingMode.EaseOut };
        var dur = TimeSpan.FromMilliseconds(260);
        st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.96, 1, dur) { EasingFunction = ease });
        st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.96, 1, dur) { EasingFunction = ease });
        root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
    }

    /// <summary>Renders the picker off-screen for the render audit.</summary>
    public System.Windows.Media.Imaging.RenderTargetBitmap RenderForAudit(int width = 860, int height = 600)
    {
        RebuildCategories();
        RebuildGrid();
        if (Content is FrameworkElement root)
        {
            root.Measure(new Size(width, height));
            root.Arrange(new Rect(0, 0, width, height));
            root.UpdateLayout();
            foreach (var (_, card, _) in _previews)
            {
                card.BeginAnimation(OpacityProperty, null);
                card.Opacity = 1;
                ((TranslateTransform)card.RenderTransform).BeginAnimation(TranslateTransform.YProperty, null);
            }
            root.UpdateLayout();
            var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(root);
            bmp.Freeze();
            return bmp;
        }
        throw new InvalidOperationException("Picker has no content.");
    }
}
