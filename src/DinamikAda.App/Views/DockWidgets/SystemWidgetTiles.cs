using System.Windows;
using System.Windows.Controls;
using DinamikAda.Core.Widgets;

namespace DinamikAda.App.Views.DockWidgets;

/// <summary>System widgets: CPU/RAM rings, top processes, network, temperature, batteries, toggles, progress.</summary>
internal static class SystemWidgetTiles
{
    public static void Register(IDictionary<string, Func<DockWidgetDescriptor, DockWidgetContext, IDockWidgetTile>> map)
    {
        map["cpu"] = (d, c) => new LoadRingTile(d, c, isCpu: true);
        map["ram"] = (d, c) => new LoadRingTile(d, c, isCpu: false);
    }

    /// <summary>CPU or RAM load as a ring with the percentage in the middle; the card shows the history.</summary>
    private sealed class LoadRingTile : DockWidgetTileBase
    {
        private readonly bool _isCpu;
        private RingGauge _ring = null!;
        private TextBlock _value = null!;
        private Sparkline? _cardChart;
        private TextBlock? _cardDetail;

        public LoadRingTile(DockWidgetDescriptor d, DockWidgetContext c, bool isCpu) : base(d, c)
        {
            _isCpu = isCpu;
        }

        protected override bool ShowChrome => false;

        protected override FrameworkElement BuildContent(DockWidgetContext ctx)
        {
            var p = ctx.Palette;
            double h = ctx.TileHeight;
            _ring = W.Ring(p, h - 2, Math.Max(2.5, h * 0.09), p.Accent);
            _value = W.Text(p, "", Math.Max(8.5, h * 0.27), weight: FontWeights.SemiBold);
            _value.HorizontalAlignment = HorizontalAlignment.Center;
            var g = new Grid();
            g.Children.Add(_ring);
            g.Children.Add(_value);
            return g;
        }

        protected override void Refresh(DockWidgetContext ctx)
        {
            var t = ctx.Orch.Telemetry.Current;
            double pct = _isCpu ? t.CpuPercent : t.MemoryPercent;
            if (!double.IsFinite(pct)) pct = 0;
            var color = ctx.Palette.LoadColor(pct);
            if (_ring.Fill is not System.Windows.Media.SolidColorBrush sb || sb.Color != color) _ring.Fill = W.Brush(color);
            _ring.AnimateTo(pct / 100.0);
            W.SetTextAnimated(_value, ((int)Math.Round(pct)).ToString());
        }

        protected override FrameworkElement? CreateCard(DockWidgetContext ctx)
        {
            var p = ctx.Palette;
            _cardChart = new Sparkline { Height = 70, Stroke = p.AccentBrush, FixedMax = 100, Margin = new Thickness(0, 4, 0, 8) };
            _cardDetail = W.Text(p, "", 12.5, p.SecondaryForeground, ui: false);
            var body = W.Column(_cardChart, _cardDetail);
            return W.Card(p, Descriptor.Glyph, Descriptor.Name(ctx.Lang), body, 280,
                W.Button(p, ctx.T("Görev Yöneticisi", "Task Manager"), () => DockWidgetActions.Run(ctx, "taskmgr.exe"), ""));
        }

        protected override void RefreshCard(DockWidgetContext ctx)
        {
            var t = ctx.Orch.Telemetry.Current;
            if (_isCpu)
            {
                _cardChart?.SetValues(ctx.Data.CpuHistory);
                if (_cardDetail is not null) _cardDetail.Text = ctx.T($"Şu an %{t.CpuPercent:0} · son 60 saniye", $"Now {t.CpuPercent:0}% · last 60 seconds");
            }
            else
            {
                _cardChart?.SetValues(ctx.Data.MemHistory);
                if (_cardDetail is not null) _cardDetail.Text = $"{t.MemoryUsedGb:0.0} / {t.MemoryTotalGb:0.0} GB";
            }
        }
    }
}
