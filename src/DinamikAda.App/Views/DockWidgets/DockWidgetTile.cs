using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DinamikAda.App.Services;
using DinamikAda.App.Services.DockWidgets;
using DinamikAda.Core.Config;
using DinamikAda.Core.Widgets;

namespace DinamikAda.App.Views.DockWidgets;

/// <summary>Everything a tile needs: services, live data, style and the dock's popup/rebuild hooks.</summary>
public sealed class DockWidgetContext
{
    public required AppOrchestrator Orch { get; init; }
    public required DockWidgetDataHub Data { get; init; }
    public required DockWidgetPalette Palette { get; set; }
    /// <summary>Opens (or replaces) the dock's detail card with <c>content</c>, anchored to <c>anchor</c>.</summary>
    public required Action<FrameworkElement, FrameworkElement> ShowCard { get; init; }
    public required Action CloseCard { get; init; }
    /// <summary>True while the card currently shown belongs to <c>anchor</c>.</summary>
    public required Func<FrameworkElement, bool> IsCardOpenFor { get; init; }
    /// <summary>Asks the dock to rebuild its tiles (e.g. after a widget changes width or is removed).</summary>
    public required Action RequestRebuild { get; init; }

    public AppConfig Config => Orch.Config;
    public string Lang => Orch.Config.Language;
    public bool En => Orch.Config.Language == "en";
    /// <summary>Tile height in DIP (the dock icon size).</summary>
    public double TileHeight { get; set; } = 32;
    public DateTime Now { get; set; } = DateTime.Now;
    /// <summary>True when rendering for screenshots/smoke tests: no OS side effects, deterministic data.</summary>
    public bool IsAuditMode { get; set; }

    public string T(string tr, string en) => En ? en : tr;

    /// <summary>Edits and persists the config the same way Settings does.</summary>
    public void EditConfig(Action<AppConfig> edit) => Orch.ConfigStore.Update(Orch.Config, edit);
}

public interface IDockWidgetTile
{
    string Id { get; }
    DockWidgetDescriptor Descriptor { get; }
    /// <summary>The element placed on the dock; height = <see cref="DockWidgetContext.TileHeight"/>.</summary>
    FrameworkElement Root { get; }
    /// <summary>Called every second and whenever widget data changes.</summary>
    void Update(DockWidgetContext ctx);
    /// <summary>Optional detail card shown above the tile on click.</summary>
    FrameworkElement? BuildCard(DockWidgetContext ctx);
    /// <summary>Called by the dock once the tile is placed (start looping animations here).</summary>
    void OnAttached(DockWidgetContext ctx);
    /// <summary>Called when the tile is removed (stop timers and looping animations).</summary>
    void OnDetached();
}

/// <summary>
/// Base tile, sized to the descriptor's width units.
/// <list type="bullet">
/// <item>Plain tiles (counters, text) have no box at all: they sit straight on the dock glass, separated by spacing.</item>
/// <item>Content-coloured tiles call <see cref="SetTileBackground"/>; they get a gradient body that crossfades on
/// change, a glossy top highlight and a hairline inner rim.</item>
/// <item>Hover is a soft brightness lift (and a 1px rise), never a grey fill. Click opens the card.</item>
/// </list>
/// Subclasses build their content once in <see cref="BuildContent"/> and refresh it in <see cref="Refresh"/>.
/// </summary>
public abstract class DockWidgetTileBase : IDockWidgetTile
{
    /// <summary>Gradient crossfade length when a tile's colour changes (new track, new sky).</summary>
    public const double BackgroundCrossfadeMs = 600;

    private static readonly Brush GlossBrush = DockWidgetPalette.Freeze(new LinearGradientBrush(
        new GradientStopCollection
        {
            new(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF), 0.0),
            new(Color.FromArgb(0x0E, 0xFF, 0xFF, 0xFF), 0.28),
            new(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0.45),
            new(Color.FromArgb(0x00, 0x00, 0x00, 0x00), 0.62),
            new(Color.FromArgb(0x1A, 0x00, 0x00, 0x00), 1.0)
        }, 90));
    private static readonly Brush RimBrush = DockWidgetPalette.Freeze(new LinearGradientBrush(
        new GradientStopCollection
        {
            new(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF), 0.0),
            new(Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF), 0.45),
            new(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF), 1.0)
        }, 90));
    private static readonly Brush VividHoverBrush = DockWidgetPalette.Freeze(new SolidColorBrush(Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF)));

    private readonly Border _chip;
    private readonly Border _bgA;
    private readonly Border _bgB;
    private readonly Border _gloss;
    private readonly Border _rim;
    private readonly Border _hover;
    private readonly Border _host;
    private readonly TranslateTransform _lift = new();
    private bool _frontA = true;
    private bool _vivid;
    private Brush? _plainHover;
    private FrameworkElement? _card;
    private DockWidgetContext? _ctx;

    protected DockWidgetTileBase(DockWidgetDescriptor descriptor, DockWidgetContext ctx)
    {
        Descriptor = descriptor;
        _ctx = ctx;
        double h = ctx.TileHeight;
        var radius = new CornerRadius(Math.Max(7, Math.Round(h * 0.28)));
        _chip = new Border
        {
            Height = h,
            Width = TileWidth(descriptor, h),
            CornerRadius = radius,
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Top,
            ClipToBounds = false,
            SnapsToDevicePixels = true,
            Tag = descriptor.Id
        };
        _bgA = new Border { CornerRadius = radius, Opacity = 0, IsHitTestVisible = false };
        _bgB = new Border { CornerRadius = radius, Opacity = 0, IsHitTestVisible = false };
        _gloss = new Border { CornerRadius = radius, Background = GlossBrush, Opacity = 0, IsHitTestVisible = false };
        _rim = new Border { CornerRadius = radius, BorderBrush = RimBrush, BorderThickness = new Thickness(0.8), Opacity = 0, IsHitTestVisible = false };
        _hover = new Border { CornerRadius = radius, Opacity = 0, IsHitTestVisible = false };
        _host = new Border { Padding = ContentPadding(h), RenderTransform = _lift };
        var layers = new Grid { ClipToBounds = false };
        layers.Children.Add(_bgA);
        layers.Children.Add(_bgB);
        layers.Children.Add(_gloss);
        layers.Children.Add(_hover);
        layers.Children.Add(_host);
        layers.Children.Add(_rim);
        _chip.Child = layers;
        System.Windows.Automation.AutomationProperties.SetName(_chip, descriptor.Name(ctx.Lang));
        _chip.MouseEnter += (_, _) => SetHover(true);
        _chip.MouseLeave += (_, _) => SetHover(false);
        _host.Child = BuildContent(ctx);
    }

    /// <summary>Tile width in DIP for a descriptor at tile height <paramref name="h"/>.</summary>
    public static double TileWidth(DockWidgetDescriptor d, double h) =>
        Math.Round(h * d.WidthUnits + (d.WidthUnits > 1 ? (d.WidthUnits - 1) * 6 : 0));

    public string Id => Descriptor.Id;
    public DockWidgetDescriptor Descriptor { get; }
    public FrameworkElement Root => _chip;
    protected Border Chip => _chip;

    /// <summary>False for tiles that draw their own full-bleed shape (rings, calendar page…): no hover glow.</summary>
    protected virtual bool ShowChrome => true;

    /// <summary>True for tiles that always have a coloured body (media, weather, battery…): the default padding
    /// then leaves room inside the body. Read during construction, so overrides must return a constant.</summary>
    protected virtual bool VividByDefault => false;

    protected virtual Thickness ContentPadding(double h)
    {
        if (Descriptor.WidthUnits <= 1.05) return new Thickness(2);
        double x = Math.Round(h * (VividByDefault ? 0.26 : 0.08));
        return new Thickness(x, 2, x, 2);
    }

    /// <summary>True once the tile shows a coloured body.</summary>
    protected bool IsVivid => _vivid;

    /// <summary>Replaces the tile content (used by tiles that rebuild their layout after construction).</summary>
    protected void SetContent(FrameworkElement content) => _host.Child = content;

    /// <summary>
    /// Gives the tile a coloured body (null removes it). Changes crossfade over <see cref="BackgroundCrossfadeMs"/>;
    /// the gloss highlight and the hairline rim fade in with the first colour.
    /// </summary>
    protected void SetTileBackground(Brush? brush, bool animate = true)
    {
        double ms = animate ? BackgroundCrossfadeMs : 0;
        if (brush is null)
        {
            if (!_vivid) return;
            _vivid = false;
            Fade(_bgA, 0, ms);
            Fade(_bgB, 0, ms);
            Fade(_gloss, 0, ms);
            Fade(_rim, 0, ms);
            return;
        }
        var front = _frontA ? _bgA : _bgB;
        var back = _frontA ? _bgB : _bgA;
        if (_vivid && ReferenceEquals(front.Background, brush)) return;
        if (!_vivid)
        {
            // First colour: no crossfade partner, just fade the body in.
            front.Background = brush;
            Fade(front, 1, ms);
            Fade(back, 0, 0);
            Fade(_gloss, 1, ms);
            Fade(_rim, 1, ms);
            _vivid = true;
            return;
        }
        back.Background = brush;
        // Both body layers stay below the content (z-index 0): the incoming one just sits above the outgoing one.
        Panel.SetZIndex(back, -1);
        Panel.SetZIndex(front, -2);
        Fade(back, 1, ms, from: ms > 0 ? 0 : null);
        // The old body stays under the new one until it is fully covered, then drops out.
        if (ms > 0) Fade(front, 0, 1, delayMs: ms);
        else Fade(front, 0, 0);
        _frontA = !_frontA;
    }

    private static void Fade(UIElement el, double to, double ms, double? from = null, double delayMs = 0)
    {
        if (ms <= 0)
        {
            el.BeginAnimation(UIElement.OpacityProperty, null);
            el.Opacity = to;
            return;
        }
        var a = Motion.To(to, ms, Motion.Gentle, delayMs);
        if (from.HasValue) a.From = from.Value;
        el.BeginAnimation(UIElement.OpacityProperty, a, HandoffBehavior.SnapshotAndReplace);
    }

    private void SetHover(bool on)
    {
        if (_vivid) _hover.Background = VividHoverBrush;
        else if (ShowChrome)
        {
            _plainHover ??= PlainHoverBrush(_ctx?.Palette.IsLight == true);
            _hover.Background = _plainHover;
        }
        else _hover.Background = null;
        _hover.BeginAnimation(UIElement.OpacityProperty, Motion.To(on ? 1 : 0, on ? 120 : 200), HandoffBehavior.SnapshotAndReplace);
        _lift.BeginAnimation(TranslateTransform.YProperty, Motion.To(on ? -1 : 0, on ? 180 : 240), HandoffBehavior.SnapshotAndReplace);
    }

    /// <summary>A soft radial light (not a box) for hovering a plain tile.</summary>
    private static Brush PlainHoverBrush(bool light)
    {
        byte c = light ? (byte)0 : (byte)0xFF;
        var b = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.5, 0.4),
            Center = new Point(0.5, 0.5),
            RadiusX = 0.62,
            RadiusY = 0.8
        };
        b.GradientStops.Add(new GradientStop(Color.FromArgb(light ? (byte)0x12 : (byte)0x1C, c, c, c), 0));
        b.GradientStops.Add(new GradientStop(Color.FromArgb(light ? (byte)0x06 : (byte)0x0A, c, c, c), 0.6));
        b.GradientStops.Add(new GradientStop(Color.FromArgb(0, c, c, c), 1));
        return DockWidgetPalette.Freeze(b);
    }

    protected abstract FrameworkElement BuildContent(DockWidgetContext ctx);
    protected abstract void Refresh(DockWidgetContext ctx);

    /// <summary>Card content; null when the tile has no detail card (then <see cref="OnPrimaryClick"/> handles clicks).</summary>
    protected virtual FrameworkElement? CreateCard(DockWidgetContext ctx) => null;
    /// <summary>Refresh the open card's live parts (called every second while it is shown).</summary>
    protected virtual void RefreshCard(DockWidgetContext ctx) { }
    /// <summary>Click action for tiles without a card. Return true when handled.</summary>
    public virtual bool OnPrimaryClick(DockWidgetContext ctx) => false;

    public void Update(DockWidgetContext ctx)
    {
        _ctx = ctx;
        Refresh(ctx);
        if (_card is not null && ctx.IsCardOpenFor(_chip)) RefreshCard(ctx);
    }

    public FrameworkElement? BuildCard(DockWidgetContext ctx)
    {
        _card = CreateCard(ctx);
        if (_card is not null) RefreshCard(ctx);
        return _card;
    }

    public virtual void OnAttached(DockWidgetContext ctx) { }
    public virtual void OnDetached() { }

    /// <summary>Re-opens the card with freshly built content (after structural changes, e.g. a todo was added).</summary>
    protected void ReopenCard(DockWidgetContext ctx)
    {
        var card = BuildCard(ctx);
        if (card is not null) ctx.ShowCard(_chip, card);
    }
}

/// <summary>Registry of tile constructors; each widget group registers its ids.</summary>
public static class DockWidgetTileFactory
{
    private static readonly Dictionary<string, Func<DockWidgetDescriptor, DockWidgetContext, IDockWidgetTile>> Map =
        BuildMap();

    private static Dictionary<string, Func<DockWidgetDescriptor, DockWidgetContext, IDockWidgetTile>> BuildMap()
    {
        var map = new Dictionary<string, Func<DockWidgetDescriptor, DockWidgetContext, IDockWidgetTile>>(StringComparer.OrdinalIgnoreCase);
        SystemWidgetTiles.Register(map);
        ClockWidgetTiles.Register(map);
        WeatherWidgetTiles.Register(map);
        ProductivityWidgetTiles.Register(map);
        MediaAppsFinanceWidgetTiles.Register(map);
        Accounts.FinanceAccountTiles.Register(map);
        Accounts.DeveloperAccountTiles.Register(map);
        Accounts.SocialMailAccountTiles.Register(map);
        return map;
    }

    public static bool IsImplemented(string id) => Map.ContainsKey(id);

    public static IDockWidgetTile? Create(string id, DockWidgetContext ctx)
    {
        var d = DockWidgetCatalog.Find(id);
        if (d is null || !Map.TryGetValue(d.Id, out var create)) return null;
        try
        {
            return create(d, ctx);
        }
        catch (Exception ex)
        {
            Services.CrashLogger.Log("DockWidget", ex, $"Widget '{id}' failed to build.");
            return null;
        }
    }
}
