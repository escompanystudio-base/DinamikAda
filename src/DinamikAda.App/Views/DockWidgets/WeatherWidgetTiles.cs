using System.Collections.Immutable;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using DinamikAda.App.Services.DockWidgets;
using DinamikAda.Core.Widgets;

namespace DinamikAda.App.Views.DockWidgets;

/// <summary>Weather widgets: current, details, forecast, status, hourly and the sun's path.</summary>
internal static class WeatherWidgetTiles
{
    public static void Register(IDictionary<string, Func<DockWidgetDescriptor, DockWidgetContext, IDockWidgetTile>> map)
    {
        map["weather"] = (d, c) => new WeatherNowTile(d, c);
        map["weather-details"] = (d, c) => new WeatherDetailsTile(d, c);
        map["forecast"] = (d, c) => new ForecastTile(d, c);
        map["weather-status"] = (d, c) => new WeatherStatusTile(d, c);
        map["hourly"] = (d, c) => new HourlyTile(d, c);
        map["sun"] = (d, c) => new SunTile(d, c);
    }

    // ---- Shared helpers ------------------------------------------------------------------------

    private const string RainGlyph = "";
    private const string MoonGlyph = "";
    private const string SunGlyph = "";
    private static readonly CultureInfo TrCulture = CultureInfo.GetCultureInfo("tr-TR");
    private static readonly CultureInfo EnCulture = CultureInfo.GetCultureInfo("en-US");

    private static CultureInfo Cul(DockWidgetContext ctx) => ctx.En ? EnCulture : TrCulture;

    private static double InnerWidth(DockWidgetDescriptor d, double h)
    {
        double u = d.WidthUnits;
        double tileW = Math.Round(h * u + (u > 1 ? (u - 1) * 6 : 0));
        double pad = u > 1.05 ? Math.Round(h * 0.22) : 2;
        return Math.Max(10, tileW - pad * 2);
    }

    private static double InnerHeight(double h) => Math.Max(10, h - 4);

    private static Viewbox Fit(UIElement child, HorizontalAlignment ha = HorizontalAlignment.Center) => new()
    {
        Child = child,
        Stretch = Stretch.Uniform,
        StretchDirection = StretchDirection.DownOnly,
        HorizontalAlignment = ha,
        VerticalAlignment = VerticalAlignment.Center
    };

    private static double ToDisplayTemp(double celsius, bool fahrenheit) => fahrenheit ? celsius * 9.0 / 5.0 + 32.0 : celsius;

    private static string Deg(DockWidgetContext ctx, double celsius) =>
        double.IsFinite(celsius)
            ? ((int)Math.Round(ToDisplayTemp(celsius, ctx.Config.WeatherFahrenheit))).ToString(CultureInfo.InvariantCulture) + "°"
            : "—";

    private static string DegUnit(DockWidgetContext ctx, double celsius) =>
        Deg(ctx, celsius) + (ctx.Config.WeatherFahrenheit ? "F" : "C");

    private static string Clock(DockWidgetContext ctx, DateTime t) =>
        ctx.Config.DockClock24h ? DockWidgetMath.FormatClock(t, true) : DockWidgetMath.FormatClock(t, false) + " " + DockWidgetMath.AmPm(t);

    private static string NoDataText(DockWidgetContext ctx) =>
        ctx.Data.WeatherStatus ?? ctx.T("Ayarlardan şehir seç", "Pick a city in Settings");

    private static WeatherSnapshot? Weather(DockWidgetContext ctx) =>
        ctx.Data.Weather ?? (ctx.IsAuditMode ? SampleWeather(ctx.Now) : null);

    private static WeatherSnapshot? _sample;

    /// <summary>Deterministic sample for screenshots: Istanbul, 18°, partly cloudy, with a 7-day outlook.</summary>
    private static WeatherSnapshot SampleWeather(DateTime now)
    {
        var cached = _sample;
        if (cached is not null && cached.FetchedLocal.Date == now.Date && cached.FetchedLocal.Hour == now.Hour) return cached;
        var d = now.Date;
        int[] codes = [2, 3, 61, 1, 0, 80, 2];
        double[] hi = [19, 17, 14, 16, 20, 15, 18];
        double[] lo = [12, 11, 9, 8, 10, 9, 11];
        var days = Enumerable.Range(0, 7)
            .Select(i => new WeatherDay(d.AddDays(i), codes[i], hi[i], lo[i], codes[i] >= 61 ? 70 : 10))
            .ToImmutableArray();
        var start = d.AddHours(now.Hour + 1);
        double[] ht = [18, 17, 17, 16, 15, 14];
        int[] hc = [2, 2, 3, 3, 1, 0];
        var hours = Enumerable.Range(0, 6).Select(i => new WeatherHour(start.AddHours(i), hc[i], ht[i])).ToImmutableArray();
        var sunrise = d.AddHours(7).AddMinutes(12);
        var sunset = d.AddHours(16).AddMinutes(52);
        var snap = new WeatherSnapshot("Istanbul", 18, 16, 2, now >= sunrise && now <= sunset, 14, 0, 68, 19, 12, 10,
            sunrise, sunset, days, hours, d.AddHours(now.Hour));
        _sample = snap;
        return snap;
    }

    private static string ShortDay(DockWidgetContext ctx, DateTime day, DateTime today) =>
        day.Date == today.Date ? ctx.T("Bugün", "Today") : day.ToString("ddd", Cul(ctx));

    private static void StopFloat(UIElement el)
    {
        if (el.RenderTransform is TranslateTransform tt && !tt.IsFrozen) tt.BeginAnimation(TranslateTransform.YProperty, null);
    }

    /// <summary>Cross-fades a text block to new text (out, swap, in).</summary>
    private static void CrossFade(TextBlock tb, string text)
    {
        if (string.Equals(tb.Text, text, StringComparison.Ordinal)) return;
        if (string.IsNullOrEmpty(tb.Text))
        {
            tb.Text = text;
            return;
        }
        var o = new DoubleAnimation(0, TimeSpan.FromMilliseconds(140));
        o.Completed += (_, _) =>
        {
            tb.Text = text;
            tb.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260))
            {
                EasingFunction = Motion.Soft
            });
        };
        tb.BeginAnimation(UIElement.OpacityProperty, o);
    }

    /// <summary>Staggered slide-up fade for a set of columns.</summary>
    private static void StaggerIn(IEnumerable<UIElement> items, double stepMs = 60)
    {
        int i = 0;
        foreach (var el in items)
        {
            var tt = new TranslateTransform(0, 5);
            el.RenderTransform = tt;
            el.Opacity = 0;
            var begin = TimeSpan.FromMilliseconds(i * stepMs);
            var ease = Motion.Soft;
            tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(5, 0, TimeSpan.FromMilliseconds(340)) { BeginTime = begin, EasingFunction = ease });
            el.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300)) { BeginTime = begin });
            i++;
        }
    }

    /// <summary>
    /// Base for weather tiles: owns the "no data" overlay and the shared detail card. Subclasses render a
    /// snapshot in <see cref="Render"/>, which runs only when the snapshot (or display options) change.
    /// </summary>
    private abstract class WeatherTileBase : DockWidgetTileBase
    {
        private TextBlock _empty = null!;
        private FrameworkElement _main = null!;
        private object? _renderKey;
        private WeatherCard? _card;

        protected WeatherTileBase(DockWidgetDescriptor d, DockWidgetContext c) : base(d, c) { }

        protected abstract FrameworkElement BuildMain(DockWidgetContext ctx);
        protected abstract void Render(DockWidgetContext ctx, WeatherSnapshot wx, bool animate);
        /// <summary>Cheap per-second updates (e.g. the sun position); default none.</summary>
        protected virtual void Tick(DockWidgetContext ctx, WeatherSnapshot wx) { }

        protected bool HasRendered { get; private set; }

        protected override FrameworkElement BuildContent(DockWidgetContext ctx)
        {
            var p = ctx.Palette;
            double h = ctx.TileHeight;
            _main = BuildMain(ctx);
            _empty = W.Text(p, "", Math.Max(7, h * 0.22), p.SecondaryForeground, ui: false);
            _empty.TextWrapping = TextWrapping.Wrap;
            _empty.MaxHeight = InnerHeight(h);
            _empty.TextAlignment = TextAlignment.Center;
            _empty.HorizontalAlignment = HorizontalAlignment.Center;
            _empty.Visibility = Visibility.Collapsed;
            var g = new Grid();
            g.Children.Add(_main);
            g.Children.Add(_empty);
            return g;
        }

        protected override void Refresh(DockWidgetContext ctx)
        {
            var wx = Weather(ctx);
            if (wx is null)
            {
                _main.Visibility = Visibility.Collapsed;
                _empty.Visibility = Visibility.Visible;
                _empty.Text = NoDataText(ctx);
                _empty.ToolTip = _empty.Text;
                _renderKey = null;
                return;
            }
            _main.Visibility = Visibility.Visible;
            _empty.Visibility = Visibility.Collapsed;
            var key = (wx, ctx.Config.WeatherFahrenheit, ctx.Config.DockClock24h, ctx.Lang, ctx.Now.Date);
            if (!Equals(key, _renderKey))
            {
                _renderKey = key;
                Render(ctx, wx, HasRendered);
                HasRendered = true;
            }
            Tick(ctx, wx);
        }

        protected override FrameworkElement? CreateCard(DockWidgetContext ctx)
        {
            _card = new WeatherCard();
            return _card.Build(ctx, Descriptor);
        }

        protected override void RefreshCard(DockWidgetContext ctx) => _card?.Refresh(ctx);
    }

    /// <summary>Full weather card: conditions, feels-like, humidity, wind, rain, high/low, sun times, next 3 days.</summary>
    private sealed class WeatherCard
    {
        private StackPanel _body = null!;
        private TextBlock _title = null!;
        private object? _key = new();

        public FrameworkElement Build(DockWidgetContext ctx, DockWidgetDescriptor d)
        {
            _body = new StackPanel();
            var card = W.Card(ctx.Palette, d.Glyph, d.Name(ctx.Lang), _body, 300);
            // The card's header title is the second child of its header DockPanel; we retitle it with the city.
            if (card is StackPanel sp && sp.Children[0] is DockPanel header && header.Children[^1] is TextBlock tb) _title = tb;
            else _title = new TextBlock();
            return card;
        }

        public void Refresh(DockWidgetContext ctx)
        {
            var wx = Weather(ctx);
            object key = wx is null ? NoDataText(ctx) : (wx, ctx.Config.WeatherFahrenheit, ctx.Config.DockClock24h);
            if (Equals(key, _key)) return;
            _key = key;
            var p = ctx.Palette;
            _body.Children.Clear();
            if (wx is null)
            {
                var t = W.Text(p, NoDataText(ctx), 13, p.SecondaryForeground, ui: false);
                t.TextWrapping = TextWrapping.Wrap;
                _body.Children.Add(t);
                return;
            }
            _title.Text = string.IsNullOrWhiteSpace(wx.City) ? ctx.T("Hava Durumu", "Weather") : wx.City;

            var glyph = W.Glyph(p, DockWidgetMath.WeatherGlyph(wx.Code, wx.IsDay), 34, p.AccentBrush);
            var temp = W.Text(p, Deg(ctx, wx.TempC), 36, weight: FontWeights.SemiBold);
            temp.Margin = new Thickness(10, 0, 0, 0);
            var cond = W.Text(p, DockWidgetMath.WeatherText(wx.Code, ctx.Lang), 13.5, p.SecondaryForeground, ui: false);
            cond.Margin = new Thickness(10, 0, 0, 0);
            var top = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            top.Children.Add(glyph);
            top.Children.Add(temp);
            top.Children.Add(cond);
            _body.Children.Add(top);

            _body.Children.Add(W.CardRow(p, ctx.T("Hissedilen", "Feels like"), DegUnit(ctx, wx.FeelsLikeC)));
            _body.Children.Add(W.CardRow(p, ctx.T("Nem", "Humidity"), ctx.En ? $"{wx.Humidity}%" : $"%{wx.Humidity}"));
            _body.Children.Add(W.CardRow(p, ctx.T("Rüzgâr", "Wind"), $"{wx.WindKmh.ToString("0", CultureInfo.InvariantCulture)} km/h"));
            _body.Children.Add(W.CardRow(p, ctx.T("Yağış", "Precipitation"),
                $"{wx.PrecipMm.ToString("0.#", CultureInfo.InvariantCulture)} mm · " + (ctx.En ? $"{wx.PrecipProbability}%" : $"%{wx.PrecipProbability}")));
            _body.Children.Add(W.CardRow(p, ctx.T("En yüksek / en düşük", "High / low"), $"{Deg(ctx, wx.HighC)} / {Deg(ctx, wx.LowC)}"));
            _body.Children.Add(W.CardRow(p, ctx.T("Gün doğumu / batımı", "Sunrise / sunset"), $"{Clock(ctx, wx.Sunrise)} / {Clock(ctx, wx.Sunset)}"));

            var next = wx.Days.Where(x => x.Date.Date > ctx.Now.Date).Take(3).ToList();
            if (next.Count > 0)
            {
                var grid = new Grid { Margin = new Thickness(0, 10, 0, 0) };
                for (int i = 0; i < next.Count; i++)
                {
                    grid.ColumnDefinitions.Add(new ColumnDefinition());
                    var day = next[i];
                    var name = W.Text(p, day.Date.ToString("dddd", Cul(ctx)), 12, p.SecondaryForeground, FontWeights.SemiBold);
                    var g = W.Glyph(p, DockWidgetMath.WeatherGlyph(day.Code), 20, p.Foreground);
                    g.Margin = new Thickness(0, 4, 0, 4);
                    var hl = W.Text(p, $"{Deg(ctx, day.HighC)} / {Deg(ctx, day.LowC)}", 12.5, weight: FontWeights.SemiBold);
                    name.HorizontalAlignment = hl.HorizontalAlignment = HorizontalAlignment.Center;
                    var col = new Border
                    {
                        Background = p.CardRowBackground,
                        CornerRadius = new CornerRadius(10),
                        Padding = new Thickness(4, 8, 4, 8),
                        Margin = new Thickness(i == 0 ? 0 : 3, 0, i == next.Count - 1 ? 0 : 3, 0),
                        Child = W.Column(name, g, hl)
                    };
                    Grid.SetColumn(col, i);
                    grid.Children.Add(col);
                }
                _body.Children.Add(grid);
            }
            var upd = W.Text(p, ctx.T($"Güncellendi {Clock(ctx, wx.FetchedLocal)}", $"Updated {Clock(ctx, wx.FetchedLocal)}"), 11, p.TertiaryForeground, ui: false);
            upd.Margin = new Thickness(0, 8, 0, 0);
            _body.Children.Add(upd);
        }
    }

    // ---- weather ---------------------------------------------------------------------------------

    private sealed class WeatherNowTile : WeatherTileBase
    {
        private TextBlock _glyph = null!, _temp = null!, _hl = null!;
        private bool _floating;

        public WeatherNowTile(DockWidgetDescriptor d, DockWidgetContext c) : base(d, c) { }

        protected override FrameworkElement BuildMain(DockWidgetContext ctx)
        {
            var p = ctx.Palette;
            double h = ctx.TileHeight;
            _glyph = W.Glyph(p, "", Math.Max(11, h * 0.5), p.AccentBrush);
            _temp = W.Text(p, "", Math.Max(10, h * 0.38), weight: FontWeights.SemiBold);
            _hl = W.Text(p, "", Math.Max(6, h * 0.18), p.SecondaryForeground, FontWeights.SemiBold);
            _hl.Margin = new Thickness(0, -h * 0.04, 0, 0);
            var right = W.Column(_temp, _hl);
            right.Margin = new Thickness(Math.Max(2, h * 0.1), 0, 0, 0);
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            row.Children.Add(_glyph);
            row.Children.Add(right);
            return Fit(row);
        }

        protected override void Render(DockWidgetContext ctx, WeatherSnapshot wx, bool animate)
        {
            _glyph.Text = DockWidgetMath.WeatherGlyph(wx.Code, wx.IsDay);
            W.SetTextAnimated(_temp, Deg(ctx, wx.TempC), animate);
            _hl.Text = ctx.T($"Y {Deg(ctx, wx.HighC)}  D {Deg(ctx, wx.LowC)}", $"H {Deg(ctx, wx.HighC)}  L {Deg(ctx, wx.LowC)}");
        }

        public override void OnAttached(DockWidgetContext ctx)
        {
            _floating = true;
            W.FloatForever(_glyph, Math.Max(1, ctx.TileHeight * 0.045), 2.6);
        }

        public override void OnDetached()
        {
            if (_floating) StopFloat(_glyph);
            _floating = false;
        }
    }

    // ---- weather-details -------------------------------------------------------------------------

    private sealed class WeatherDetailsTile : WeatherTileBase
    {
        private TextBlock _wind = null!, _rain = null!, _low = null!, _high = null!;
        private BarGauge _bar = null!;
        private Ellipse _marker = null!;
        private double _barW, _markerSize;

        public WeatherDetailsTile(DockWidgetDescriptor d, DockWidgetContext c) : base(d, c) { }

        protected override FrameworkElement BuildMain(DockWidgetContext ctx)
        {
            var p = ctx.Palette;
            double h = ctx.TileHeight;
            double innerW = InnerWidth(Descriptor, h);
            double font = Math.Max(6.5, h * 0.2);
            _wind = W.Text(p, "", font, weight: FontWeights.SemiBold);
            _rain = W.Text(p, "", font, weight: FontWeights.SemiBold);
            var windLbl = W.Text(p, ctx.T("Rüzgâr ", "Wind "), font, p.SecondaryForeground);
            var rainGlyph = W.Glyph(p, RainGlyph, font * 1.05, p.SecondaryForeground);
            rainGlyph.Margin = new Thickness(0, 0, 2, 0);
            var top = new Grid();
            top.ColumnDefinitions.Add(new ColumnDefinition());
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var windRow = new StackPanel { Orientation = Orientation.Horizontal };
            windRow.Children.Add(windLbl);
            windRow.Children.Add(_wind);
            var rainRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            rainRow.Children.Add(rainGlyph);
            rainRow.Children.Add(_rain);
            Grid.SetColumn(rainRow, 1);
            top.Children.Add(windRow);
            top.Children.Add(rainRow);

            double small = Math.Max(6, h * 0.18);
            _low = W.Text(p, "", small, p.SecondaryForeground, FontWeights.SemiBold);
            _high = W.Text(p, "", small, p.SecondaryForeground, FontWeights.SemiBold);
            _low.TextTrimming = _high.TextTrimming = TextTrimming.None;
            double labelW = Math.Ceiling(small * 2.2);
            _low.Width = _high.Width = labelW;
            _low.TextAlignment = TextAlignment.Left;
            _high.TextAlignment = TextAlignment.Right;
            _barW = Math.Max(16, innerW - labelW * 2 - 6);
            double barH = Math.Max(3, h * 0.11);
            _markerSize = barH + Math.Max(2, h * 0.06);
            var accent = p.Accent;
            _bar = W.Bar(p, _barW, barH, accent);
            _bar.Fill = DockWidgetPalette.Freeze(new LinearGradientBrush(
                Color.FromArgb(0xFF, 0x4F, 0xA8, 0xFF), p.Warning, 0));
            _marker = new Ellipse
            {
                Width = _markerSize,
                Height = _markerSize,
                Fill = p.Foreground,
                Stroke = p.IsLight ? W.Brush(Colors.White) : W.Brush(Color.FromRgb(0x10, 0x12, 0x18)),
                StrokeThickness = 1
            };
            Canvas.SetTop(_marker, (barH - _markerSize) / 2);
            Canvas.SetLeft(_marker, -_markerSize / 2);
            var canvas = new Canvas { Width = _barW, Height = barH, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3, 0, 3, 0) };
            canvas.Children.Add(_bar);
            canvas.Children.Add(_marker);
            var bottom = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, Math.Max(1, h * 0.08), 0, 0) };
            bottom.Children.Add(_low);
            bottom.Children.Add(canvas);
            bottom.Children.Add(_high);
            var col = W.Column(top, bottom);
            col.Width = innerW;
            return Fit(col);
        }

        protected override void Render(DockWidgetContext ctx, WeatherSnapshot wx, bool animate)
        {
            W.SetTextAnimated(_wind, $"{wx.WindKmh.ToString("0", CultureInfo.InvariantCulture)} km/h", animate);
            string pct = ctx.En ? $"{wx.PrecipProbability}%" : $"%{wx.PrecipProbability}";
            W.SetTextAnimated(_rain, $"{wx.PrecipMm.ToString("0.#", CultureInfo.InvariantCulture)} mm · {pct}", animate);
            _low.Text = Deg(ctx, wx.LowC);
            _high.Text = Deg(ctx, wx.HighC);
            double lo = Math.Min(wx.LowC, wx.TempC), hi = Math.Max(wx.HighC, wx.TempC);
            double frac = hi - lo < 0.01 ? 0.5 : (wx.TempC - lo) / (hi - lo);
            frac = Math.Clamp(frac, 0, 1);
            _bar.Value = 1;
            double target = frac * _barW - _markerSize / 2;
            double from = animate ? Canvas.GetLeft(_marker) : -_markerSize / 2;
            if (double.IsNaN(from)) from = -_markerSize / 2;
            _marker.BeginAnimation(Canvas.LeftProperty, new DoubleAnimation(from, target, TimeSpan.FromMilliseconds(animate ? 600 : 900))
            {
                EasingFunction = Motion.Soft
            });
        }
    }

    // ---- forecast --------------------------------------------------------------------------------

    private sealed class ForecastTile : WeatherTileBase
    {
        private readonly TextBlock[] _names = new TextBlock[4];
        private readonly TextBlock[] _glyphs = new TextBlock[4];
        private readonly TextBlock[] _temps = new TextBlock[4];
        private readonly FrameworkElement[] _cols = new FrameworkElement[4];

        public ForecastTile(DockWidgetDescriptor d, DockWidgetContext c) : base(d, c) { }

        protected override FrameworkElement BuildMain(DockWidgetContext ctx)
        {
            var p = ctx.Palette;
            double h = ctx.TileHeight;
            var g = new Grid();
            for (int i = 0; i < 4; i++)
            {
                g.ColumnDefinitions.Add(new ColumnDefinition());
                _names[i] = W.Text(p, "", Math.Max(6, h * 0.17), i == 0 ? p.AccentBrush : p.SecondaryForeground, FontWeights.SemiBold);
                _glyphs[i] = W.Glyph(p, "", Math.Max(8, h * 0.3), p.Foreground);
                _temps[i] = W.Text(p, "", Math.Max(6, h * 0.17), weight: FontWeights.SemiBold);
                _names[i].HorizontalAlignment = _temps[i].HorizontalAlignment = HorizontalAlignment.Center;
                _names[i].TextTrimming = _temps[i].TextTrimming = TextTrimming.None;
                var col = W.Column(_names[i], _glyphs[i], _temps[i]);
                var fit = Fit(col);
                fit.Margin = new Thickness(1, 0, 1, 0);
                _cols[i] = fit;
                Grid.SetColumn(fit, i);
                g.Children.Add(fit);
            }
            return g;
        }

        protected override void Render(DockWidgetContext ctx, WeatherSnapshot wx, bool animate)
        {
            var today = ctx.Now.Date;
            var days = wx.Days.Where(x => x.Date.Date >= today).Take(4).ToList();
            for (int i = 0; i < 4; i++)
            {
                if (i < days.Count)
                {
                    var d = days[i];
                    _names[i].Text = ShortDay(ctx, d.Date, today);
                    _glyphs[i].Text = DockWidgetMath.WeatherGlyph(d.Code, i != 0 || wx.IsDay);
                    _temps[i].Text = $"{Deg(ctx, d.HighC)}/{Deg(ctx, d.LowC)}";
                    _cols[i].Visibility = Visibility.Visible;
                }
                else
                {
                    _cols[i].Visibility = Visibility.Hidden;
                }
            }
            if (animate) StaggerIn(_cols);
        }

        public override void OnAttached(DockWidgetContext ctx) => StaggerIn(_cols);
    }

    // ---- weather-status --------------------------------------------------------------------------

    private sealed class WeatherStatusTile : WeatherTileBase
    {
        private TextBlock _glyph = null!, _cond = null!, _city = null!;

        public WeatherStatusTile(DockWidgetDescriptor d, DockWidgetContext c) : base(d, c) { }

        protected override FrameworkElement BuildMain(DockWidgetContext ctx)
        {
            var p = ctx.Palette;
            double h = ctx.TileHeight;
            double innerW = InnerWidth(Descriptor, h);
            _glyph = W.Glyph(p, "", Math.Max(10, h * 0.42), p.AccentBrush);
            _glyph.Margin = new Thickness(0, 0, Math.Max(3, h * 0.14), 0);
            _cond = W.Text(p, "", Math.Max(8, h * 0.27), weight: FontWeights.SemiBold);
            _city = W.Text(p, "", Math.Max(6.5, h * 0.19), p.SecondaryForeground);
            _city.Margin = new Thickness(0, -h * 0.03, 0, 0);
            var text = W.Column(_cond, _city);
            var dp = new DockPanel { LastChildFill = true, Width = innerW, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(_glyph, Dock.Left);
            dp.Children.Add(_glyph);
            dp.Children.Add(text);
            return dp;
        }

        protected override void Render(DockWidgetContext ctx, WeatherSnapshot wx, bool animate)
        {
            string glyph = DockWidgetMath.WeatherGlyph(wx.Code, wx.IsDay);
            string cond = $"{DockWidgetMath.WeatherText(wx.Code, ctx.Lang)} · {Deg(ctx, wx.TempC)}";
            string city = string.IsNullOrWhiteSpace(wx.City) ? "—" : wx.City;
            if (animate)
            {
                CrossFade(_glyph, glyph);
                CrossFade(_cond, cond);
                CrossFade(_city, city);
            }
            else
            {
                _glyph.Text = glyph;
                _cond.Text = cond;
                _city.Text = city;
            }
        }
    }

    // ---- hourly ----------------------------------------------------------------------------------

    private sealed class HourlyTile : WeatherTileBase
    {
        private readonly TextBlock[] _times = new TextBlock[6];
        private readonly TextBlock[] _glyphs = new TextBlock[6];
        private readonly TextBlock[] _temps = new TextBlock[6];
        private readonly FrameworkElement[] _cols = new FrameworkElement[6];
        private Sparkline _spark = null!;
        private RectangleGeometry _sparkClip = null!;
        private double _w, _sparkH;

        public HourlyTile(DockWidgetDescriptor d, DockWidgetContext c) : base(d, c) { }

        protected override FrameworkElement BuildMain(DockWidgetContext ctx)
        {
            var p = ctx.Palette;
            double h = ctx.TileHeight;
            _w = InnerWidth(Descriptor, h);
            double innerH = InnerHeight(h);
            _sparkH = Math.Round(innerH * 0.55);
            _sparkClip = new RectangleGeometry(new Rect(0, 0, _w, _sparkH));
            _spark = new Sparkline
            {
                Width = _w,
                Height = _sparkH,
                Stroke = W.Brush(p.Accent),
                StrokeThickness = Math.Max(1, h * 0.04),
                ShowHead = false,
                FillArea = true,
                Opacity = 0.55,
                VerticalAlignment = VerticalAlignment.Center,
                Clip = _sparkClip,
                IsHitTestVisible = false
            };
            var cols = new Grid { Width = _w, Height = innerH };
            for (int i = 0; i < 6; i++)
            {
                cols.ColumnDefinitions.Add(new ColumnDefinition());
                _times[i] = W.Text(p, "", Math.Max(6, h * 0.17), p.SecondaryForeground, FontWeights.SemiBold);
                _glyphs[i] = W.Glyph(p, "", Math.Max(7, h * 0.26), p.Foreground);
                _temps[i] = W.Text(p, "", Math.Max(6, h * 0.18), weight: FontWeights.SemiBold);
                _times[i].HorizontalAlignment = _temps[i].HorizontalAlignment = HorizontalAlignment.Center;
                _times[i].TextTrimming = _temps[i].TextTrimming = TextTrimming.None;
                var fit = Fit(W.Column(_times[i], _glyphs[i], _temps[i]));
                _cols[i] = fit;
                Grid.SetColumn(fit, i);
                cols.Children.Add(fit);
            }
            var root = new Grid { Width = _w, Height = innerH };
            root.Children.Add(_spark);
            root.Children.Add(cols);
            return root;
        }

        protected override void Render(DockWidgetContext ctx, WeatherSnapshot wx, bool animate)
        {
            var hours = wx.Hours.Take(6).ToList();
            var values = new List<double>(hours.Count);
            bool h24 = ctx.Config.DockClock24h;
            for (int i = 0; i < 6; i++)
            {
                if (i < hours.Count)
                {
                    var hr = hours[i];
                    _times[i].Text = h24
                        ? hr.Time.ToString("HH", CultureInfo.InvariantCulture)
                        : hr.Time.ToString("%h", CultureInfo.InvariantCulture) + DockWidgetMath.AmPm(hr.Time)[..1];
                    bool day = hr.Time.TimeOfDay >= wx.Sunrise.TimeOfDay && hr.Time.TimeOfDay <= wx.Sunset.TimeOfDay;
                    _glyphs[i].Text = DockWidgetMath.WeatherGlyph(hr.Code, day);
                    _temps[i].Text = Deg(ctx, hr.TempC);
                    _cols[i].Visibility = Visibility.Visible;
                    values.Add(ToDisplayTemp(hr.TempC, ctx.Config.WeatherFahrenheit));
                }
                else
                {
                    _cols[i].Visibility = Visibility.Hidden;
                }
            }
            _spark.SetValues(values);
            if (animate) DrawIn();
        }

        private void DrawIn()
        {
            _sparkClip.BeginAnimation(RectangleGeometry.RectProperty, new RectAnimation(
                new Rect(0, 0, 0, _sparkH), new Rect(0, 0, _w, _sparkH), TimeSpan.FromMilliseconds(900))
            {
                BeginTime = TimeSpan.FromMilliseconds(150),
                EasingFunction = Motion.Gentle
            });
        }

        public override void OnAttached(DockWidgetContext ctx)
        {
            StaggerIn(_cols, 50);
            DrawIn();
        }
    }

    // ---- sun -------------------------------------------------------------------------------------

    /// <summary>Half-ellipse arc from sunrise (left) to sunset (right) with a sun dot at <see cref="Progress"/>.</summary>
    private sealed class SunArc : FrameworkElement
    {
        public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
            nameof(Progress), typeof(double), typeof(SunArc),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

        public double Progress
        {
            get => (double)GetValue(ProgressProperty);
            set => SetValue(ProgressProperty, value);
        }

        public Brush Track { get; init; } = Brushes.Gray;
        public Brush Trail { get; init; } = Brushes.Orange;
        public Color SunColor { get; init; } = Colors.Orange;
        public double Thickness { get; init; } = 1.5;

        private double _target = double.NaN;

        public void AnimateTo(double value, bool fromStart)
        {
            value = Math.Clamp(double.IsFinite(value) ? value : 0, 0, 1);
            if (!fromStart && !double.IsNaN(_target) && Math.Abs(value - _target) < 0.002) return;
            _target = value;
            var anim = new DoubleAnimation(value, TimeSpan.FromMilliseconds(fromStart ? 1400 : 800))
            {
                EasingFunction = Motion.Gentle
            };
            if (fromStart) anim.From = 0;
            BeginAnimation(ProgressProperty, anim, HandoffBehavior.SnapshotAndReplace);
        }

        private (Point Center, double Rx, double Ry, double Base) Geometry()
        {
            double w = ActualWidth, h = ActualHeight;
            double r = Thickness * 2.6;
            double baseY = h - Thickness;
            return (new Point(w / 2, baseY), Math.Max(1, w / 2 - r), Math.Max(1, baseY - r), baseY);
        }

        private static Point On((Point Center, double Rx, double Ry, double Base) g, double p)
        {
            double th = Math.PI * (1 - p);
            return new Point(g.Center.X + g.Rx * Math.Cos(th), g.Center.Y - g.Ry * Math.Sin(th));
        }

        protected override void OnRender(DrawingContext dc)
        {
            if (ActualWidth < 6 || ActualHeight < 6) return;
            var g = Geometry();
            var horizon = new Pen(Track, Math.Max(0.6, Thickness * 0.6)) { DashStyle = DashStyles.Dash };
            dc.DrawLine(horizon, new Point(0, g.Base), new Point(ActualWidth, g.Base));

            var trackPen = new Pen(Track, Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            dc.DrawGeometry(null, trackPen, ArcGeometry(g, 0, 1));
            double p = Math.Clamp(Progress, 0, 1);
            if (p > 0.005)
            {
                var trailPen = new Pen(Trail, Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                dc.DrawGeometry(null, trailPen, ArcGeometry(g, 0, p));
            }
            var sun = On(g, p);
            var glow = new RadialGradientBrush(Color.FromArgb(0x90, SunColor.R, SunColor.G, SunColor.B), Color.FromArgb(0, SunColor.R, SunColor.G, SunColor.B));
            glow.Freeze();
            dc.DrawEllipse(glow, null, sun, Thickness * 3.2, Thickness * 3.2);
            var core = new SolidColorBrush(SunColor);
            core.Freeze();
            dc.DrawEllipse(core, null, sun, Thickness * 1.7, Thickness * 1.7);
        }

        private static StreamGeometry ArcGeometry((Point Center, double Rx, double Ry, double Base) g, double from, double to)
        {
            var sg = new StreamGeometry();
            using (var c = sg.Open())
            {
                c.BeginFigure(On(g, from), false, false);
                const int steps = 32;
                for (int i = 1; i <= steps; i++) c.LineTo(On(g, from + (to - from) * i / steps), true, true);
            }
            sg.Freeze();
            return sg;
        }
    }

    private sealed class SunTile : WeatherTileBase
    {
        private SunArc _arc = null!;
        private TextBlock _rise = null!, _set = null!, _nightLabel = null!, _nightTime = null!;
        private FrameworkElement _dayView = null!, _nightView = null!;
        private bool? _isNight;
        private bool _attachedGlide;
        private StackPanel? _cardBody;
        private object? _cardKey;

        public SunTile(DockWidgetDescriptor d, DockWidgetContext c) : base(d, c) { }

        protected override FrameworkElement BuildMain(DockWidgetContext ctx)
        {
            var p = ctx.Palette;
            double h = ctx.TileHeight;
            double innerW = InnerWidth(Descriptor, h);
            double innerH = InnerHeight(h);
            double small = Math.Max(6, h * 0.17);
            _arc = new SunArc
            {
                Width = innerW,
                Height = Math.Max(8, innerH - small * 1.3),
                Track = p.Track,
                Trail = W.Brush(p.Warning, 0xC0),
                SunColor = p.Warning,
                Thickness = Math.Max(1.1, h * 0.045)
            };
            _rise = W.Text(p, "", small, p.SecondaryForeground, FontWeights.SemiBold);
            _set = W.Text(p, "", small, p.SecondaryForeground, FontWeights.SemiBold);
            _rise.TextTrimming = _set.TextTrimming = TextTrimming.None;
            _set.HorizontalAlignment = HorizontalAlignment.Right;
            var times = new Grid { Width = innerW };
            times.Children.Add(_rise);
            times.Children.Add(_set);
            var day = W.Column(_arc, times);
            _dayView = day;

            var moon = W.Glyph(p, MoonGlyph, Math.Max(10, h * 0.42), p.SecondaryForeground);
            moon.Margin = new Thickness(0, 0, Math.Max(3, h * 0.12), 0);
            _nightLabel = W.Text(p, "", Math.Max(6, h * 0.18), p.SecondaryForeground);
            _nightTime = W.Text(p, "", Math.Max(9, h * 0.32), weight: FontWeights.SemiBold);
            _nightTime.Margin = new Thickness(0, -h * 0.03, 0, 0);
            var nightRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            nightRow.Children.Add(moon);
            nightRow.Children.Add(W.Column(_nightLabel, _nightTime));
            _nightView = Fit(nightRow);
            _nightView.Visibility = Visibility.Collapsed;

            var g = new Grid();
            g.Children.Add(_dayView);
            g.Children.Add(_nightView);
            return g;
        }

        protected override void Render(DockWidgetContext ctx, WeatherSnapshot wx, bool animate)
        {
            _rise.Text = Clock(ctx, wx.Sunrise);
            _set.Text = Clock(ctx, wx.Sunset);
            _nightLabel.Text = ctx.T("Gün doğumu", "Sunrise");
            _isNight = null;
        }

        private static (DateTime Rise, DateTime Set) TodayTimes(DateTime now, WeatherSnapshot wx) =>
            (now.Date + wx.Sunrise.TimeOfDay, now.Date + wx.Sunset.TimeOfDay);

        protected override void Tick(DockWidgetContext ctx, WeatherSnapshot wx)
        {
            var (rise, set) = TodayTimes(ctx.Now, wx);
            double? prog = DockWidgetMath.SunProgress(ctx.Now, rise, set);
            bool night = prog is null;
            if (night != _isNight)
            {
                _isNight = night;
                _dayView.Visibility = night ? Visibility.Collapsed : Visibility.Visible;
                _nightView.Visibility = night ? Visibility.Visible : Visibility.Collapsed;
                _nightTime.Text = Clock(ctx, wx.Sunrise);
                if (!night)
                {
                    _arc.AnimateTo(prog ?? 0, fromStart: true);
                    _attachedGlide = true;
                    return;
                }
            }
            if (prog is double pv)
            {
                _arc.AnimateTo(pv, fromStart: !_attachedGlide);
                _attachedGlide = true;
            }
        }

        public override void OnAttached(DockWidgetContext ctx)
        {
            var wx = Weather(ctx);
            if (wx is null) return;
            var (rise, set) = TodayTimes(ctx.Now, wx);
            if (DockWidgetMath.SunProgress(ctx.Now, rise, set) is double pv)
            {
                _arc.AnimateTo(pv, fromStart: true);
                _attachedGlide = true;
            }
        }

        protected override FrameworkElement? CreateCard(DockWidgetContext ctx)
        {
            _cardBody = new StackPanel();
            _cardKey = new object();
            return W.Card(ctx.Palette, Descriptor.Glyph, Descriptor.Name(ctx.Lang), _cardBody, 270);
        }

        protected override void RefreshCard(DockWidgetContext ctx)
        {
            if (_cardBody is null) return;
            var wx = Weather(ctx);
            var p = ctx.Palette;
            object key = wx is null ? NoDataText(ctx) : (wx, ctx.Config.DockClock24h, ctx.Now.Hour, ctx.Now.Minute);
            if (Equals(key, _cardKey)) return;
            _cardKey = key;
            _cardBody.Children.Clear();
            if (wx is null)
            {
                var t = W.Text(p, NoDataText(ctx), 13, p.SecondaryForeground, ui: false);
                t.TextWrapping = TextWrapping.Wrap;
                _cardBody.Children.Add(t);
                return;
            }
            var (rise, set) = TodayTimes(ctx.Now, wx);
            _cardBody.Children.Add(W.CardRow(p, ctx.T("Gün doğumu", "Sunrise"), Clock(ctx, wx.Sunrise), null, SunGlyph));
            _cardBody.Children.Add(W.CardRow(p, ctx.T("Gün batımı", "Sunset"), Clock(ctx, wx.Sunset), null, MoonGlyph));
            _cardBody.Children.Add(W.CardRow(p, ctx.T("Gün uzunluğu", "Day length"), DockWidgetMath.FormatDuration(set - rise, ctx.Lang)));
            string remaining = ctx.Now < rise
                ? ctx.T($"Doğuma {DockWidgetMath.FormatDuration(rise - ctx.Now, ctx.Lang)}", $"{DockWidgetMath.FormatDuration(rise - ctx.Now, ctx.Lang)} to sunrise")
                : ctx.Now > set
                    ? ctx.T("Güneş battı", "The sun has set")
                    : ctx.T($"Batıma {DockWidgetMath.FormatDuration(set - ctx.Now, ctx.Lang)}", $"{DockWidgetMath.FormatDuration(set - ctx.Now, ctx.Lang)} to sunset");
            _cardBody.Children.Add(W.CardRow(p, ctx.T("Durum", "Now"), remaining, p.AccentBrush));
            if (!string.IsNullOrWhiteSpace(wx.City))
            {
                var city = W.Text(p, wx.City, 11.5, p.TertiaryForeground, ui: false);
                city.Margin = new Thickness(0, 6, 0, 0);
                _cardBody.Children.Add(city);
            }
        }
    }
}
