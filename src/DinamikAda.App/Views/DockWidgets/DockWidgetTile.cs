using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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
/// Base tile: a rounded chip sized to the descriptor's width units, hover highlight, click → card.
/// Subclasses build their content once in <see cref="BuildContent"/> and refresh it in <see cref="Refresh"/>.
/// </summary>
public abstract class DockWidgetTileBase : IDockWidgetTile
{
    private readonly Border _chip;
    private FrameworkElement? _card;
    private DockWidgetContext? _ctx;

    protected DockWidgetTileBase(DockWidgetDescriptor descriptor, DockWidgetContext ctx)
    {
        Descriptor = descriptor;
        _ctx = ctx;
        var p = ctx.Palette;
        double h = ctx.TileHeight;
        _chip = new Border
        {
            Height = h,
            Width = Math.Round(h * descriptor.WidthUnits + (descriptor.WidthUnits > 1 ? (descriptor.WidthUnits - 1) * 6 : 0)),
            CornerRadius = new CornerRadius(Math.Max(8, h * 0.28)),
            Background = ShowChrome ? p.TileBackground : Brushes.Transparent,
            BorderBrush = ShowChrome ? p.TileBorder : Brushes.Transparent,
            BorderThickness = new Thickness(ShowChrome ? 0.6 : 0),
            Padding = ContentPadding(h),
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Top,
            ClipToBounds = false,
            SnapsToDevicePixels = true,
            Tag = descriptor.Id
        };
        System.Windows.Automation.AutomationProperties.SetName(_chip, descriptor.Name(ctx.Lang));
        _chip.MouseEnter += (_, _) => { if (ShowChrome && _ctx is not null) _chip.Background = _ctx.Palette.TileHoverBackground; };
        _chip.MouseLeave += (_, _) => { if (ShowChrome && _ctx is not null) _chip.Background = _ctx.Palette.TileBackground; };
        _chip.Child = BuildContent(ctx);
    }

    public string Id => Descriptor.Id;
    public DockWidgetDescriptor Descriptor { get; }
    public FrameworkElement Root => _chip;
    protected Border Chip => _chip;

    /// <summary>False for tiles that draw their own full-bleed shape (rings, calendar page…).</summary>
    protected virtual bool ShowChrome => true;
    protected virtual Thickness ContentPadding(double h) => Descriptor.WidthUnits > 1.05
        ? new Thickness(Math.Round(h * 0.22), 2, Math.Round(h * 0.22), 2)
        : new Thickness(2);

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
