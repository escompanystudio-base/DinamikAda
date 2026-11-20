using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DinamikAda.App.Views.DockWidgets;
using DinamikAda.Core.Config;
using DinamikAda.Core.Widgets;

namespace DinamikAda.App.Views;

/// <summary>Smoke-test and render-audit entry points for the dock widgets (no OS side effects).</summary>
public partial class DockWindow
{
    /// <summary>Builds, updates, opens the card of and detaches every catalog widget; returns failures.</summary>
    public List<string> AuditBuildAllWidgets()
    {
        SetSmokeOrAuditMode(true);
        var failures = new List<string>();
        var ctx = WidgetContext;
        foreach (var d in DockWidgetCatalog.All)
        {
            if (!DockWidgetTileFactory.IsImplemented(d.Id))
            {
                failures.Add($"Dock widget '{d.Id}' has no tile implementation.");
                continue;
            }
            try
            {
                var tile = DockWidgetTileFactory.Create(d.Id, ctx);
                if (tile is null)
                {
                    failures.Add($"Dock widget '{d.Id}' could not be created.");
                    continue;
                }
                tile.Update(ctx);
                tile.OnAttached(ctx);
                ctx.Now = DateTime.Now.AddSeconds(61);
                tile.Update(ctx);
                tile.Root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                if (tile.Root.DesiredSize.Height > ctx.TileHeight + 0.5)
                    failures.Add($"Dock widget '{d.Id}' is {tile.Root.DesiredSize.Height:0.#} DIP tall (tile height {ctx.TileHeight}).");
                var card = tile.BuildCard(ctx);
                card?.Measure(new Size(400, double.PositiveInfinity));
                tile.OnDetached();
            }
            catch (Exception ex)
            {
                failures.Add($"Dock widget '{d.Id}' threw: {ex.GetType().Name}: {ex.Message}");
            }
        }
        return failures;
    }

    /// <summary>Renders the dock with the given widgets and style (the audit config is isolated).</summary>
    public RenderTargetBitmap RenderWidgetDockForAudit(IReadOnlyList<string> widgetIds, DockVisualStyle style, int width = 1600, int height = 110,
        DockScreenEdge edge = DockScreenEdge.Bottom)
    {
        SetSmokeOrAuditMode(true);
        _orch.Config.DockWidgets = DockWidgetCatalog.Sanitize(widgetIds);
        _orch.Config.DockStyle = style;
        _orch.Config.DockEdge = edge;
        ApplyDockEdgeLayout();
        ApplyDockStyleVisuals();
        RenderForAudit(width, height);
        // Let entrance animations (pop-ins, staggered fades, ring sweeps) finish before capturing.
        PumpAuditAnimations(1400);
        foreach (var (tile, _) in _widgetTiles) tile.Update(WidgetContext);
        DockRootGrid.Measure(new Size(width, height));
        DockRootGrid.Arrange(new Rect(0, 0, width, height));
        DockRootGrid.UpdateLayout();
        var bmp = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(DockRootGrid);
        bmp.Freeze();
        return bmp;
    }

    private static void PumpAuditAnimations(int ms)
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    /// <summary>Renders one widget's detail card on a dark backdrop.</summary>
    public RenderTargetBitmap RenderWidgetCardForAudit(string widgetId, int width = 440, int height = 600)
    {
        SetSmokeOrAuditMode(true);
        var ctx = WidgetContext;
        var tile = DockWidgetTileFactory.Create(widgetId, ctx) ?? throw new InvalidOperationException($"No tile for {widgetId}.");
        tile.Update(ctx);
        var card = tile.BuildCard(ctx) ?? throw new InvalidOperationException($"{widgetId} has no card.");
        var border = new System.Windows.Controls.Border
        {
            Background = ctx.Palette.CardBackground,
            BorderBrush = ctx.Palette.CardBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(16, 14, 16, 14),
            Child = card
        };
        border.Measure(new Size(width - 24, double.PositiveInfinity));
        double h = Math.Ceiling(border.DesiredSize.Height);
        double w = Math.Ceiling(border.DesiredSize.Width);
        border.Arrange(new Rect(0, 0, w, h));
        border.UpdateLayout();
        tile.Update(ctx);
        PumpAuditAnimations(900);
        border.UpdateLayout();

        int finalW = Math.Max(width, (int)w + 24);
        int finalH = Math.Max(height, (int)h + 24);
        var cardBmp = new RenderTargetBitmap((int)w, (int)h, 96, 96, PixelFormats.Pbgra32);
        cardBmp.Render(border);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(8, 10, 16)), null, new Rect(0, 0, finalW, finalH));
            dc.DrawImage(cardBmp, new Rect(Math.Round((finalW - w) / 2), Math.Round((finalH - h) / 2), w, h));
        }
        var bmp = new RenderTargetBitmap(finalW, finalH, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(dv);
        bmp.Freeze();
        tile.OnDetached();
        return bmp;
    }

    public RenderTargetBitmap RenderWidgetPickerForAudit(int width = 860, int height = 600)
    {
        SetSmokeOrAuditMode(true);
        var picker = new DockWidgetPickerWindow(_orch, WidgetHub,
            () => CreateWidgetPalette(Color.FromRgb(10, 132, 255)), _ => { }, _ => { });
        return picker.RenderForAudit(width, height);
    }
}
