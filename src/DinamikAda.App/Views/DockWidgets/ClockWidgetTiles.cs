using System.Collections.Immutable;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using DinamikAda.App.Services.DockWidgets;
using DinamikAda.Core.Widgets;

namespace DinamikAda.App.Views.DockWidgets;

/// <summary>Clock widgets: digital, flip, world, week strip, calendar page, analog, segment and combos.</summary>
internal static class ClockWidgetTiles
{
    public static void Register(IDictionary<string, Func<DockWidgetDescriptor, DockWidgetContext, IDockWidgetTile>> map)
    {
        map["clock"] = (d, c) => new DigitalClockTile(d, c);
        map["flip-clock"] = (d, c) => new FlipClockTile(d, c);
        map["world-clocks"] = (d, c) => new WorldClocksTile(d, c);
        map["seven-days"] = (d, c) => new SevenDaysTile(d, c);
        map["world-time"] = (d, c) => new WorldTimeTile(d, c);
        map["today"] = (d, c) => new TodayTile(d, c);
        map["date"] = (d, c) => new DateTile(d, c);
        map["day-number"] = (d, c) => new DayNumberTile(d, c);
        map["analog"] = (d, c) => new AnalogTile(d, c);
        map["segment-clock"] = (d, c) => new SegmentClockTile(d, c);
        map["clock-weather"] = (d, c) => new ClockWeatherTile(d, c);
    }

    // ---- Shared helpers ------------------------------------------------------------------------

    private const string SettingsGlyph = "";
    private static readonly CultureInfo TrCulture = CultureInfo.GetCultureInfo("tr-TR");
    private static readonly CultureInfo EnCulture = CultureInfo.GetCultureInfo("en-US");

    private static CultureInfo Cul(DockWidgetContext ctx) => ctx.En ? EnCulture : TrCulture;

    /// <summary>Content width inside the chip (mirrors <see cref="DockWidgetTileBase"/>'s sizing).</summary>
    private static double InnerWidth(DockWidgetDescriptor d, double h)
    {
        double u = d.WidthUnits;
        double tileW = Math.Round(h * u + (u > 1 ? (u - 1) * 6 : 0));
        double pad = u > 1.05 ? Math.Round(h * 0.22) : 2;
        return Math.Max(10, tileW - pad * 2);
    }

    private static double InnerHeight(double h) => Math.Max(10, h - 4);

    /// <summary>Scales content down (never up) so it always stays inside the chip.</summary>
    private static Viewbox Fit(UIElement child, HorizontalAlignment ha = HorizontalAlignment.Center) => new()
    {
        Child = child,
        Stretch = Stretch.Uniform,
        StretchDirection = StretchDirection.DownOnly,
        HorizontalAlignment = ha,
        VerticalAlignment = VerticalAlignment.Center
    };

    private static string Clock(DockWidgetContext ctx, DateTime t) => DockWidgetMath.FormatClock(t, ctx.Config.DockClock24h);

    private static string ClockWithAmPm(DockWidgetContext ctx, DateTime t) =>
        ctx.Config.DockClock24h ? Clock(ctx, t) : Clock(ctx, t) + " " + DockWidgetMath.AmPm(t);

    private static string Upper(DockWidgetContext ctx, string s) => s.ToUpper(Cul(ctx));

    private static double ToDisplayTemp(double celsius, bool fahrenheit) => fahrenheit ? celsius * 9.0 / 5.0 + 32.0 : celsius;

    private static string Deg(DockWidgetContext ctx, double celsius) =>
        double.IsFinite(celsius)
            ? ((int)Math.Round(ToDisplayTemp(celsius, ctx.Config.WeatherFahrenheit))).ToString(CultureInfo.InvariantCulture) + "°"
            : "—";

    private static WeatherSnapshot? Weather(DockWidgetContext ctx) =>
        ctx.Data.Weather ?? (ctx.IsAuditMode ? SampleWeather(ctx.Now) : null);

    private static WeatherSnapshot? _sample;

    /// <summary>Deterministic sample used for screenshots when no live weather exists (cached per hour).</summary>
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

    private static TimeSpan OffsetVsLocal(TimeZoneInfo tz)
    {
        var utc = DateTime.UtcNow;
        return tz.GetUtcOffset(utc) - TimeZoneInfo.Local.GetUtcOffset(utc);
    }

    private static void StopFloat(UIElement el)
    {
        if (el.RenderTransform is TranslateTransform tt && !tt.IsFrozen) tt.BeginAnimation(TranslateTransform.YProperty, null);
    }

    /// <summary>Fades the element out, applies the change, then fades it back in.</summary>
    private static void FadeSwap(UIElement el, Action change)
    {
        var o = new DoubleAnimation(0, TimeSpan.FromMilliseconds(140)) { EasingFunction = Motion.Soft };
        o.Completed += (_, _) =>
        {
            change();
            el.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260))
            {
                EasingFunction = Motion.Soft
            });
        };
        el.BeginAnimation(UIElement.OpacityProperty, o);
    }

    private static void StaggerFadeIn(IReadOnlyList<UIElement> items, double stepMs = 45)
    {
        for (int i = 0; i < items.Count; i++)
        {
            var el = items[i];
            el.Opacity = 0;
            el.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260))
            {
                BeginTime = TimeSpan.FromMilliseconds(i * stepMs)
            });
        }
    }

    /// <summary>Base for clock tiles: the click opens the shared clock card.</summary>
    private abstract class ClockTileBase : DockWidgetTileBase
    {
        private ClockCard? _clockCard;

        protected ClockTileBase(DockWidgetDescriptor d, DockWidgetContext c) : base(d, c) { }

        protected override FrameworkElement? CreateCard(DockWidgetContext ctx)
        {
            _clockCard = new ClockCard();
            return _clockCard.Build(ctx, Descriptor);
        }

        protected override void RefreshCard(DockWidgetContext ctx) => _clockCard?.Refresh(ctx);
    }

    /// <summary>Big clock, full date, ISO week and day of year.</summary>
    private sealed class ClockCard
    {
        private TextBlock _time = null!, _sec = null!, _date = null!, _week = null!, _doy = null!;

        public FrameworkElement Build(DockWidgetContext ctx, DockWidgetDescriptor d)
        {
            var p = ctx.Palette;
            _time = W.Text(p, "", 40, weight: FontWeights.SemiBold);
            _sec = W.Text(p, "", 17, p.SecondaryForeground);
            _sec.VerticalAlignment = VerticalAlignment.Bottom;
            _sec.Margin = new Thickness(6, 0, 0, 9);
            var timeRow = new StackPanel { Orientation = Orientation.Horizontal };
            timeRow.Children.Add(_time);
            timeRow.Children.Add(_sec);
            _date = W.Text(p, "", 13.5, p.SecondaryForeground, ui: false);
            _date.Margin = new Thickness(0, 0, 0, 10);
            var weekRow = W.CardRow(p, ctx.T("Hafta", "Week"), "");
            _week = (TextBlock)((DockPanel)weekRow).Children[0];
            var doyRow = W.CardRow(p, ctx.T("Yılın günü", "Day of year"), "");
            _doy = (TextBlock)((DockPanel)doyRow).Children[0];
            var body = W.Column(timeRow, _date, weekRow, doyRow);
            return W.Card(p, d.Glyph, d.Name(ctx.Lang), body, 280,
                W.Button(p, "", () => DockWidgetActions.OpenSettings(ctx, "ms-settings:dateandtime"), SettingsGlyph));
        }

        public void Refresh(DockWidgetContext ctx)
        {
            var now = ctx.Now;
            W.SetTextAnimated(_time, Clock(ctx, now), animate: false);
            string sec = now.ToString("ss", CultureInfo.InvariantCulture);
            if (!ctx.Config.DockClock24h) sec += " " + DockWidgetMath.AmPm(now);
            W.SetTextAnimated(_sec, sec);
            _date.Text = now.ToString("D", Cul(ctx));
            int days = DateTime.IsLeapYear(now.Year) ? 366 : 365;
            _week.Text = ISOWeek.GetWeekOfYear(now).ToString(CultureInfo.InvariantCulture);
            _doy.Text = $"{now.DayOfYear} / {days}";
        }
    }

    // ---- clock -----------------------------------------------------------------------------------

    private sealed class DigitalClockTile : ClockTileBase
    {
        private TextBlock _time = null!, _sec = null!, _ampm = null!, _date = null!;

        public DigitalClockTile(DockWidgetDescriptor d, DockWidgetContext c) : base(d, c) { }

        protected override FrameworkElement BuildContent(DockWidgetContext ctx)
        {
            var p = ctx.Palette;
            double h = ctx.TileHeight;
            _time = W.Text(p, "", Math.Max(11, h * 0.42), weight: FontWeights.SemiBold);
            _sec = W.Text(p, "", Math.Max(7, h * 0.22), p.SecondaryForeground, FontWeights.SemiBold);
            _ampm = W.Text(p, "", Math.Max(6, h * 0.17), p.TertiaryForeground, FontWeights.SemiBold);
            var side = W.Column(_ampm, _sec);
            side.Margin = new Thickness(2, 0, 0, 0);
            side.VerticalAlignment = VerticalAlignment.Bottom;
            var top = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            top.Children.Add(_time);
            top.Children.Add(side);
            _date = W.Text(p, "", Math.Max(6.5, h * 0.2), p.SecondaryForeground);
            _date.HorizontalAlignment = HorizontalAlignment.Center;
            _date.Margin = new Thickness(0, -h * 0.05, 0, 0);
            return Fit(W.Column(top, _date));
        }

        protected override void Refresh(DockWidgetContext ctx)
        {
            var now = ctx.Now;
            W.SetTextAnimated(_time, Clock(ctx, now), animate: false);
            W.SetTextAnimated(_sec, now.ToString("ss", CultureInfo.InvariantCulture));
            bool h12 = !ctx.Config.DockClock24h;
            _ampm.Visibility = h12 ? Visibility.Visible : Visibility.Collapsed;
            if (h12) _ampm.Text = DockWidgetMath.AmPm(now);
            string date = now.ToString(ctx.En ? "ddd, MMM d" : "d MMM ddd", Cul(ctx));
            if (_date.Text != date) _date.Text = date;
        }
    }

    // ---- flip-clock ------------------------------------------------------------------------------

    /// <summary>One split-flap digit card: static halves plus two flaps that fold around the hinge.</summary>
    private sealed class FlapDigit
    {
        private readonly TextBlock _topText, _bottomText, _flapTopText, _flapBottomText;
        private readonly Border _flapTop, _flapBottom;
        private readonly ScaleTransform _flapTopScale = new(1, 1), _flapBottomScale = new(1, 0);
        private string _value = "";

        public Grid Root { get; }

        public FlapDigit(DockWidgetPalette p, double w, double h)
        {
            double half = Math.Round(h / 2);
            double radius = Math.Max(2, w * 0.18);
            var cardBg = W.Brush(p.IsLight ? Color.FromRgb(0x2A, 0x2C, 0x33) : Color.FromRgb(0x24, 0x27, 0x30));
            var flapBg = W.Brush(p.IsLight ? Color.FromRgb(0x33, 0x36, 0x3E) : Color.FromRgb(0x2C, 0x30, 0x3A));
            var digitFg = W.Brush(Color.FromRgb(0xF4, 0xF4, 0xF6));
            double fontSize = h * 0.74;

            Border MakeHalf(bool bottom, Brush bg, out TextBlock text)
            {
                text = new TextBlock
                {
                    FontSize = fontSize,
                    FontFamily = p.UiFont,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = digitFg,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextAlignment = TextAlignment.Center
                };
                var full = new Grid { Width = w, Height = h };
                full.Children.Add(text);
                Canvas.SetTop(full, bottom ? -(h - half) : 0);
                var canvas = new Canvas { Width = w, Height = bottom ? h - half : half };
                canvas.Children.Add(full);
                return new Border
                {
                    Width = w,
                    Height = bottom ? h - half : half,
                    VerticalAlignment = bottom ? VerticalAlignment.Bottom : VerticalAlignment.Top,
                    Background = bg,
                    CornerRadius = bottom ? new CornerRadius(0, 0, radius, radius) : new CornerRadius(radius, radius, 0, 0),
                    ClipToBounds = true,
                    Child = canvas
                };
            }

            var top = MakeHalf(false, cardBg, out _topText);
            var bottom = MakeHalf(true, cardBg, out _bottomText);
            _flapTop = MakeHalf(false, flapBg, out _flapTopText);
            _flapBottom = MakeHalf(true, flapBg, out _flapBottomText);
            _flapTop.RenderTransformOrigin = new Point(0.5, 1);
            _flapTop.RenderTransform = _flapTopScale;
            _flapBottom.RenderTransformOrigin = new Point(0.5, 0);
            _flapBottom.RenderTransform = _flapBottomScale;
            _flapTop.Visibility = Visibility.Hidden;
            _flapBottom.Visibility = Visibility.Hidden;

            var hinge = new Border
            {
                Height = 1,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, half - 0.5, 0, 0),
                Background = W.Brush(Color.FromArgb(0xC0, 0x08, 0x09, 0x0C))
            };
            Root = new Grid { Width = w, Height = h, VerticalAlignment = VerticalAlignment.Center };
            Root.Children.Add(top);
            Root.Children.Add(bottom);
            Root.Children.Add(_flapTop);
            Root.Children.Add(_flapBottom);
            Root.Children.Add(hinge);
        }

        public void Set(string value, bool animate)
        {
            if (value == _value) return;
            string old = _value;
            _value = value;
            if (!animate || old.Length == 0)
            {
                _topText.Text = _bottomText.Text = value;
                return;
            }

            // Static halves: new top, old bottom. Flaps: old top folds down, then new bottom unfolds.
            _topText.Text = value;
            _bottomText.Text = old;
            _flapTopText.Text = old;
            _flapBottomText.Text = value;
            _flapTop.Visibility = Visibility.Visible;
            _flapBottom.Visibility = Visibility.Visible;
            _flapBottomScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            _flapBottomScale.ScaleY = 0;

            var fold = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(150)) { EasingFunction = Motion.Exit };
            fold.Completed += (_, _) =>
            {
                _flapTop.Visibility = Visibility.Hidden;
                var unfold = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)) { EasingFunction = Motion.Soft };
                unfold.Completed += (_, _) =>
                {
                    _bottomText.Text = _value;
                    _flapBottom.Visibility = Visibility.Hidden;
                };
                _flapBottomScale.BeginAnimation(ScaleTransform.ScaleYProperty, unfold);
            };
            _flapTopScale.BeginAnimation(ScaleTransform.ScaleYProperty, fold);
        }
    }

    private sealed class FlipClockTile : ClockTileBase
    {
        private readonly FlapDigit[] _digits = new FlapDigit[4];
        private bool _shown;

        public FlipClockTile(DockWidgetDescriptor d, DockWidgetContext c) : base(d, c) { }

        protected override FrameworkElement BuildContent(DockWidgetContext ctx)
        {
            var p = ctx.Palette;
            double h = ctx.TileHeight;
            double innerW = InnerWidth(Descriptor, h);
            double colonW = Math.Max(4, h * 0.16);
            double gap = Math.Max(1.5, h * 0.05);
            double dw = Math.Floor((innerW - colonW - gap * 2) / 4);
            double dh = Math.Min(InnerHeight(h) - 2, dw * 1.75);
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            for (int i = 0; i < 4; i++)
            {
                _digits[i] = new FlapDigit(p, dw, dh);
                if (i == 1 || i == 3) _digits[i].Root.Margin = new Thickness(gap, 0, 0, 0);
                row.Children.Add(_digits[i].Root);
                if (i == 1)
                {
                    var dot1 = new Ellipse { Width = colonW * 0.4, Height = colonW * 0.4, Fill = p.SecondaryForeground, Margin = new Thickness(0, 0, 0, dh * 0.18) };
                    var dot2 = new Ellipse { Width = colonW * 0.4, Height = colonW * 0.4, Fill = p.SecondaryForeground };
                    var colon = new StackPanel { Width = colonW, VerticalAlignment = VerticalAlignment.Center };
                    dot1.HorizontalAlignment = dot2.HorizontalAlignment = HorizontalAlignment.Center;
                    colon.Children.Add(dot1);
                    colon.Children.Add(dot2);
                    row.Children.Add(colon);
                }
            }
            return Fit(row);
        }

        protected override void Refresh(DockWidgetContext ctx)
        {
            var now = ctx.Now;
            string hh = ctx.Config.DockClock24h
                ? now.ToString("HH", CultureInfo.InvariantCulture)
                : now.ToString("%h", CultureInfo.InvariantCulture).PadLeft(2, ' ');
            string mm = now.ToString("mm", CultureInfo.InvariantCulture);
            string all = hh + mm;
            for (int i = 0; i < 4; i++) _digits[i].Set(all[i] == ' ' ? " " : all[i].ToString(), _shown);
            _shown = true;
        }
    }

    // ---- analog faces (world-clocks, analog) -----------------------------------------------------

    /// <summary>Analog face on a Canvas with smoothly animated hands (continuous angles, no 360→0 jump).</summary>
    private sealed class AnalogFace
    {
        private readonly RotateTransform _hour = new(), _minute = new(), _second = new();
        private readonly bool _hasSecond;
        private double _hourAngle = double.NaN, _minuteAngle = double.NaN;

        public Canvas Root { get; }

        public AnalogFace(double size, Brush face, Brush? faceBorder, Brush tick, Brush hand, Brush? secondHand, bool majorTicksOnly)
        {
            _hasSecond = secondHand is not null;
            double c = size / 2;
            Root = new Canvas { Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            Root.Children.Add(new Ellipse
            {
                Width = size,
                Height = size,
                Fill = face,
                Stroke = faceBorder,
                StrokeThickness = faceBorder is null ? 0 : Math.Max(0.6, size * 0.02)
            });
            for (int i = 0; i < 12; i++)
            {
                bool major = i % 3 == 0;
                if (majorTicksOnly && !major && size < 18) continue;
                double a = i * 30 * Math.PI / 180;
                double r1 = c * (major ? 0.70 : 0.78), r2 = c * 0.88;
                Root.Children.Add(new Line
                {
                    X1 = c + r1 * Math.Sin(a), Y1 = c - r1 * Math.Cos(a),
                    X2 = c + r2 * Math.Sin(a), Y2 = c - r2 * Math.Cos(a),
                    Stroke = tick,
                    StrokeThickness = Math.Max(0.7, size * (major ? 0.045 : 0.03)),
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round
                });
            }
            Root.Children.Add(Hand(c, c * 0.48, Math.Max(1.2, size * 0.075), hand, _hour, 0));
            Root.Children.Add(Hand(c, c * 0.72, Math.Max(1.0, size * 0.05), hand, _minute, 0));
            if (secondHand is not null) Root.Children.Add(Hand(c, c * 0.80, Math.Max(0.7, size * 0.025), secondHand, _second, c * 0.16));
            Root.Children.Add(new Ellipse
            {
                Width = size * 0.1, Height = size * 0.1,
                Fill = secondHand ?? hand,
                Margin = new Thickness(c - size * 0.05, c - size * 0.05, 0, 0)
            });
        }

        private static Line Hand(double c, double length, double thickness, Brush brush, RotateTransform rt, double tail)
        {
            rt.CenterX = c;
            rt.CenterY = c;
            return new Line
            {
                X1 = c, Y1 = c + tail, X2 = c, Y2 = c - length,
                Stroke = brush,
                StrokeThickness = thickness,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                RenderTransform = rt
            };
        }

        public void SetTime(DateTime t, bool animate)
        {
            var (h, m, _) = DockWidgetMath.HandAngles(new DateTime(t.Year, t.Month, t.Day, t.Hour, t.Minute, 0));
            _hourAngle = Move(_hour, _hourAngle, h, animate);
            _minuteAngle = Move(_minute, _minuteAngle, m, animate);
        }

        private static double Move(RotateTransform rt, double current, double target, bool animate)
        {
            if (double.IsNaN(current))
            {
                rt.Angle = target;
                return target;
            }
            double delta = ((target - current) % 360 + 540) % 360 - 180;   // shortest way, handles 359→0
            if (Math.Abs(delta) < 0.01) return current;
            double next = current + delta;
            if (!animate)
            {
                rt.BeginAnimation(RotateTransform.AngleProperty, null);
                rt.Angle = next;
                return next;
            }
            rt.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(current, next, TimeSpan.FromMilliseconds(600))
            {
                EasingFunction = Motion.Soft
            }, HandoffBehavior.SnapshotAndReplace);
            return next;
        }

        /// <summary>Continuous second-hand sweep: one turn per minute, synced to <paramref name="now"/>.</summary>
        public void StartSweep(DateTime now)
        {
            if (!_hasSecond) return;
            double a0 = (now.Second + now.Millisecond / 1000.0) * 6.0;
            _second.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(a0, a0 + 360, TimeSpan.FromSeconds(60))
            {
                RepeatBehavior = RepeatBehavior.Forever
            });
        }

        public void StopSweep() => _second.BeginAnimation(RotateTransform.AngleProperty, null);

        public void SetSecondStatic(DateTime now) => _second.Angle = now.Second * 6.0;
    }

    // ---- world-clocks ----------------------------------------------------------------------------

    private sealed class WorldClocksTile : DockWidgetTileBase
    {
        private readonly AnalogFace[] _faces = new AnalogFace[3];
        private readonly TextBlock[] _labels = new TextBlock[3];
        private readonly TimeZoneInfo?[] _zones = new TimeZoneInfo?[3];
        private readonly string[] _zoneIds = new string[3];
        private string _zoneKey = "\0";
        private bool _shown;
        private StackPanel? _cardRows;
        private string _cardKey = "";
        private readonly List<TextBlock> _cardValues = [];

        public WorldClocksTile(DockWidgetDescriptor d, DockWidgetContext c) : base(d, c) { }

        protected override FrameworkElement BuildContent(DockWidgetContext ctx)
        {
            var p = ctx.Palette;
            double h = ctx.TileHeight;
            double innerW = InnerWidth(Descriptor, h);
            double face = Math.Min(h * 0.6, innerW / 3 - 4);
            var grid = new Grid();
            var faceBg = p.IsLight ? W.Brush(Color.FromArgb(0x10, 0, 0, 0)) : W.Brush(Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF));
            var hand = p.Foreground;
            for (int i = 0; i < 3; i++)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition());
                _faces[i] = new AnalogFace(face, faceBg, p.TileBorder, p.TertiaryForeground, hand, null, true);
                _labels[i] = W.Text(p, "", Math.Max(6, h * 0.18), p.SecondaryForeground, FontWeights.SemiBold);
                _labels[i].HorizontalAlignment = HorizontalAlignment.Center;
                _labels[i].MaxWidth = innerW / 3;
                var col = W.Column(_faces[i].Root, _labels[i]);
                col.HorizontalAlignment = HorizontalAlignment.Center;
                Grid.SetColumn(col, i);
                grid.Children.Add(col);
            }
            return Fit(grid);
        }

        private void ResolveZones(DockWidgetContext ctx)
        {
            var list = ctx.Config.WorldClockZones ?? [];
            string key = string.Join("|", list);
            if (key == _zoneKey) return;
            _zoneKey = key;
            for (int i = 0; i < 3; i++)
            {
                string id = i < list.Count ? list[i] : "";
                _zoneIds[i] = id;
                _zones[i] = DockWidgetMath.FindTimeZone(id);
                string city = DockWidgetMath.CityFromZoneId(id);
                _labels[i].Text = _zones[i] is null ? (string.IsNullOrWhiteSpace(id) ? "—" : city + "?") : city;
            }
        }

        protected override void Refresh(DockWidgetContext ctx)
        {
            ResolveZones(ctx);
            for (int i = 0; i < 3; i++)
            {
                var tz = _zones[i];
                var t = tz is null ? ctx.Now : ctx.Now + OffsetVsLocal(tz);
                _faces[i].SetTime(t, _shown);
            }
            _shown = true;
        }

        protected override FrameworkElement? CreateCard(DockWidgetContext ctx)
        {
            _cardRows = new StackPanel();
            _cardKey = "";
            return W.Card(ctx.Palette, Descriptor.Glyph, Descriptor.Name(ctx.Lang), _cardRows, 280,
                W.Button(ctx.Palette, "", () => DockWidgetActions.OpenSettings(ctx, "ms-settings:dateandtime"), SettingsGlyph));
        }

        protected override void RefreshCard(DockWidgetContext ctx)
        {
            if (_cardRows is null) return;
            ResolveZones(ctx);
            var p = ctx.Palette;
            if (_cardKey != _zoneKey)
            {
                _cardKey = _zoneKey;
                _cardRows.Children.Clear();
                _cardValues.Clear();
                var local = W.CardRow(p, ctx.T("Yerel", "Local"), "", p.AccentBrush);
                _cardRows.Children.Add(local);
                _cardValues.Add((TextBlock)((DockPanel)local).Children[0]);
                for (int i = 0; i < 3; i++)
                {
                    if (string.IsNullOrWhiteSpace(_zoneIds[i])) continue;
                    var row = W.CardRow(p, DockWidgetMath.CityFromZoneId(_zoneIds[i]), "");
                    _cardRows.Children.Add(row);
                    _cardValues.Add((TextBlock)((DockPanel)row).Children[0]);
                }
            }
            _cardValues[0].Text = ClockWithAmPm(ctx, ctx.Now);
            int v = 1;
            for (int i = 0; i < 3 && v < _cardValues.Count; i++)
            {
                if (string.IsNullOrWhiteSpace(_zoneIds[i])) continue;
                var tz = _zones[i];
                _cardValues[v++].Text = tz is null
                    ? ctx.T("Bilinmeyen bölge", "Unknown zone")
                    : $"{ClockWithAmPm(ctx, ctx.Now + OffsetVsLocal(tz))}   {DockWidgetMath.FormatOffset(OffsetVsLocal(tz))}";
            }
        }
    }

    // ---- seven-days ------------------------------------------------------------------------------

    private sealed class SevenDaysTile : DockWidgetTileBase
    {
        private readonly TextBlock[] _names = new TextBlock[7];
        private readonly TextBlock[] _nums = new TextBlock[7];
        private readonly StackPanel[] _chips = new StackPanel[7];
        private TextBlock _temp = null!;
        private Border _highlight = null!;
        private StackPanel _row = null!;
        private Brush? _onAccent;
        private double _chipW, _todayW;
        private DateTime _day = DateTime.MinValue;
        private int _todayIdx = -1;
        private StackPanel? _cardRows;
        private object? _cardSnap = new();

        public SevenDaysTile(DockWidgetDescriptor d, DockWidgetContext c) : base(d, c) { }

        protected override FrameworkElement BuildContent(DockWidgetContext ctx)
        {
            var p = ctx.Palette;
            double h = ctx.TileHeight;
            double innerW = InnerWidth(Descriptor, h);
            double innerH = InnerHeight(h);
            _chipW = Math.Floor(innerW / 7.9);
            _todayW = innerW - _chipW * 6;
            var canvas = new Canvas { Width = innerW, Height = innerH };
            _highlight = new Border
            {
                Width = _todayW,
                Height = innerH,
                CornerRadius = new CornerRadius(Math.Max(5, h * 0.2)),
                Background = W.Brush(p.Accent)
            };
            canvas.Children.Add(_highlight);
            var row = _row = new StackPanel { Orientation = Orientation.Horizontal, Height = innerH };
            _temp = W.Text(p, "", Math.Max(6.5, h * 0.2), weight: FontWeights.SemiBold);
            _temp.HorizontalAlignment = HorizontalAlignment.Center;
            for (int i = 0; i < 7; i++)
            {
                _names[i] = W.Text(p, "", Math.Max(6, h * 0.17), p.SecondaryForeground, FontWeights.SemiBold);
                _nums[i] = W.Text(p, "", Math.Max(8, h * 0.3), weight: FontWeights.SemiBold);
                _names[i].HorizontalAlignment = _nums[i].HorizontalAlignment = HorizontalAlignment.Center;
                _names[i].TextTrimming = _nums[i].TextTrimming = TextTrimming.None;
                _chips[i] = W.Column(_names[i], _nums[i]);
                _chips[i].HorizontalAlignment = HorizontalAlignment.Center;
                var cell = new Grid { Width = _chipW, Height = innerH };
                cell.Children.Add(Fit(_chips[i]));
                row.Children.Add(cell);
            }
            var root = new Grid { Width = innerW, Height = innerH, ClipToBounds = true };
            root.Children.Add(canvas);
            root.Children.Add(row);
            return root;
        }

        private void Layout(DockWidgetContext ctx, bool slide)
        {
            var p = ctx.Palette;
            var row = _row;
            var monday = _day.AddDays(-(((int)_day.DayOfWeek + 6) % 7));
            _onAccent ??= IsLightColor(p.Accent) ? W.Brush(Color.FromRgb(0x10, 0x12, 0x18)) : W.Brush(Colors.White);
            var onAccent = _onAccent;
            for (int i = 0; i < 7; i++)
            {
                var d = monday.AddDays(i);
                bool today = i == _todayIdx;
                _names[i].Text = Upper(ctx, d.ToString("ddd", Cul(ctx)));
                _nums[i].Text = d.Day.ToString(CultureInfo.InvariantCulture);
                _names[i].Foreground = today ? onAccent : p.SecondaryForeground;
                _nums[i].Foreground = today ? onAccent : p.Foreground;
                var cell = (Grid)row.Children[i];
                cell.Width = today ? _todayW : _chipW;
                if (_chips[i].Children.Contains(_temp)) _chips[i].Children.Remove(_temp);
                if (today)
                {
                    _temp.Foreground = onAccent;
                    _chips[i].Children.Add(_temp);
                }
            }
            double target = _todayIdx * _chipW;
            if (slide)
            {
                _highlight.BeginAnimation(Canvas.LeftProperty, new DoubleAnimation(0, target, TimeSpan.FromMilliseconds(520))
                {
                    BeginTime = TimeSpan.FromMilliseconds(120),
                    EasingFunction = Motion.Soft
                });
                _highlight.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300)));
            }
            else
            {
                _highlight.BeginAnimation(Canvas.LeftProperty, null);
                Canvas.SetLeft(_highlight, target);
            }
        }

        private static bool IsLightColor(Color c) => (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0 > 0.62;

        protected override void Refresh(DockWidgetContext ctx)
        {
            var day = ctx.Now.Date;
            if (day != _day)
            {
                bool first = _day == DateTime.MinValue;
                _day = day;
                _todayIdx = ((int)day.DayOfWeek + 6) % 7;
                Layout(ctx, slide: !first);
            }
            var wx = Weather(ctx);
            string t = wx is null ? "" : Deg(ctx, wx.TempC);
            _temp.Visibility = t.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            W.SetTextAnimated(_temp, t);
        }

        public override void OnAttached(DockWidgetContext ctx)
        {
            if (_day == DateTime.MinValue) Refresh(ctx);
            StaggerFadeIn(_chips);
            Layout(ctx, slide: true);
        }

        protected override FrameworkElement? CreateCard(DockWidgetContext ctx)
        {
            _cardRows = new StackPanel();
            _cardSnap = new object();
            return W.Card(ctx.Palette, Descriptor.Glyph, Descriptor.Name(ctx.Lang), _cardRows, 290);
        }

        protected override void RefreshCard(DockWidgetContext ctx)
        {
            if (_cardRows is null) return;
            var wx = Weather(ctx);
            object key = (object?)wx ?? ctx.Now.Date;
            if (Equals(key, _cardSnap)) return;
            _cardSnap = key;
            _cardRows.Children.Clear();
            var p = ctx.Palette;
            var monday = ctx.Now.Date.AddDays(-(((int)ctx.Now.DayOfWeek + 6) % 7));
            for (int i = 0; i < 7; i++)
            {
                var d = monday.AddDays(i);
                bool today = d == ctx.Now.Date;
                string label = d.ToString(ctx.En ? "dddd, MMM d" : "d MMMM dddd", Cul(ctx));
                var wd = wx?.Days.FirstOrDefault(x => x.Date.Date == d);
                string value = wd is null ? (d < ctx.Now.Date ? "" : "—") : $"{Deg(ctx, wd.HighC)} / {Deg(ctx, wd.LowC)}";
                string? glyph = wd is null ? null : DockWidgetMath.WeatherGlyph(wd.Code);
                _cardRows.Children.Add(W.CardRow(p, label, value, today ? p.AccentBrush : null, glyph));
            }
            if (wx is null)
            {
                var status = W.Text(p, ctx.Data.WeatherStatus ?? ctx.T("Ayarlardan şehir seç", "Pick a city in Settings"), 12, p.TertiaryForeground, ui: false);
                status.Margin = new Thickness(0, 6, 0, 0);
                _cardRows.Children.Add(status);
            }
        }
    }

    // ---- world-time ------------------------------------------------------------------------------

    private sealed class WorldTimeTile : ClockTileBase
    {
        private TextBlock _city = null!, _time = null!, _ampm = null!;
        private string _zoneId = "\0";
        private TimeZoneInfo? _zone;

        public WorldTimeTile(DockWidgetDescriptor d, DockWidgetContext c) : base(d, c) { }

        protected override FrameworkElement BuildContent(DockWidgetContext ctx)
        {
            var p = ctx.Palette;
            double h = ctx.TileHeight;
            _city = W.Text(p, "", Math.Max(6.5, h * 0.19), p.SecondaryForeground, FontWeights.SemiBold);
            _city.HorizontalAlignment = HorizontalAlignment.Center;
            _city.MaxWidth = InnerWidth(Descriptor, h);
            _time = W.Text(p, "", Math.Max(11, h * 0.42), weight: FontWeights.SemiBold);
            _ampm = W.Text(p, "", Math.Max(6, h * 0.17), p.TertiaryForeground, FontWeights.SemiBold);
            _ampm.VerticalAlignment = VerticalAlignment.Bottom;
            _ampm.Margin = new Thickness(2, 0, 0, h * 0.06);
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, -h * 0.05, 0, 0) };
            row.Children.Add(_time);
            row.Children.Add(_ampm);
            return Fit(W.Column(_city, row));
        }

        protected override void Refresh(DockWidgetContext ctx)
        {
            string id = ctx.Config.WorldTimeZone ?? "";
            if (id != _zoneId)
            {
                _zoneId = id;
                _zone = DockWidgetMath.FindTimeZone(id);
            }
            if (_zone is null)
            {
                _city.Text = string.IsNullOrWhiteSpace(id) ? ctx.T("Bölge seçilmedi", "No zone set") : DockWidgetMath.CityFromZoneId(id) + " ?";
                W.SetTextAnimated(_time, "--:--");
                _ampm.Visibility = Visibility.Collapsed;
                return;
            }
            var off = OffsetVsLocal(_zone);
            var t = ctx.Now + off;
            string city = $"{Upper(ctx, DockWidgetMath.CityFromZoneId(id))}  {DockWidgetMath.FormatOffset(off)}";
            if (_city.Text != city) _city.Text = city;
            W.SetTextAnimated(_time, Clock(ctx, t));
            bool h12 = !ctx.Config.DockClock24h;
            _ampm.Visibility = h12 ? Visibility.Visible : Visibility.Collapsed;
            if (h12) _ampm.Text = DockWidgetMath.AmPm(t);
        }
    }

    // ---- today -----------------------------------------------------------------------------------

    private sealed class TodayTile : ClockTileBase
    {
        private TextBlock _day = null!, _time = null!, _glyph = null!, _temp = null!;

        public TodayTile(DockWidgetDescriptor d, DockWidgetContext c) : base(d, c) { }

        protected override FrameworkElement BuildContent(DockWidgetContext ctx)
        {
            var p = ctx.Palette;
            double h = ctx.TileHeight;
            _day = W.Text(p, "", Math.Max(6.5, h * 0.2), p.AccentBrush, FontWeights.SemiBold);
            _time = W.Text(p, "", Math.Max(10, h * 0.38), weight: FontWeights.SemiBold);
            _time.Margin = new Thickness(0, -h * 0.04, 0, 0);
            var left = W.Column(_day, _time);
            _glyph = W.Glyph(p, "", Math.Max(9, h * 0.36), p.Foreground);
            _temp = W.Text(p, "", Math.Max(6.5, h * 0.21), weight: FontWeights.SemiBold);
            _temp.HorizontalAlignment = HorizontalAlignment.Center;
            var right = W.Column(_glyph, _temp);
            right.HorizontalAlignment = HorizontalAlignment.Right;
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var lf = Fit(left, HorizontalAlignment.Left);
            var rf = Fit(right, HorizontalAlignment.Right);
            rf.Margin = new Thickness(4, 0, 0, 0);
            Grid.SetColumn(rf, 1);
            g.Children.Add(lf);
            g.Children.Add(rf);
            return g;
        }

        protected override void Refresh(DockWidgetContext ctx)
        {
            var now = ctx.Now;
            string day = Upper(ctx, now.ToString(ctx.En ? "ddd MMM d" : "ddd d MMM", Cul(ctx)));
            if (_day.Text != day) _day.Text = day;
            W.SetTextAnimated(_time, ClockWithAmPm(ctx, now), animate: false);
            var wx = Weather(ctx);
            string glyph = wx is null ? "" : DockWidgetMath.WeatherGlyph(wx.Code, wx.IsDay);
            if (_glyph.Text != glyph) _glyph.Text = glyph;
            _glyph.Opacity = wx is null ? 0.45 : 1;
            W.SetTextAnimated(_temp, wx is null ? "—" : Deg(ctx, wx.TempC));
        }

        public override void OnAttached(DockWidgetContext ctx) => W.FloatForever(_glyph, Math.Max(1, ctx.TileHeight * 0.04), 2.8);
        public override void OnDetached() => StopFloat(_glyph);
    }

    // ---- date ------------------------------------------------------------------------------------

    private sealed class DateTile : ClockTileBase
    {
        private TextBlock _date = null!, _weekday = null!;
        private FrameworkElement _box = null!;
        private DateTime _day = DateTime.MinValue;

        public DateTile(DockWidgetDescriptor d, DockWidgetContext c) : base(d, c) { }

        protected override FrameworkElement BuildContent(DockWidgetContext ctx)
        {
            var p = ctx.Palette;
            double h = ctx.TileHeight;
            _weekday = W.Text(p, "", Math.Max(6.5, h * 0.2), p.AccentBrush, FontWeights.SemiBold);
            _date = W.Text(p, "", Math.Max(10, h * 0.38), weight: FontWeights.SemiBold);
            _date.Margin = new Thickness(0, -h * 0.04, 0, 0);
            _weekday.HorizontalAlignment = _date.HorizontalAlignment = HorizontalAlignment.Center;
            _box = Fit(W.Column(_weekday, _date));
            return _box;
        }

        protected override void Refresh(DockWidgetContext ctx)
        {
            var day = ctx.Now.Date;
            if (day == _day) return;
            bool first = _day == DateTime.MinValue;
            _day = day;
            void Apply()
            {
                _weekday.Text = Upper(ctx, day.ToString("dddd", Cul(ctx)));
                _date.Text = day.ToString(ctx.En ? "MMMM d" : "d MMMM", Cul(ctx));
            }
            if (first) Apply();
            else FadeSwap(_box, Apply);
        }
    }

    // ---- day-number ------------------------------------------------------------------------------

    private sealed class DayNumberTile : DockWidgetTileBase
    {
        private TextBlock _month = null!, _num = null!;
        private Border _body = null!;
        private readonly ScaleTransform _fold = new(1, 1);
        private readonly TranslateTransform _lift = new();
        private DateTime _day = DateTime.MinValue;

        public DayNumberTile(DockWidgetDescriptor d, DockWidgetContext c) : base(d, c) { }

        protected override bool ShowChrome => false;
        protected override Thickness ContentPadding(double h) => new(0);

        protected override FrameworkElement BuildContent(DockWidgetContext ctx)
        {
            var p = ctx.Palette;
            double h = ctx.TileHeight;
            double r = Math.Max(6, h * 0.24);
            var red = p.Negative;
            _month = W.Text(p, "", Math.Max(6, h * 0.19), W.Brush(Colors.White), FontWeights.Bold);
            _month.HorizontalAlignment = HorizontalAlignment.Center;
            _month.TextTrimming = TextTrimming.None;
            var header = new Border
            {
                Height = Math.Round(h * 0.3),
                Background = W.Brush(red),
                CornerRadius = new CornerRadius(r, r, 0, 0),
                Child = _month
            };
            _num = W.Text(p, "", Math.Max(11, h * 0.5), p.Foreground, FontWeights.SemiBold);
            _num.HorizontalAlignment = HorizontalAlignment.Center;
            _num.TextTrimming = TextTrimming.None;
            _body = new Border
            {
                Child = _num,
                RenderTransformOrigin = new Point(0.5, 0),
                RenderTransform = _fold
            };
            var stack = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(header, Dock.Top);
            stack.Children.Add(header);
            stack.Children.Add(_body);
            var page = new Border
            {
                Width = h,
                Height = h,
                CornerRadius = new CornerRadius(r),
                Background = p.IsLight ? W.Brush(Colors.White) : p.TileHoverBackground,
                BorderBrush = p.TileBorder,
                BorderThickness = new Thickness(0.6),
                ClipToBounds = true,
                Child = stack,
                RenderTransform = _lift
            };
            page.MouseEnter += (_, _) => _lift.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(-Math.Max(1.5, h * 0.06), TimeSpan.FromMilliseconds(160)) { EasingFunction = Motion.Soft });
            page.MouseLeave += (_, _) => _lift.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(0, TimeSpan.FromMilliseconds(200)) { EasingFunction = Motion.Soft });
            return page;
        }

        protected override void Refresh(DockWidgetContext ctx)
        {
            var day = ctx.Now.Date;
            if (day == _day) return;
            bool first = _day == DateTime.MinValue;
            _day = day;
            void Apply()
            {
                _month.Text = Upper(ctx, day.ToString("MMM", Cul(ctx)));
                _num.Text = day.Day.ToString(CultureInfo.InvariantCulture);
            }
            if (first) { Apply(); return; }
            var down = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(180)) { EasingFunction = Motion.Exit };
            down.Completed += (_, _) =>
            {
                Apply();
                _fold.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260))
                {
                    EasingFunction = Motion.Soft
                });
            };
            _fold.BeginAnimation(ScaleTransform.ScaleYProperty, down);
        }

        protected override FrameworkElement? CreateCard(DockWidgetContext ctx)
        {
            var p = ctx.Palette;
            var now = ctx.Now.Date;
            var cul = Cul(ctx);
            var grid = new UniformGrid { Columns = 7 };
            var firstDay = new DateTime(now.Year, now.Month, 1);
            for (int i = 0; i < 7; i++)
            {
                var wd = Upper(ctx, firstDay.AddDays(i - ((int)firstDay.DayOfWeek + 6) % 7).ToString("ddd", cul));
                var t = W.Text(p, wd.Length > 2 ? wd[..2] : wd, 11, p.TertiaryForeground, FontWeights.SemiBold);
                t.HorizontalAlignment = HorizontalAlignment.Center;
                t.Margin = new Thickness(0, 0, 0, 4);
                grid.Children.Add(t);
            }
            int lead = ((int)firstDay.DayOfWeek + 6) % 7;
            for (int i = 0; i < lead; i++) grid.Children.Add(new Border());
            int days = DateTime.DaysInMonth(now.Year, now.Month);
            for (int dd = 1; dd <= days; dd++)
            {
                bool today = dd == now.Day;
                var t = W.Text(p, dd.ToString(CultureInfo.InvariantCulture), 12, today ? W.Brush(Colors.White) : p.Foreground,
                    today ? FontWeights.Bold : FontWeights.Normal);
                t.HorizontalAlignment = HorizontalAlignment.Center;
                grid.Children.Add(new Border
                {
                    Width = 26, Height = 24,
                    CornerRadius = new CornerRadius(12),
                    Background = today ? W.Brush(p.Negative) : Brushes.Transparent,
                    Child = t,
                    Margin = new Thickness(1)
                });
            }
            var title = W.Text(p, now.ToString(ctx.En ? "MMMM yyyy" : "MMMM yyyy", cul), 13, p.SecondaryForeground, FontWeights.SemiBold);
            title.Margin = new Thickness(0, 0, 0, 8);
            return W.Card(p, Descriptor.Glyph, Descriptor.Name(ctx.Lang), W.Column(title, grid), 260);
        }
    }

    // ---- analog ----------------------------------------------------------------------------------

    private sealed class AnalogTile : ClockTileBase
    {
        private AnalogFace _face = null!;
        private bool _shown, _sweeping;

        public AnalogTile(DockWidgetDescriptor d, DockWidgetContext c) : base(d, c) { }

        protected override bool ShowChrome => false;
        protected override Thickness ContentPadding(double h) => new(0);

        protected override FrameworkElement BuildContent(DockWidgetContext ctx)
        {
            var p = ctx.Palette;
            double h = ctx.TileHeight;
            _face = new AnalogFace(h,
                W.Brush(Color.FromRgb(0x14, 0x16, 0x1C)),
                W.Brush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
                W.Brush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF)),
                W.Brush(Color.FromRgb(0xF5, 0xF5, 0xF7)),
                W.Brush(Color.FromRgb(0xFF, 0x9F, 0x0A)),
                false);
            _ = p;
            return _face.Root;
        }

        protected override void Refresh(DockWidgetContext ctx)
        {
            _face.SetTime(ctx.Now, _shown);
            if (!_sweeping) _face.SetSecondStatic(ctx.Now);
            _shown = true;
        }

        public override void OnAttached(DockWidgetContext ctx)
        {
            _sweeping = true;
            _face.StartSweep(ctx.IsAuditMode ? ctx.Now : DateTime.Now);
        }

        public override void OnDetached()
        {
            _sweeping = false;
            _face.StopSweep();
        }
    }

    // ---- segment-clock ---------------------------------------------------------------------------

    private sealed class SegmentDigit
    {
        // Segment order: a b c d e f g
        private static readonly string[] Masks =
        [
            "1111110", "0110000", "1101101", "1111001", "0110011",
            "1011011", "1011111", "1110000", "1111111", "1111011"
        ];

        private readonly Polygon[] _segs = new Polygon[7];
        private readonly bool[] _on = new bool[7];
        private char _value = '\0';
        private const double OffOpacity = 0.09;

        public Canvas Root { get; }

        public SegmentDigit(double w, double h, Brush fill)
        {
            Root = new Canvas { Width = w, Height = h };
            double t = Math.Max(1.4, w * 0.2);
            double g = t * 0.12;
            double hl = w - t - 2 * g;
            double vl = h / 2 - t / 2 - 2 * g;
            _segs[0] = H(t / 2 + g, t / 2, hl, t);
            _segs[1] = V(w - t / 2, t / 2 + g, vl, t);
            _segs[2] = V(w - t / 2, h / 2 + g, vl, t);
            _segs[3] = H(t / 2 + g, h - t / 2, hl, t);
            _segs[4] = V(t / 2, h / 2 + g, vl, t);
            _segs[5] = V(t / 2, t / 2 + g, vl, t);
            _segs[6] = H(t / 2 + g, h / 2, hl, t);
            foreach (var s in _segs)
            {
                s.Fill = fill;
                s.Opacity = OffOpacity;
                Root.Children.Add(s);
            }
        }

        private static Polygon H(double x, double y, double l, double t) => new()
        {
            Points = [new(x, y), new(x + t / 2, y - t / 2), new(x + l - t / 2, y - t / 2), new(x + l, y), new(x + l - t / 2, y + t / 2), new(x + t / 2, y + t / 2)]
        };

        private static Polygon V(double x, double y, double l, double t) => new()
        {
            Points = [new(x, y), new(x + t / 2, y + t / 2), new(x + t / 2, y + l - t / 2), new(x, y + l), new(x - t / 2, y + l - t / 2), new(x - t / 2, y + t / 2)]
        };

        public void Set(char c, bool animate)
        {
            if (c == _value) return;
            _value = c;
            string mask = c is >= '0' and <= '9' ? Masks[c - '0'] : "0000000";
            for (int i = 0; i < 7; i++)
            {
                bool on = mask[i] == '1';
                if (on == _on[i] && animate) continue;
                _on[i] = on;
                double target = on ? 1 : OffOpacity;
                if (animate)
                    _segs[i].BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(target, TimeSpan.FromMilliseconds(on ? 220 : 320)));
                else
                {
                    _segs[i].BeginAnimation(UIElement.OpacityProperty, null);
                    _segs[i].Opacity = target;
                }
            }
        }
    }

    private sealed class SegmentClockTile : ClockTileBase
    {
        private readonly SegmentDigit[] _digits = new SegmentDigit[4];
        private TextBlock _top = null!;
        private StackPanel _colon = null!;
        private bool _shown;

        public SegmentClockTile(DockWidgetDescriptor d, DockWidgetContext c) : base(d, c) { }

        protected override FrameworkElement BuildContent(DockWidgetContext ctx)
        {
            var p = ctx.Palette;
            double h = ctx.TileHeight;
            double innerW = InnerWidth(Descriptor, h);
            double topFont = Math.Max(6, h * 0.17);
            double dh = Math.Max(10, InnerHeight(h) - topFont * 1.35 - 1);
            double colonW = Math.Max(3, dh * 0.22);
            double gap = Math.Max(1.2, dh * 0.08);
            double dw = Math.Min(dh * 0.55, (innerW - colonW - gap * 4) / 4);
            var fill = W.Brush(p.Accent);
            _top = W.Text(p, "", topFont, p.SecondaryForeground, FontWeights.SemiBold);
            _top.HorizontalAlignment = HorizontalAlignment.Center;
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            for (int i = 0; i < 4; i++)
            {
                _digits[i] = new SegmentDigit(dw, dh, fill);
                _digits[i].Root.Margin = new Thickness(i == 0 ? 0 : gap, 0, 0, 0);
                row.Children.Add(_digits[i].Root);
                if (i == 1)
                {
                    double ds = Math.Max(1.6, dw * 0.2);
                    _colon = new StackPanel { Width = colonW, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(gap, 0, 0, 0) };
                    _colon.Children.Add(new Rectangle { Width = ds, Height = ds, Fill = fill, Margin = new Thickness(0, 0, 0, dh * 0.22) });
                    _colon.Children.Add(new Rectangle { Width = ds, Height = ds, Fill = fill });
                    row.Children.Add(_colon);
                }
            }
            return Fit(W.Column(_top, row));
        }

        protected override void Refresh(DockWidgetContext ctx)
        {
            var now = ctx.Now;
            string top = Upper(ctx, now.ToString(ctx.En ? "ddd MMM d" : "ddd d MMM", Cul(ctx)));
            if (_top.Text != top) _top.Text = top;
            string hh = ctx.Config.DockClock24h
                ? now.ToString("HH", CultureInfo.InvariantCulture)
                : now.ToString("%h", CultureInfo.InvariantCulture).PadLeft(2, ' ');
            string all = hh + now.ToString("mm", CultureInfo.InvariantCulture);
            for (int i = 0; i < 4; i++) _digits[i].Set(all[i], _shown);
            _shown = true;
        }

        public override void OnAttached(DockWidgetContext ctx) =>
            _colon.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, 0.15, TimeSpan.FromMilliseconds(500))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = Motion.Gentle
            });

        public override void OnDetached() => _colon.BeginAnimation(UIElement.OpacityProperty, null);
    }

    // ---- clock-weather ---------------------------------------------------------------------------

    private sealed class ClockWeatherTile : ClockTileBase
    {
        private readonly Border[] _blocks = new Border[3];
        private TextBlock _hh = null!, _mm = null!, _wd = null!, _dn = null!, _glyph = null!, _temp = null!;

        public ClockWeatherTile(DockWidgetDescriptor d, DockWidgetContext c) : base(d, c) { }

        protected override Thickness ContentPadding(double h) => new(Math.Max(2, Math.Round(h * 0.08)), 2, Math.Max(2, Math.Round(h * 0.08)), 2);

        protected override FrameworkElement BuildContent(DockWidgetContext ctx)
        {
            var p = ctx.Palette;
            double h = ctx.TileHeight;
            double big = Math.Max(8, h * 0.3), small = Math.Max(6, h * 0.2);
            TextBlock Big(string s = "") { var t = W.Text(p, s, big, weight: FontWeights.SemiBold); t.HorizontalAlignment = HorizontalAlignment.Center; t.TextTrimming = TextTrimming.None; return t; }
            TextBlock Small() { var t = W.Text(p, "", small, p.SecondaryForeground, FontWeights.SemiBold); t.HorizontalAlignment = HorizontalAlignment.Center; t.TextTrimming = TextTrimming.None; return t; }
            _hh = Big(); _mm = Big();
            _wd = Small(); _dn = Big();
            _glyph = W.Glyph(p, "", small * 1.2, p.Foreground);
            _temp = Big();
            var lineGap = new Thickness(0, -h * 0.06, 0, 0);
            _mm.Margin = lineGap;
            _dn.Margin = lineGap;
            _temp.Margin = new Thickness(0, -h * 0.02, 0, 0);
            UIElement[] contents = [W.Column(_hh, _mm), W.Column(_wd, _dn), W.Column(_glyph, _temp)];
            byte[] alphas = [0x70, 0x48, 0x28];
            double[] stars = [1.15, 1, 1];
            var g = new Grid();
            for (int i = 0; i < 3; i++)
            {
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(stars[i], GridUnitType.Star) });
                _blocks[i] = new Border
                {
                    Background = W.Brush(p.Accent, alphas[i]),
                    CornerRadius = new CornerRadius(Math.Max(4, h * 0.16)),
                    Margin = new Thickness(i == 0 ? 0 : 1.5, 0, 0, 0),
                    Padding = new Thickness(1),
                    Child = Fit(contents[i])
                };
                Grid.SetColumn(_blocks[i], i);
                g.Children.Add(_blocks[i]);
            }
            return g;
        }

        protected override void Refresh(DockWidgetContext ctx)
        {
            var now = ctx.Now;
            string hh = ctx.Config.DockClock24h
                ? now.ToString("HH", CultureInfo.InvariantCulture)
                : now.ToString("%h", CultureInfo.InvariantCulture);
            W.SetTextAnimated(_hh, hh);
            W.SetTextAnimated(_mm, now.ToString("mm", CultureInfo.InvariantCulture));
            string wd = Upper(ctx, now.ToString("ddd", Cul(ctx)));
            if (_wd.Text != wd) _wd.Text = wd;
            W.SetTextAnimated(_dn, now.Day.ToString(CultureInfo.InvariantCulture));
            var wx = Weather(ctx);
            string glyph = wx is null ? "" : DockWidgetMath.WeatherGlyph(wx.Code, wx.IsDay);
            if (_glyph.Text != glyph) _glyph.Text = glyph;
            _glyph.Opacity = wx is null ? 0.45 : 1;
            W.SetTextAnimated(_temp, wx is null ? "—" : Deg(ctx, wx.TempC));
        }

        public override void OnAttached(DockWidgetContext ctx)
        {
            for (int i = 0; i < 3; i++) W.PopIn(_blocks[i], i * 80);
        }
    }
}
