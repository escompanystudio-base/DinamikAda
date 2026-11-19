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
        double pad = u > 1.05 ? Math.Round(h * 0.26) : 2;
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
        private SkyKind? _sky;

        protected WeatherTileBase(DockWidgetDescriptor d, DockWidgetContext c) : base(d, c) { }

        protected override bool VividByDefault => true;

        protected abstract FrameworkElement BuildMain(DockWidgetContext ctx);
        protected abstract void Render(DockWidgetContext ctx, WeatherSnapshot wx, bool animate);
        /// <summary>Cheap per-second updates (e.g. the sun position); default none.</summary>
        protected virtual void Tick(DockWidgetContext ctx, WeatherSnapshot wx) { }

        protected bool HasRendered { get; private set; }

        protected override FrameworkElement BuildContent(DockWidgetContext ctx)
        {
            var p = Vivid.On(ctx.Palette);
            double h = ctx.TileHeight;
            _main = BuildMain(ctx);
            _empty = W.Text(p, "", Math.Max(7, h * 0.22), p.SecondaryForeground, ui: false);
            _empty.TextWrapping = TextWrapping.Wrap;
            _empty.MaxHeight = InnerHeight(h);
            _empty.TextAlignment = TextAlignment.Center;
            _empty.HorizontalAlignment = HorizontalAlignment.Center;
            _empty.Visibility = Visibility.Collapsed;
            var g = new Grid { ClipToBounds = false };
            g.Children.Add(_main);
            g.Children.Add(_empty);
            return g;
        }

        protected override void Refresh(DockWidgetContext ctx)
        {
            var wx = Weather(ctx);
            var sky = wx is null ? SkyKind.Cloudy : SkyOf(ctx, wx);
            if (sky != _sky)
            {
                SetTileBackground(SkyBrush(sky), animate: _sky is not null);
                _sky = sky;
            }
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

    private static readonly Dictionary<SkyKind, Brush> SkyBrushes = new();

    private static Brush SkyBrush(SkyKind kind)
    {
        if (!SkyBrushes.TryGetValue(kind, out var b)) SkyBrushes[kind] = b = Vivid.Sky(SkyGradient.Stops(kind));
        return b;
    }

    /// <summary>The sky right now, using today's sunrise and sunset for golden hour and the sunset glow.</summary>
    private static SkyKind SkyOf(DockWidgetContext ctx, WeatherSnapshot wx) =>
        SkyGradient.Classify(wx.Code, wx.IsDay, ctx.Now, ctx.Now.Date + wx.Sunrise.TimeOfDay, ctx.Now.Date + wx.Sunset.TimeOfDay);

    private static bool IsDayAt(WeatherSnapshot wx, DateTime t) =>
        t.TimeOfDay >= wx.Sunrise.TimeOfDay && t.TimeOfDay <= wx.Sunset.TimeOfDay;

    /// <summary>
    /// The weather card: a big temperature with the glossy icon, the next hours as a row of icons, and a five-day
    /// list with temperature range bars, then the details.
    /// </summary>
    private sealed class WeatherCard
    {
        private const double Width = 312;
        private StackPanel _body = null!;
        private object? _key = new();

        public FrameworkElement Build(DockWidgetContext ctx, DockWidgetDescriptor d)
        {
            _body = new StackPanel { Width = Width };
            return _body;
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

            // Hero: sky-tinted panel with city, condition, big temperature and the icon.
            var sky = SkyOf(ctx, wx);
            var on = Vivid.On(p);
            var city = W.Text(on, string.IsNullOrWhiteSpace(wx.City) ? ctx.T("Hava Durumu", "Weather") : wx.City, 15, weight: FontWeights.SemiBold);
            var temp = W.Text(on, Deg(ctx, wx.TempC), 48, weight: FontWeights.Bold);
            temp.Margin = new Thickness(-2, -4, 0, -4);
            var cond = W.Text(on, DockWidgetMath.WeatherText(wx.Code, ctx.Lang), 13, on.SecondaryForeground, FontWeights.SemiBold, ui: false);
            var hl = W.Text(on, ctx.T($"Y {Deg(ctx, wx.HighC)}  D {Deg(ctx, wx.LowC)}", $"H {Deg(ctx, wx.HighC)}  L {Deg(ctx, wx.LowC)}"), 12, on.SecondaryForeground, ui: false);
            var left = W.Column(city, temp, cond, hl);
            var icon = new WeatherIcon(84, SkyGradient.Icon(wx.Code, wx.IsDay))
            {
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, -6, -4, 0)
            };
            var heroGrid = new Grid();
            heroGrid.Children.Add(left);
            heroGrid.Children.Add(icon);
            var hero = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            hero.Children.Add(new Border { CornerRadius = new CornerRadius(16), Background = SkyBrush(sky) });
            hero.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(16),
                Background = DockWidgetPalette.Freeze(new LinearGradientBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF), Color.FromArgb(0, 0xFF, 0xFF, 0xFF), new Point(0, 0), new Point(0, 0.5))),
                BorderBrush = W.Brush(Colors.White, 0x22),
                BorderThickness = new Thickness(0.8)
            });
            hero.Children.Add(new Border { Padding = new Thickness(16, 12, 14, 12), Child = heroGrid });
            _body.Children.Add(hero);
            W.PopIn(hero);

            // Hourly row.
            var hours = wx.Hours.Take(6).ToList();
            if (hours.Count > 0)
            {
                _body.Children.Add(Section(p, ctx.T("SAATLİK", "HOURLY")));
                var row = new Grid { Margin = new Thickness(0, 0, 0, 12) };
                for (int i = 0; i < hours.Count; i++)
                {
                    row.ColumnDefinitions.Add(new ColumnDefinition());
                    var hr = hours[i];
                    string label = i == 0 ? ctx.T("Şimdi", "Now") : ctx.Config.DockClock24h
                        ? hr.Time.ToString("HH", CultureInfo.InvariantCulture)
                        : hr.Time.ToString("%h", CultureInfo.InvariantCulture) + DockWidgetMath.AmPm(hr.Time);
                    var t = W.Text(p, label, 11, p.SecondaryForeground, FontWeights.SemiBold);
                    var ic = new WeatherIcon(26, SkyGradient.Icon(hr.Code, IsDayAt(wx, hr.Time))) { Margin = new Thickness(0, 4, 0, 4) };
                    var v = W.Text(p, Deg(ctx, hr.TempC), 13, weight: FontWeights.SemiBold);
                    t.HorizontalAlignment = v.HorizontalAlignment = ic.HorizontalAlignment = HorizontalAlignment.Center;
                    t.TextTrimming = v.TextTrimming = TextTrimming.None;
                    var col = W.Column(t, ic, v);
                    Grid.SetColumn(col, i);
                    row.Children.Add(col);
                    W.PopIn(col, 18 * (i + 1));
                }
                _body.Children.Add(row);
            }

            // Five-day list with range bars on a shared scale.
            var days = wx.Days.Where(x => x.Date.Date >= ctx.Now.Date).Take(5).ToList();
            if (days.Count > 0)
            {
                _body.Children.Add(Section(p, ctx.T("5 GÜN", "5 DAYS")));
                double min = days.Min(x => x.LowC), max = days.Max(x => x.HighC);
                if (max - min < 1) max = min + 1;
                for (int i = 0; i < days.Count; i++)
                {
                    var day = days[i];
                    var g = new Grid { Margin = new Thickness(0, 2, 0, 2), Height = 26 };
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(62) });
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
                    var name = W.Text(p, i == 0 ? ctx.T("Bugün", "Today") : day.Date.ToString("ddd", Cul(ctx)), 13, p.Foreground, FontWeights.SemiBold);
                    var ic = new WeatherIcon(22, SkyGradient.Icon(day.Code, true));
                    var lo = W.Text(p, Deg(ctx, day.LowC), 13, p.SecondaryForeground, FontWeights.SemiBold);
                    lo.HorizontalAlignment = HorizontalAlignment.Right;
                    var hi = W.Text(p, Deg(ctx, day.HighC), 13, p.Foreground, FontWeights.SemiBold);
                    hi.HorizontalAlignment = HorizontalAlignment.Right;
                    var bar = new RangeBar((day.LowC - min) / (max - min), (day.HighC - min) / (max - min), p.Track)
                    {
                        Height = 5,
                        Margin = new Thickness(10, 0, 4, 0),
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    Grid.SetColumn(ic, 1);
                    Grid.SetColumn(lo, 2);
                    Grid.SetColumn(bar, 3);
                    Grid.SetColumn(hi, 4);
                    g.Children.Add(name);
                    g.Children.Add(ic);
                    g.Children.Add(lo);
                    g.Children.Add(bar);
                    g.Children.Add(hi);
                    _body.Children.Add(g);
                    W.PopIn(g, 18 * (i + 7));
                }
            }

            // Details.
            var details = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
            details.Children.Add(W.CardRow(p, ctx.T("Hissedilen", "Feels like"), DegUnit(ctx, wx.FeelsLikeC)));
            details.Children.Add(W.CardRow(p, ctx.T("Nem · Rüzgâr", "Humidity · Wind"),
                (ctx.En ? $"{wx.Humidity}%" : $"%{wx.Humidity}") + $" · {wx.WindKmh.ToString("0", CultureInfo.InvariantCulture)} km/h"));
            details.Children.Add(W.CardRow(p, ctx.T("Gün doğumu / batımı", "Sunrise / sunset"), $"{Clock(ctx, wx.Sunrise)} / {Clock(ctx, wx.Sunset)}"));
            _body.Children.Add(new Border { Height = 1, Background = p.Track, Opacity = 0.6, Margin = new Thickness(0, 8, 0, 0) });
            _body.Children.Add(details);
            var upd = W.Text(p, ctx.T($"Güncellendi {Clock(ctx, wx.FetchedLocal)}", $"Updated {Clock(ctx, wx.FetchedLocal)}"), 11, p.TertiaryForeground, ui: false);
            upd.Margin = new Thickness(0, 6, 0, 0);
            _body.Children.Add(upd);
        }

        private static TextBlock Section(DockWidgetPalette p, string text)
        {
            var t = W.Text(p, text, 10.5, p.TertiaryForeground, FontWeights.SemiBold, ui: false);
            t.Margin = new Thickness(1, 0, 0, 6);
            return t;
        }
    }

    /// <summary>A temperature range bar: a cool-to-warm gradient segment from <c>lo</c> to <c>hi</c> (0..1) on a track.</summary>
    private sealed class RangeBar : FrameworkElement
    {
        private readonly double _lo, _hi;
        private readonly Brush _track;
        private static readonly Brush Warm = DockWidgetPalette.Freeze(new LinearGradientBrush(
            new GradientStopCollection
            {
                new(Color.FromRgb(0x5A, 0xC8, 0xFA), 0.0),
                new(Color.FromRgb(0xA6, 0xE2, 0x6A), 0.45),
                new(Color.FromRgb(0xFF, 0xC4, 0x3D), 0.75),
                new(Color.FromRgb(0xFF, 0x8A, 0x3D), 1.0)
            }, 0));

        public RangeBar(double lo, double hi, Brush track)
        {
            _lo = Math.Clamp(lo, 0, 1);
            _hi = Math.Clamp(Math.Max(hi, lo), 0, 1);
            _track = track;
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w < 4 || h < 1) return;
            double r = h / 2;
            dc.DrawRoundedRectangle(_track, null, new Rect(0, 0, w, h), r, r);
            double x0 = _lo * w, x1 = Math.Max(x0 + h, _hi * w);
            dc.PushClip(new RectangleGeometry(new Rect(x0, 0, x1 - x0, h), r, r));
            dc.DrawRectangle(Warm, null, new Rect(0, 0, w, h));
            dc.Pop();
        }
    }

    // ---- weather ---------------------------------------------------------------------------------

    /// <summary>
    /// The sky tile: a large glossy icon that rises a little out of the tile, condition over city, and a big
    /// temperature on the right.
    /// </summary>
    private sealed class WeatherNowTile : WeatherTileBase
    {
        private WeatherIcon _icon = null!;
        private TextBlock _temp = null!, _cond = null!, _city = null!;
        private bool _floating;

        public WeatherNowTile(DockWidgetDescriptor d, DockWidgetContext c) : base(d, c) { }

        protected override Thickness ContentPadding(double h) => new(Math.Round(h * 0.06), 0, Math.Round(h * 0.26), 0);

        protected override FrameworkElement BuildMain(DockWidgetContext ctx)
        {
            var p = Vivid.On(ctx.Palette);
            double h = ctx.TileHeight;
            double iconSize = Math.Round(h * 1.12);
            _icon = new WeatherIcon(iconSize)
            {
                Margin = new Thickness(0, -Math.Round(h * 0.26), Math.Round(h * 0.02), -Math.Round(h * 0.12)),
                VerticalAlignment = VerticalAlignment.Center
            };
            _cond = W.Text(p, "", Math.Max(8, Math.Round(h * 0.25)), p.Foreground, FontWeights.SemiBold);
            _city = W.Text(p, "", Vivid.LabelSize(h), p.SecondaryForeground, FontWeights.Medium, ui: false);
            var text = W.Column(_cond, _city);
            text.Margin = new Thickness(0, 0, Math.Round(h * 0.1), 0);
            _temp = Vivid.Value(p, "", h, p.Foreground, 1.12);
            _temp.TextTrimming = TextTrimming.None;
            var g = new Grid { ClipToBounds = false };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(_icon);
            Grid.SetColumn(text, 1);
            g.Children.Add(text);
            Grid.SetColumn(_temp, 2);
            g.Children.Add(_temp);
            return g;
        }

        protected override void Render(DockWidgetContext ctx, WeatherSnapshot wx, bool animate)
        {
            _icon.Kind = SkyGradient.Icon(wx.Code, wx.IsDay);
            W.SetTextAnimated(_temp, Deg(ctx, wx.TempC), animate);
            if (animate) CrossFade(_cond, DockWidgetMath.WeatherText(wx.Code, ctx.Lang));
            else _cond.Text = DockWidgetMath.WeatherText(wx.Code, ctx.Lang);
            _city.Text = string.IsNullOrWhiteSpace(wx.City)
                ? ctx.T($"Y {Deg(ctx, wx.HighC)}  D {Deg(ctx, wx.LowC)}", $"H {Deg(ctx, wx.HighC)}  L {Deg(ctx, wx.LowC)}")
                : wx.City;
        }

        public override void OnAttached(DockWidgetContext ctx)
        {
            _floating = true;
            FloatIcon(_icon, ctx.TileHeight);
        }

        public override void OnDetached()
        {
            if (_floating) StopFloat(_icon);
            _floating = false;
        }
    }

    /// <summary>A slow, tiny float that keeps the icon's layout margin (it overflows the tile).</summary>
    private static void FloatIcon(FrameworkElement el, double h) => W.FloatForever(el, Math.Max(0.8, h * 0.035), 3.2);

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
            var p = Vivid.On(ctx.Palette);
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
                Color.FromArgb(0xFF, 0x9B, 0xE2, 0xFF), Color.FromArgb(0xFF, 0xFF, 0xC4, 0x3D), 0));
            _marker = new Ellipse
            {
                Width = _markerSize,
                Height = _markerSize,
                Fill = p.Foreground,
                Stroke = W.Brush(Color.FromArgb(0x60, 0x00, 0x10, 0x30)),
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
        private readonly WeatherIcon[] _glyphs = new WeatherIcon[4];
        private readonly TextBlock[] _temps = new TextBlock[4];
        private readonly FrameworkElement[] _cols = new FrameworkElement[4];

        public ForecastTile(DockWidgetDescriptor d, DockWidgetContext c) : base(d, c) { }

        protected override FrameworkElement BuildMain(DockWidgetContext ctx)
        {
            var p = Vivid.On(ctx.Palette);
            double h = ctx.TileHeight;
            var g = new Grid();
            for (int i = 0; i < 4; i++)
            {
                g.ColumnDefinitions.Add(new ColumnDefinition());
                _names[i] = W.Text(p, "", Math.Max(6.5, Math.Round(h * 0.2)), i == 0 ? p.Foreground : p.SecondaryForeground, FontWeights.SemiBold);
                _glyphs[i] = new WeatherIcon(Math.Max(9, Math.Round(h * 0.36))) { HorizontalAlignment = HorizontalAlignment.Center };
                _temps[i] = W.Text(p, "", Math.Max(6.5, Math.Round(h * 0.2)), weight: FontWeights.Bold);
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
                    _glyphs[i].Kind = SkyGradient.Icon(d.Code, i != 0 || wx.IsDay);
                    _temps[i].Text = Deg(ctx, d.HighC);
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
        private WeatherIcon _glyph = null!;
        private TextBlock _cond = null!, _city = null!;

        public WeatherStatusTile(DockWidgetDescriptor d, DockWidgetContext c) : base(d, c) { }

        protected override FrameworkElement BuildMain(DockWidgetContext ctx)
        {
            var p = Vivid.On(ctx.Palette);
            double h = ctx.TileHeight;
            double innerW = InnerWidth(Descriptor, h);
            _glyph = new WeatherIcon(Math.Round(h * 0.9)) { Margin = new Thickness(-Math.Round(h * 0.12), -Math.Round(h * 0.16), Math.Max(3, h * 0.1), -Math.Round(h * 0.08)) };
            _cond = W.Text(p, "", Math.Max(8, Math.Round(h * 0.28)), weight: FontWeights.Bold);
            _city = W.Text(p, "", Vivid.LabelSize(h), p.SecondaryForeground, FontWeights.Medium, ui: false);
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
            _glyph.Kind = SkyGradient.Icon(wx.Code, wx.IsDay);
            string cond = $"{DockWidgetMath.WeatherText(wx.Code, ctx.Lang)} · {Deg(ctx, wx.TempC)}";
            string city = string.IsNullOrWhiteSpace(wx.City) ? "—" : wx.City;
            if (animate)
            {
                CrossFade(_cond, cond);
                CrossFade(_city, city);
            }
            else
            {
                _cond.Text = cond;
                _city.Text = city;
            }
        }
    }

    // ---- hourly ----------------------------------------------------------------------------------

    private sealed class HourlyTile : WeatherTileBase
    {
        private readonly TextBlock[] _times = new TextBlock[6];
        private readonly WeatherIcon[] _glyphs = new WeatherIcon[6];
        private readonly TextBlock[] _temps = new TextBlock[6];
        private readonly FrameworkElement[] _cols = new FrameworkElement[6];
        private Sparkline _spark = null!;
        private RectangleGeometry _sparkClip = null!;
        private double _w, _sparkH;

        public HourlyTile(DockWidgetDescriptor d, DockWidgetContext c) : base(d, c) { }

        protected override FrameworkElement BuildMain(DockWidgetContext ctx)
        {
            var p = Vivid.On(ctx.Palette);
            double h = ctx.TileHeight;
            _w = InnerWidth(Descriptor, h);
            double innerH = InnerHeight(h);
            _sparkH = Math.Round(innerH * 0.55);
            _sparkClip = new RectangleGeometry(new Rect(0, 0, _w, _sparkH));
            _spark = new Sparkline
            {
                Width = _w,
                Height = _sparkH,
                Stroke = W.Brush(Colors.White),
                StrokeThickness = Math.Max(1, h * 0.04),
                ShowHead = false,
                FillArea = true,
                Opacity = 0.4,
                VerticalAlignment = VerticalAlignment.Center,
                Clip = _sparkClip,
                IsHitTestVisible = false
            };
            var cols = new Grid { Width = _w, Height = innerH };
            for (int i = 0; i < 6; i++)
            {
                cols.ColumnDefinitions.Add(new ColumnDefinition());
                _times[i] = W.Text(p, "", Math.Max(6.5, Math.Round(h * 0.19)), p.SecondaryForeground, FontWeights.SemiBold);
                _glyphs[i] = new WeatherIcon(Math.Max(8, Math.Round(h * 0.32))) { HorizontalAlignment = HorizontalAlignment.Center };
                _temps[i] = W.Text(p, "", Math.Max(6.5, Math.Round(h * 0.2)), weight: FontWeights.Bold);
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
                    _glyphs[i].Kind = SkyGradient.Icon(hr.Code, day);
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
            var p = Vivid.On(ctx.Palette);
            double h = ctx.TileHeight;
            double innerW = InnerWidth(Descriptor, h);
            double innerH = InnerHeight(h);
            double small = Math.Max(6, h * 0.17);
            _arc = new SunArc
            {
                Width = innerW,
                Height = Math.Max(8, innerH - small * 1.3),
                Track = p.Track,
                Trail = W.Brush(Color.FromRgb(0xFF, 0xD6, 0x5C), 0xE0),
                SunColor = Color.FromRgb(0xFF, 0xD0, 0x40),
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

            var moon = new WeatherIcon(Math.Round(h * 0.7), WeatherIconKind.Moon);
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
