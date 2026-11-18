using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using DinamikAda.App.Services;
using DinamikAda.Core.Abstractions;
using DinamikAda.Core.Config;
using DinamikAda.Core.Widgets;
using DinamikAda.Modules.Productivity;
using IO = System.IO;

namespace DinamikAda.App.Views.DockWidgets;

/// <summary>
/// Productivity and AI widgets: AI limits/ring, day timeline, screen time, pomodoro, timer, stopwatch,
/// todos, notes, calendar and currency.
/// </summary>
internal static class ProductivityWidgetTiles
{
    public static void Register(IDictionary<string, Func<DockWidgetDescriptor, DockWidgetContext, IDockWidgetTile>> map)
    {
        map["ai-limits"] = (d, c) => new AiLimitsTile(d, c);
        map["ai-ring"] = (d, c) => new AiRingTile(d, c);
        map["day-timeline"] = (d, c) => new DayTimelineTile(d, c);
        map["screen-time"] = (d, c) => new ScreenTimeTile(d, c);
        map["pomodoro"] = (d, c) => new PomodoroTile(d, c);
        map["timer"] = (d, c) => new TimerTile(d, c);
        map["stopwatch"] = (d, c) => new StopwatchTile(d, c);
        map["todos"] = (d, c) => new TodosTile(d, c);
        map["notes"] = (d, c) => new NotesTile(d, c);
        map["calendar"] = (d, c) => new CalendarTile(d, c);
        map["currency"] = (d, c) => new CurrencyTile(d, c);
    }

    // =============================================================================================
    // Shared helpers
    // =============================================================================================

    private const string GlyphPlay = "";
    private const string GlyphPause = "";
    private const string GlyphNext = "";
    private const string GlyphReset = "";
    private const string GlyphAdd = "";
    private const string GlyphClose = "";
    private const string GlyphFlag = "";
    private const string GlyphSwap = "";
    private const string GlyphOpen = "";
    private const string GlyphFolder = "";
    private const string GlyphVideo = "";
    private const string GlyphSettings = "";
    private const string GlyphDelete = "";

    private static readonly CultureInfo TrCulture = CultureInfo.GetCultureInfo("tr-TR");
    private static readonly CultureInfo EnCulture = CultureInfo.GetCultureInfo("en-US");
    private static readonly SolidColorBrush DarkInk = W.Brush(Color.FromRgb(0x05, 0x08, 0x10));

    /// <summary>Process-wide state for the timer-owning tiles, so a dock rebuild keeps the same timers.</summary>
    private static class Shared
    {
        public static string? PomodoroId;
        public static bool PomodoroBreak;
        public static bool PomodoroAwaiting;
        public static int PomodoroRounds;
        public static DateTime PomodoroRoundsDate;
        public static readonly List<DateTime> FocusLog = new();

        public static string? TimerId;
        public static TimeSpan TimerDisplayTotal;

        public static string? StopwatchId;
    }

    private static CultureInfo Culture(DockWidgetContext ctx) => ctx.En ? EnCulture : TrCulture;

    private static double Fs(DockWidgetContext ctx, double k, double min) =>
        Math.Max(min, Math.Round(ctx.TileHeight * k * 2) / 2);

    /// <summary>Inner width of the chip (tile width minus the base class padding).</summary>
    private static double ContentWidth(DockWidgetContext ctx, DockWidgetDescriptor d)
    {
        double h = ctx.TileHeight, u = d.WidthUnits;
        double w = Math.Round(h * u + (u > 1 ? (u - 1) * 6 : 0));
        double pad = u > 1.05 ? Math.Round(h * 0.22) : 2;
        return Math.Max(10, w - 2 * pad);
    }

    private static Color ColorOf(Brush b) => b is SolidColorBrush s ? s.Color : Colors.Gray;
    private static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    private static Color Lerp(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromArgb(
            (byte)Math.Round(a.A + (b.A - a.A) * t),
            (byte)Math.Round(a.R + (b.R - a.R) * t),
            (byte)Math.Round(a.G + (b.G - a.G) * t),
            (byte)Math.Round(a.B + (b.B - a.B) * t));
    }

    /// <summary>Remaining-quota colour: green when plenty is left, orange around a third, red when nearly empty.</summary>
    private static Color QuotaColor(DockWidgetPalette p, double remaining) =>
        remaining >= 60 ? p.Positive
        : remaining >= 30 ? Lerp(p.Warning, p.Positive, (remaining - 30) / 30)
        : remaining >= 10 ? Lerp(p.Negative, p.Warning, (remaining - 10) / 20)
        : p.Negative;

    private static Brush OnAccent(DockWidgetPalette p) => p.IsLight ? Brushes.White : DarkInk;

    private static TextBlock Tabular(TextBlock tb)
    {
        System.Windows.Documents.Typography.SetNumeralAlignment(tb, FontNumeralAlignment.Tabular);
        return tb;
    }

    private static TextBlock Wrap(TextBlock tb)
    {
        tb.TextWrapping = TextWrapping.Wrap;
        tb.TextTrimming = TextTrimming.None;
        return tb;
    }

    /// <summary>Makes a sub-element of a tile clickable without the click reaching the tile (and its card).</summary>
    private static void Clickable(FrameworkElement el, Action onClick)
    {
        el.Cursor = Cursors.Hand;
        el.MouseLeftButtonDown += (_, e) => e.Handled = true;
        el.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            onClick();
        };
    }

    private static void After(int ms, Action action)
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            action();
        };
        t.Start();
    }

    private static ScaleTransform EnsureScale(FrameworkElement el)
    {
        if (el.RenderTransform is ScaleTransform st && !st.IsFrozen) return st;
        st = new ScaleTransform(1, 1);
        el.RenderTransformOrigin = new Point(0.5, 0.5);
        el.RenderTransform = st;
        return st;
    }

    private static void PulseForever(FrameworkElement el, double scale = 1.08, double seconds = 0.65)
    {
        var st = EnsureScale(el);
        var anim = new DoubleAnimation(1, scale, TimeSpan.FromSeconds(seconds))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        st.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        st.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
    }

    private static void StopScale(FrameworkElement el)
    {
        if (el.RenderTransform is not ScaleTransform st || st.IsFrozen) return;
        st.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        st.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        st.ScaleX = st.ScaleY = 1;
    }

    /// <summary>Springy "well done" pop.</summary>
    private static void Celebrate(FrameworkElement el)
    {
        var st = EnsureScale(el);
        var a = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(1000) };
        a.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        a.KeyFrames.Add(new EasingDoubleKeyFrame(1.24, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(170)), new QuadraticEase { EasingMode = EasingMode.EaseOut }));
        a.KeyFrames.Add(new EasingDoubleKeyFrame(0.92, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(430)), new QuadraticEase()));
        a.KeyFrames.Add(new EasingDoubleKeyFrame(1.10, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(640)), new QuadraticEase()));
        a.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1000)), new ElasticEase { Oscillations = 1, Springiness = 5 }));
        st.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        st.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }

    private static void Shake(FrameworkElement el)
    {
        if (el.RenderTransform is not TranslateTransform tt || tt.IsFrozen)
        {
            tt = new TranslateTransform();
            el.RenderTransform = tt;
        }
        var a = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(420), RepeatBehavior = new RepeatBehavior(3) };
        double[] xs = [0, -3.5, 3.5, -2.5, 2.5, -1, 0];
        for (int i = 0; i < xs.Length; i++)
            a.KeyFrames.Add(new LinearDoubleKeyFrame(xs[i], KeyTime.FromPercent(i / (double)(xs.Length - 1))));
        tt.BeginAnimation(TranslateTransform.XProperty, a);
    }

    private static void Breathe(UIElement el, double min = 0.3, double seconds = 0.8)
    {
        el.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, min, TimeSpan.FromSeconds(seconds))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        });
    }

    private static void StopBreathe(UIElement el)
    {
        el.BeginAnimation(UIElement.OpacityProperty, null);
        el.Opacity = 1;
    }

    private static void SlideIn(FrameworkElement el, double fromY = -10)
    {
        var tt = new TranslateTransform(0, fromY);
        el.RenderTransform = tt;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(fromY, 0, TimeSpan.FromMilliseconds(300)) { EasingFunction = ease });
        el.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300)) { EasingFunction = ease });
    }

    /// <summary>Adds a strike-through line to a text; when animated the line and the dimming fade in.</summary>
    private static void Strike(TextBlock tb, Brush fg, bool animate)
    {
        var brush = new SolidColorBrush(ColorOf(fg)) { Opacity = animate ? 0 : 1 };
        var deco = new TextDecoration(TextDecorationLocation.Strikethrough, new Pen(brush, 1.2), 0,
            TextDecorationUnit.FontRecommended, TextDecorationUnit.FontRecommended);
        tb.TextDecorations = new TextDecorationCollection { deco };
        if (animate)
        {
            brush.BeginAnimation(Brush.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)) { BeginTime = TimeSpan.FromMilliseconds(140) });
            tb.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, 0.5, TimeSpan.FromMilliseconds(420)) { BeginTime = TimeSpan.FromMilliseconds(140) });
        }
        else
        {
            tb.BeginAnimation(UIElement.OpacityProperty, null);
            tb.Opacity = 0.5;
        }
    }

    private static void ClearStrike(TextBlock tb)
    {
        tb.TextDecorations = null;
        tb.BeginAnimation(UIElement.OpacityProperty, null);
        tb.Opacity = 1;
    }

    private static string Dur(DockWidgetContext ctx, TimeSpan span) => DockWidgetMath.FormatDuration(span, ctx.Lang);
    private static string Dur(DockWidgetContext ctx, double seconds) =>
        DockWidgetMath.FormatDuration(TimeSpan.FromSeconds(double.IsFinite(seconds) ? Math.Max(0, seconds) : 0), ctx.Lang);

    private static string DayAbbrev(DockWidgetContext ctx, DateTime d) =>
        Culture(ctx).DateTimeFormat.GetAbbreviatedDayName(d.DayOfWeek);

    /// <summary>mm:ss.t for laps (h:mm:ss over an hour).</summary>
    private static string FormatLap(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        if (t.TotalHours >= 1) return DockWidgetMath.FormatTimer(t);
        return $"{(int)t.TotalMinutes:00}:{t.Seconds:00}.{t.Milliseconds / 100}";
    }

    /// <summary>A mutable brush whose colour glides to new targets (used by gauges whose colour shifts).</summary>
    private sealed class LiveBrush
    {
        private Color _target;
        public SolidColorBrush Brush { get; }

        public LiveBrush(Color c)
        {
            _target = c;
            Brush = new SolidColorBrush(c);
        }

        public void To(Color c, double ms = 650)
        {
            if (_target == c) return;
            _target = c;
            Brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(c, TimeSpan.FromMilliseconds(ms))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        }
    }

    /// <summary>Finds the timer a tile owns by id, or adopts a matching one when it owns none yet.</summary>
    private static TimerSnapshot? FindTimer(DockWidgetContext ctx, ref string? id, Func<TimerSnapshot, bool> adopt)
    {
        IReadOnlyList<TimerSnapshot> all;
        try { all = ctx.Orch.Timers.GetTimers(); }
        catch { return null; }
        if (id is not null)
        {
            foreach (var t in all)
                if (t.Id == id) return t;
            id = null;
            return null;
        }
        foreach (var t in all)
        {
            if (adopt(t))
            {
                id = t.Id;
                return t;
            }
        }
        return null;
    }

    /// <summary>A pill whose button content can be swapped (start ↔ pause) without rebuilding the card.</summary>
    private sealed class ButtonSlot
    {
        private int _state = int.MinValue;
        public Border Host { get; } = new() { Margin = new Thickness(0) };

        public void Set(int state, Func<Border> build)
        {
            if (state == _state) return;
            _state = state;
            Host.Child = build();
        }
    }

    // =============================================================================================
    // Bars chart used by screen time (staggered grow-in)
    // =============================================================================================

    private sealed class MiniBars : FrameworkElement
    {
        public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
            nameof(Progress), typeof(double), typeof(MiniBars),
            new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

        public double Progress
        {
            get => (double)GetValue(ProgressProperty);
            set => SetValue(ProgressProperty, value);
        }

        private double[] _values = [];
        private string[]? _labels;
        private double? _max;
        private int _highlight = -1;
        private Typeface? _typeface;

        public Brush BarBrush { get; set; } = Brushes.DeepSkyBlue;
        public Brush HighlightBrush { get; set; } = Brushes.White;
        public Brush? TrackBrush { get; set; }
        public Brush LabelBrush { get; set; } = Brushes.Gray;
        public FontFamily LabelFont { get; set; } = new("Segoe UI");
        public double LabelSize { get; set; } = 9;

        public void SetData(double[] values, double? max, int highlight, string[]? labels = null)
        {
            bool same = highlight == _highlight && max == _max && values.AsSpan().SequenceEqual(_values) &&
                        ((labels is null && _labels is null) || (labels is not null && _labels is not null && labels.SequenceEqual(_labels)));
            if (same) return;
            _values = (double[])values.Clone();
            _labels = labels is null ? null : (string[])labels.Clone();
            _max = max;
            _highlight = highlight;
            InvalidateVisual();
        }

        public void Regrow(double ms = 760)
        {
            BeginAnimation(ProgressProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(ms))
            {
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut }
            });
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            int n = _values.Length;
            if (n == 0 || w < 4 || h < 3) return;
            double labelH = _labels is not null ? LabelSize * 1.35 + 2 : 0;
            double chartH = Math.Max(2, h - labelH);
            double slot = w / n;
            double barW = Math.Max(1, slot * 0.64);
            double rad = Math.Min(barW / 2, 2.5);
            double max = _max ?? 0;
            foreach (var v in _values) if (double.IsFinite(v) && v > max) max = v;
            if (max <= 1e-9) max = 1;
            const double stagger = 0.9;
            double prog = Math.Clamp(Progress, 0, 1);

            for (int i = 0; i < n; i++)
            {
                double x = i * slot + (slot - barW) / 2;
                if (TrackBrush is not null)
                    dc.DrawRoundedRectangle(TrackBrush, null, new Rect(x, 0, barW, chartH), rad, rad);
                double v = double.IsFinite(_values[i]) ? Math.Max(0, _values[i]) : 0;
                double t = Math.Clamp(prog * (1 + stagger) - stagger * i / Math.Max(1, n - 1), 0, 1);
                t = 1 - Math.Pow(1 - t, 3);
                double bh = chartH * Math.Min(1, v / max) * t;
                if (v > 0 && bh < 1.2 && t > 0.99) bh = 1.2;
                if (bh <= 0.1) continue;
                dc.DrawRoundedRectangle(i == _highlight ? HighlightBrush : BarBrush, null,
                    new Rect(x, chartH - bh, barW, bh), rad, rad);
            }

            if (_labels is not null)
            {
                _typeface ??= new Typeface(LabelFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
                double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
                for (int i = 0; i < Math.Min(n, _labels.Length); i++)
                {
                    if (string.IsNullOrEmpty(_labels[i])) continue;
                    var ft = new FormattedText(_labels[i], CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                        _typeface, LabelSize, i == _highlight ? HighlightBrush : LabelBrush, dpi);
                    double x = Math.Clamp(i * slot + slot / 2 - ft.Width / 2, 0, Math.Max(0, w - ft.Width));
                    dc.DrawText(ft, new Point(x, chartH + 2));
                }
            }
        }
    }

    // =============================================================================================
    // Screen-time data (shared by the day timeline and the screen-time tile)
    // =============================================================================================

    private static readonly string[] Kinds =
    [
        AppKindClassifier.Work, AppKindClassifier.Browser, AppKindClassifier.Chat, AppKindClassifier.Media, AppKindClassifier.Other
    ];

    private static Color KindColor(DockWidgetPalette p, string kind) => kind switch
    {
        AppKindClassifier.Work => p.Accent,
        AppKindClassifier.Browser => p.Positive,
        AppKindClassifier.Chat => p.Warning,
        AppKindClassifier.Media => p.Negative,
        _ => WithAlpha(ColorOf(p.Foreground), 0x78)
    };

    private static string KindName(DockWidgetContext ctx, string kind) => kind switch
    {
        AppKindClassifier.Work => ctx.T("İş", "Work"),
        AppKindClassifier.Browser => ctx.T("Tarayıcı", "Browser"),
        AppKindClassifier.Chat => ctx.T("Sohbet", "Chat"),
        AppKindClassifier.Media => ctx.T("Medya", "Media"),
        _ => ctx.T("Diğer", "Other")
    };

    private static ScreenTimeDay? TodayData(DockWidgetContext ctx)
    {
        ScreenTimeDay? d = null;
        try { d = ctx.Data.ScreenTime.GetDay(ctx.Now); }
        catch { /* ledger unavailable: treat as no data */ }
        if ((d is null || d.TotalSeconds < 1) && ctx.IsAuditMode) d = SampleDay(ctx.Now);
        return d;
    }

    /// <summary>Plausible screen-time day for screenshots (audit mode only), cut off at the current time.</summary>
    private static ScreenTimeDay SampleDay(DateTime now)
    {
        double[] work = [0, 0, 0, 0, 0, 0, 0, 0, 600, 2600, 2900, 2700, 700, 2300, 2800, 2500, 2000, 1200, 0, 300, 0, 0, 0, 0];
        double[] browser = [0, 0, 0, 0, 0, 0, 0, 0, 300, 500, 400, 600, 900, 700, 500, 800, 900, 700, 600, 900, 1200, 800, 300, 0];
        double[] chat = [0, 0, 0, 0, 0, 0, 0, 0, 0, 200, 0, 300, 600, 0, 300, 0, 400, 0, 500, 0, 600, 0, 0, 0];
        double[] media = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1100, 0, 0, 0, 0, 0, 900, 1500, 1600, 2400, 1200, 0];
        var day = new ScreenTimeDay { Date = ScreenTimeLedger.DateKey(now) };
        var kinds = new Dictionary<string, double[]>
        {
            [AppKindClassifier.Work] = work,
            [AppKindClassifier.Browser] = browser,
            [AppKindClassifier.Chat] = chat,
            [AppKindClassifier.Media] = media
        };
        foreach (var (k, src) in kinds)
        {
            var arr = new double[24];
            for (int i = 0; i < 24; i++)
            {
                double f = i < now.Hour ? 1 : i == now.Hour ? now.Minute / 60.0 : 0;
                arr[i] = src[i] * f;
                day.HourSeconds[i] += arr[i];
            }
            day.KindHourSeconds[k] = arr;
        }
        double total = day.TotalSeconds;
        (string App, double Share)[] apps =
        [
            ("Code", 0.31), ("chrome", 0.22), ("Spotify", 0.14), ("Discord", 0.09), ("WindowsTerminal", 0.08), ("Figma", 0.06), ("explorer", 0.04)
        ];
        foreach (var (app, share) in apps) day.AppSeconds[app] = total * share;
        return day;
    }

    private static double[] SampleWeek(double today) => [21000, 26500, 19800, 30200, 24800, 12400, today];

    // =============================================================================================
    // AI
    // =============================================================================================

    private sealed record AiPlan(string Id, string Label, double Pct, bool HasData, DateTimeOffset? ResetUtc);

    private static AiPlan ToPlan(AiQuotaSlotSnapshot s) => new(
        s.Id,
        string.IsNullOrWhiteSpace(s.Label) ? s.Provider : s.Label,
        double.IsFinite(s.EffectiveRemainingPercent) ? Math.Clamp(s.EffectiveRemainingPercent, 0, 100) : 0,
        s.HasQuotaData && !s.QuotaAwaitingRefresh,
        s.NextResetUtc ?? s.BlockingResetUtc ?? s.WeeklyResetUtc);

    private static List<AiPlan> LoadAiPlans(DockWidgetContext ctx, out AiPlan? attention)
    {
        var list = new List<AiPlan>();
        attention = null;
        try
        {
            var svc = ctx.Orch.AiQuota;
            foreach (var s in svc.GetSnapshots()) list.Add(ToPlan(s));
            var a = svc.GetSummary().AttentionSlot;
            if (a is not null) attention = list.FirstOrDefault(x => x.Id == a.Id) ?? ToPlan(a);
        }
        catch
        {
            // quota tracker not ready: show the empty state
        }
        if (list.Count == 0 && ctx.IsAuditMode)
        {
            var now = new DateTimeOffset(ctx.Now);
            list.Add(new AiPlan("audit-1", "Claude Max", 38, true, now.AddMinutes(87)));
            list.Add(new AiPlan("audit-2", "ChatGPT Plus", 81, true, now.AddHours(3).AddMinutes(12)));
        }
        attention ??= list.FirstOrDefault();
        return list;
    }

    private static List<AiPlan> PickTwo(List<AiPlan> list, AiPlan? attention)
    {
        var result = new List<AiPlan>(2);
        if (attention is not null) result.Add(attention);
        foreach (var p in list)
        {
            if (result.Count >= 2) break;
            if (result.Any(x => x.Id == p.Id)) continue;
            result.Add(p);
        }
        return result;
    }

    private static string ResetIn(DockWidgetContext ctx, AiPlan plan)
    {
        if (plan.ResetUtc is not { } r) return plan.HasData ? W.Pct(plan.Pct) : "—";
        var left = r - new DateTimeOffset(ctx.Now);
        return left <= TimeSpan.Zero ? ctx.T("şimdi", "now") : Dur(ctx, left);
    }

    private static void OpenAiTab(DockWidgetContext ctx)
    {
        if (ctx.IsAuditMode) return;
        ctx.CloseCard();
        try { ctx.Orch.Coordinator.RequestNavigateTab(ExpandedTabId.AiQuota); }
        catch { ctx.Orch.RequestOpenSettings(); }
    }

    /// <summary>Two AI plans on one tile: name, remaining bar and time to reset.</summary>
    private sealed class AiLimitsTile : DockWidgetTileBase
    {
        private sealed class PlanRow
        {
            public FrameworkElement Root = null!;
            public TextBlock Label = null!;
            public BarGauge Bar = null!;
            public LiveBrush Fill = null!;
            public TextBlock Reset = null!;
            public TextBlock? Pct;
        }

        private readonly PlanRow[] _rows = new PlanRow[2];
        private TextBlock _empty = null!;
        private StackPanel? _cardList;
        private string _cardKey = "\0";
        private readonly List<PlanRow> _cardRows = new();

        public AiLimitsTile(DockWidgetDescriptor d, DockWidgetContext c) : base(d, c) { }

        protected override FrameworkElement BuildContent(DockWidgetContext ctx)
        {
            var p = ctx.Palette;
            double h = ctx.TileHeight;
            double w = ContentWidth(ctx, Descriptor);
            double fs = Fs(ctx, 0.25, 7.5);
            double barH = Math.Max(3, Math.Round(h * 0.12));
            var g = new Grid { VerticalAlignment = VerticalAlignment.Center };
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (int i = 0; i < 2; i++)
            {
                var row = MakeTileRow(p, fs, barH, w * 0.33, w * 0.36, Math.Max(3, h * 0.12));
                row.Root.Margin = new Thickness(0, i == 0 ? 0 : Math.Max(1, Math.Round(h * 0.07)), 0, 0);
                Grid.SetRow(row.Root, i);
                g.Children.Add(row.Root);
                _rows[i] = row;
            }
            _empty = W.Text(p, ctx.T("AI hesabı yok", "No AI accounts"), fs, p.SecondaryForeground);
            _empty.Visibility = Visibility.Collapsed;
            Grid.SetRowSpan(_empty, 2);
            g.Children.Add(_empty);
            return g;
        }

        private static PlanRow MakeTileRow(DockWidgetPalette p, double fs, double barH, double labelMax, double resetMax, double gap)
        {
            var r = new PlanRow();
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            r.Label = W.Text(p, "", fs, p.Foreground, FontWeights.SemiBold);
            r.Label.MaxWidth = labelMax;
            r.Label.Margin = new Thickness(0, 0, gap, 0);
            r.Fill = new LiveBrush(p.Positive);
            r.Bar = new BarGauge { Height = barH, Track = p.Track, Fill = r.Fill.Brush, VerticalAlignment = VerticalAlignment.Center, MinWidth = 6 };
            Grid.SetColumn(r.Bar, 1);
            r.Reset = Tabular(W.Text(p, "", Math.Max(7, fs - 0.5), p.SecondaryForeground));
            r.Reset.MaxWidth = resetMax;
            r.Reset.Margin = new Thickness(gap, 0, 0, 0);
            r.Reset.TextAlignment = TextAlignment.Right;
            Grid.SetColumn(r.Reset, 2);
            g.Children.Add(r.Label);
            g.Children.Add(r.Bar);
            g.Children.Add(r.Reset);
            r.Root = g;
            return r;
        }

        private static void Apply(DockWidgetContext ctx, PlanRow row, AiPlan plan, string resetText)
        {
            row.Root.Visibility = Visibility.Visible;
            if (row.Label.Text != plan.Label) row.Label.Text = plan.Label;
            row.Bar.AnimateTo(plan.HasData ? plan.Pct / 100 : 0, 900);
            row.Fill.To(plan.HasData ? QuotaColor(ctx.Palette, plan.Pct) : ColorOf(ctx.Palette.TertiaryForeground));
            W.SetTextAnimated(row.Reset, resetText);
            if (row.Pct is not null)
            {
                W.SetTextAnimated(row.Pct, plan.HasData ? W.Pct(plan.Pct) : "—");
                row.Pct.Foreground = row.Fill.Brush;
            }
        }

        protected override void Refresh(DockWidgetContext ctx)
        {
            var list = LoadAiPlans(ctx, out var attention);
            var two = PickTwo(list, attention);
            _empty.Visibility = two.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            for (int i = 0; i < 2; i++)
            {
                if (i < two.Count) Apply(ctx, _rows[i], two[i], ResetIn(ctx, two[i]));
                else _rows[i].Root.Visibility = Visibility.Collapsed;
            }
        }

        protected override FrameworkElement? CreateCard(DockWidgetContext ctx)
        {
            var p = ctx.Palette;
            _cardList = new StackPanel();
            _cardKey = "\0";
            _cardRows.Clear();
            var buttons = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            buttons.Children.Add(W.Button(p, ctx.T("AI sekmesini aç", "Open AI tab"), () => OpenAiTab(ctx), GlyphOpen, primary: true));
            return W.Card(p, Descriptor.Glyph, Descriptor.Name(ctx.Lang), W.Column(_cardList, buttons), 300);
        }

        protected override void RefreshCard(DockWidgetContext ctx)
        {
            if (_cardList is null) return;
            var p = ctx.Palette;
            var list = LoadAiPlans(ctx, out _);
            string key = string.Join("|", list.Select(x => x.Id));
            if (key != _cardKey)
            {
                _cardKey = key;
                _cardList.Children.Clear();
                _cardRows.Clear();
                if (list.Count == 0)
                {
                    _cardList.Children.Add(Wrap(W.Text(p, ctx.T("Henüz AI hesabı yok. AI sekmesinden ekleyebilirsin.",
                        "No AI accounts yet. You can add one from the AI tab."), 12.5, p.SecondaryForeground, ui: false)));
                }
                foreach (var _ in list)
                {
                    var r = new PlanRow();
                    var head = new DockPanel();
                    r.Pct = Tabular(W.Text(p, "", 13, p.Foreground, FontWeights.SemiBold));
                    DockPanel.SetDock(r.Pct, Dock.Right);
                    head.Children.Add(r.Pct);
                    r.Label = W.Text(p, "", 13, p.Foreground, FontWeights.SemiBold);
                    head.Children.Add(r.Label);
                    r.Fill = new LiveBrush(p.Positive);
                    r.Bar = new BarGauge { Height = 6, Track = p.Track, Fill = r.Fill.Brush, Margin = new Thickness(0, 5, 0, 4) };
                    r.Reset = Tabular(W.Text(p, "", 11.5, p.SecondaryForeground, ui: false));
                    var block = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
                    block.Children.Add(head);
                    block.Children.Add(r.Bar);
                    block.Children.Add(r.Reset);
                    r.Root = block;
                    _cardList.Children.Add(block);
                    _cardRows.Add(r);
                }
            }
            for (int i = 0; i < Math.Min(list.Count, _cardRows.Count); i++)
            {
                var plan = list[i];
                string detail;
                if (plan.ResetUtc is { } reset)
                {
                    var local = reset.ToLocalTime();
                    var left = reset - new DateTimeOffset(ctx.Now);
                    string at = (local.Date == ctx.Now.Date ? local.ToString("HH:mm", Culture(ctx)) : local.ToString("d MMM HH:mm", Culture(ctx)));
                    detail = left <= TimeSpan.Zero
                        ? ctx.T("Yenilendi", "Reset")
                        : ctx.T($"Yenilenme {at} · {Dur(ctx, left)} sonra", $"Resets {at} · in {Dur(ctx, left)}");
                }
                else
                {
                    detail = ctx.T("Yenilenme zamanı bilinmiyor", "Reset time unknown");
                }
                Apply(ctx, _cardRows[i], plan, detail);
            }
        }
    }

    /// <summary>The attention slot's remaining quota as a ring; it pulses when nearly empty.</summary>
    private sealed class AiRingTile : DockWidgetTileBase
    {
        private Grid _host = null!;
        private RingGauge _ring = null!;
        private LiveBrush _fill = null!;
        private TextBlock _value = null!;
        private bool _attached;
        private bool _pulsing;
        private string _tip = "";

        public AiRingTile(DockWidgetDescriptor d, DockWidgetContext c) : base(d, c) { }

        protected override bool ShowChrome => false;

        protected override FrameworkElement BuildContent(DockWidgetContext ctx)
        {
            var p = ctx.Palette;
            double h = ctx.TileHeight;
            _fill = new LiveBrush(p.Positive);
            _ring = W.Ring(p, h - 2, Math.Max(2.5, h * 0.1), p.Positive);
            _ring.Fill = _fill.Brush;
            _value = Tabular(W.Text(p, "", Math.Max(8.5, h * 0.27), weight: FontWeights.SemiBold));
            _value.HorizontalAlignment = HorizontalAlignment.Center;
            _host = new Grid();
            _host.Children.Add(_ring);
            _host.Children.Add(_value);
            return _host;
        }

        protected override void Refresh(DockWidgetContext ctx)
        {
            LoadAiPlans(ctx, out var a);
            if (a is null || !a.HasData)
            {
                _ring.AnimateTo(0);
                _fill.To(ColorOf(ctx.Palette.TertiaryForeground));
                W.SetTextAnimated(_value, "—");
                SetPulse(false);
                SetTip(a is null ? ctx.T("AI hesabı yok", "No AI accounts") : a.Label);
                return;
            }
            _ring.AnimateTo(a.Pct / 100.0, 900);
            _fill.To(QuotaColor(ctx.Palette, a.Pct));
            W.SetTextAnimated(_value, ((int)Math.Round(a.Pct)).ToString(CultureInfo.InvariantCulture));
            SetPulse(a.Pct < 15);
            SetTip($"{a.Label} · {W.Pct(a.Pct)} · {ResetIn(ctx, a)}");
        }

        private void SetTip(string tip)
        {
            if (tip == _tip) return;
            _tip = tip;
            Chip.ToolTip = tip;
        }

        private void SetPulse(bool on)
        {
            if (on == _pulsing) return;
            _pulsing = on;
            if (on && _attached) PulseForever(_host, 1.09, 0.55);
            else if (!on) StopScale(_host);
        }

        public override void OnAttached(DockWidgetContext ctx)
        {
            _attached = true;
            if (_pulsing) PulseForever(_host, 1.09, 0.55);
        }

        public override void OnDetached()
        {
            _attached = false;
            StopScale(_host);
        }

        public override bool OnPrimaryClick(DockWidgetContext ctx)
        {
            OpenAiTab(ctx);
            return true;
        }
    }

    // =============================================================================================
    // Day timeline
    // =============================================================================================

    /// <summary>24-hour strip: hour segments by dominant app kind, markers and a gliding "now" cursor.</summary>
    private sealed class TimelineStrip : FrameworkElement
    {
        public static readonly DependencyProperty GrowProperty = DependencyProperty.Register(
            nameof(Grow), typeof(double), typeof(TimelineStrip),
            new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty NowFractionProperty = DependencyProperty.Register(
            nameof(NowFraction), typeof(double), typeof(TimelineStrip),
            new FrameworkPropertyMetadata(-1.0, FrameworkPropertyMetadataOptions.AffectsRender));

        public double Grow
        {
            get => (double)GetValue(GrowProperty);
            set => SetValue(GrowProperty, value);
        }

        public double NowFraction
        {
            get => (double)GetValue(NowFractionProperty);
            set => SetValue(NowFractionProperty, value);
        }

        public Color?[] HourColors { get; } = new Color?[24];
        public double[] HourLevels { get; } = new double[24];
        public List<double> FocusMarks { get; } = new();
        public List<double> SunTicks { get; } = new();
        public double? MeetingStart { get; set; }
        public double? MeetingEnd { get; set; }

        public Brush TrackBrush { get; set; } = Brushes.DimGray;
        public Brush CursorBrush { get; set; } = Brushes.White;
        public Brush LabelBrush { get; set; } = Brushes.Gray;
        public Brush FocusBrush { get; set; } = Brushes.Tomato;
        public Brush MeetingBrush { get; set; } = Brushes.DeepSkyBlue;
        public Brush SunBrush { get; set; } = Brushes.Orange;
        public FontFamily LabelFont { get; set; } = new("Segoe UI");
        public double LabelSize { get; set; } = 7;
        public bool ShowLabels { get; set; } = true;

        private readonly Dictionary<Color, SolidColorBrush> _brushes = new();
        private Typeface? _typeface;
        private double _cursorTarget = -1;

        private SolidColorBrush BrushFor(Color c)
        {
            if (!_brushes.TryGetValue(c, out var b)) _brushes[c] = b = W.Brush(c);
            return b;
        }

        public void GlideTo(double fraction, bool animate)
        {
            fraction = Math.Clamp(fraction, 0, 1);
            if (_cursorTarget >= 0 && Math.Abs(fraction - _cursorTarget) < 0.0002) return;
            bool first = _cursorTarget < 0;
            _cursorTarget = fraction;
            if (!animate)
            {
                BeginAnimation(NowFractionProperty, null);
                NowFraction = fraction;
                return;
            }
            BeginAnimation(NowFractionProperty, new DoubleAnimation(fraction, TimeSpan.FromMilliseconds(first ? 1200 : 800))
            {
                From = first ? 0 : null,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            }, HandoffBehavior.SnapshotAndReplace);
        }

        public void GrowIn(double ms = 1000)
        {
            BeginAnimation(GrowProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(ms))
            {
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut }
            });
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w < 24 || h < 8) return;
            double labelH = ShowLabels ? Math.Ceiling(LabelSize * 1.3) : 0;
            double markH = Math.Max(3, Math.Round(h * 0.2));
            double stripY = markH + 1;
            double stripH = Math.Max(3, h - labelH - stripY - 1);
            double sw = w / 24;
            double gap = sw >= 4 ? 1 : 0.5;
            double segW = Math.Max(0.5, sw - gap);
            double rad = Math.Min(2, segW / 2);
            double grow = Math.Clamp(Grow, 0, 1);

            for (int i = 0; i < 24; i++)
            {
                double x = i * sw + gap / 2;
                dc.DrawRoundedRectangle(TrackBrush, null, new Rect(x, stripY, segW, stripH), rad, rad);
                if (HourColors[i] is not { } c) continue;
                double t = Math.Clamp(grow * 1.6 - 0.6 * i / 23.0, 0, 1);
                t = 1 - Math.Pow(1 - t, 3);
                double level = 0.3 + 0.7 * Math.Clamp(HourLevels[i], 0, 1);
                double fh = stripH * level * t;
                if (fh > 0.4) dc.DrawRoundedRectangle(BrushFor(c), null, new Rect(x, stripY + stripH - fh, segW, fh), rad, rad);
            }

            var sunPen = new Pen(SunBrush, 1);
            foreach (var s in SunTicks)
            {
                double x = Math.Round(Math.Clamp(s, 0, 1) * w) + 0.5;
                dc.DrawLine(sunPen, new Point(x, stripY - 1), new Point(x, stripY + stripH + 1));
                dc.DrawEllipse(SunBrush, null, new Point(x, markH * 0.5), 1.7, 1.7);
            }

            if (MeetingStart is { } ms)
            {
                double x = Math.Clamp(ms, 0, 1) * w;
                if (MeetingEnd is { } me && me > ms)
                {
                    double xe = Math.Clamp(me, 0, 1) * w;
                    dc.DrawRoundedRectangle(MeetingBrush, null, new Rect(x, markH * 0.38, Math.Max(2, xe - x), Math.Max(1.4, markH * 0.26)), 0.8, 0.8);
                }
                var tri = new StreamGeometry();
                using (var g = tri.Open())
                {
                    double tw = Math.Max(2.4, markH * 0.55);
                    g.BeginFigure(new Point(x - tw, 0.3), true, true);
                    g.LineTo(new Point(x + tw, 0.3), true, false);
                    g.LineTo(new Point(x, markH), true, false);
                }
                tri.Freeze();
                dc.DrawGeometry(MeetingBrush, null, tri);
            }

            double fr = Math.Max(1.4, markH * 0.3);
            foreach (var f in FocusMarks)
                dc.DrawEllipse(FocusBrush, null, new Point(Math.Clamp(f, 0, 1) * w, markH * 0.5), fr, fr);

            if (ShowLabels)
            {
                _typeface ??= new Typeface(LabelFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
                double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
                foreach (int hr in new[] { 0, 6, 12, 18, 24 })
                {
                    var ft = new FormattedText(hr.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight, _typeface, LabelSize, LabelBrush, dpi);
                    double x = Math.Clamp(hr / 24.0 * w - ft.Width / 2, 0, Math.Max(0, w - ft.Width));
                    dc.DrawText(ft, new Point(x, h - labelH));
                }
            }

            if (NowFraction >= 0)
            {
                double x = Math.Clamp(NowFraction, 0, 1) * w;
                var pen = new Pen(CursorBrush, 1.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                dc.DrawLine(pen, new Point(x, 1.5), new Point(x, stripY + stripH + 1.5));
                dc.DrawEllipse(CursorBrush, null, new Point(x, 1.8), 2.1, 2.1);
            }
        }
    }

    private sealed class DayTimelineTile : DockWidgetTileBase
    {
        private TimelineStrip _strip = null!;
        private TimelineStrip? _cardStrip;
        private StackPanel? _legend;
        private TextBlock? _cardTotal;
        private DateTime _cardBuiltFor = DateTime.MinValue;
        private ScreenTimeDay? _day;
        private DateTime _dataAt = DateTime.MinValue;
        private string _dataKey = "";

        public DayTimelineTile(DockWidgetDescriptor d, DockWidgetContext c) : base(d, c) { }

        private static TimelineStrip NewStrip(DockWidgetContext ctx, bool labels, double labelSize)
        {
            var p = ctx.Palette;
            return new TimelineStrip
            {
                TrackBrush = p.Track,
                CursorBrush = p.Foreground,
                LabelBrush = p.TertiaryForeground,
                FocusBrush = W.Brush(p.Negative),
                MeetingBrush = W.Brush(p.Accent),
                SunBrush = W.Brush(p.Warning),
                LabelFont = p.UiFont,
                LabelSize = labelSize,
                ShowLabels = labels,
                SnapsToDevicePixels = true
            };
        }

        protected override FrameworkElement BuildContent(DockWidgetContext ctx)
        {
            double h = ctx.TileHeight;
            _strip = NewStrip(ctx, h >= 24, Fs(ctx, 0.2, 6.5));
            return _strip;
        }

        protected override void Refresh(DockWidgetContext ctx)
        {
            var now = ctx.Now;
            string key = ScreenTimeLedger.DateKey(now);
            if (key != _dataKey || now < _dataAt || (now - _dataAt).TotalSeconds >= 30)
            {
                _dataKey = key;
                _dataAt = now;
                _day = TodayData(ctx);
                Fill(ctx, _strip, _day);
            }
            _strip.GlideTo(now.TimeOfDay.TotalHours / 24.0, !ctx.IsAuditMode);
        }

        public override void OnAttached(DockWidgetContext ctx)
        {
            if (!ctx.IsAuditMode) _strip.GrowIn();
        }

        private static void Fill(DockWidgetContext ctx, TimelineStrip strip, ScreenTimeDay? day)
        {
            var p = ctx.Palette;
            for (int i = 0; i < 24; i++)
            {
                strip.HourColors[i] = null;
                strip.HourLevels[i] = 0;
                if (day is null || day.HourSeconds.Length < 24) continue;
                double total = day.HourSeconds[i];
                if (!double.IsFinite(total) || total < 30) continue;
                string best = AppKindClassifier.Other;
                double bestSec = -1;
                foreach (var k in Kinds)
                {
                    if (day.KindHourSeconds.TryGetValue(k, out var arr) && arr.Length > i && arr[i] > bestSec)
                    {
                        bestSec = arr[i];
                        best = k;
                    }
                }
                strip.HourColors[i] = KindColor(p, bestSec > 0 ? best : AppKindClassifier.Other);
                strip.HourLevels[i] = total / 3600.0;
            }

            var now = ctx.Now;
            strip.FocusMarks.Clear();
            int focusMin = Math.Clamp(ctx.Config.PomodoroFocusMinutes, 1, 180);
            foreach (var done in Shared.FocusLog)
                if (done.Date == now.Date) strip.FocusMarks.Add(Math.Max(0, (done.TimeOfDay.TotalMinutes - focusMin) / 1440.0));
            try
            {
                foreach (var t in ctx.Orch.Timers.GetTimers())
                {
                    if (!t.IsPomodoro || t.IsCompleted) continue;
                    var start = now - (t.Total - t.RemainingOrElapsed);
                    if (start.Date == now.Date) strip.FocusMarks.Add(start.TimeOfDay.TotalHours / 24.0);
                }
            }
            catch { /* timers unavailable */ }
            if (strip.FocusMarks.Count == 0 && ctx.IsAuditMode)
            {
                foreach (var hr in new[] { 9.5, 10.1, 14.2, 15.0 })
                    if (hr < now.TimeOfDay.TotalHours) strip.FocusMarks.Add(hr / 24.0);
            }

            strip.MeetingStart = strip.MeetingEnd = null;
            var m = CalendarTile.Meeting(ctx);
            if (m is not null && !m.IsAllDay)
            {
                var s = m.StartUtc.ToLocalTime().DateTime;
                var e = m.EndUtc.ToLocalTime().DateTime;
                if (s.Date == now.Date)
                {
                    strip.MeetingStart = s.TimeOfDay.TotalHours / 24.0;
                    strip.MeetingEnd = e.Date == now.Date ? e.TimeOfDay.TotalHours / 24.0 : 1.0;
                }
            }

            strip.SunTicks.Clear();
            var wx = ctx.Data.Weather;
            if (wx is not null)
            {
                if (wx.Sunrise.Date == now.Date) strip.SunTicks.Add(wx.Sunrise.TimeOfDay.TotalHours / 24.0);
                if (wx.Sunset.Date == now.Date) strip.SunTicks.Add(wx.Sunset.TimeOfDay.TotalHours / 24.0);
            }
            else if (ctx.IsAuditMode)
            {
                strip.SunTicks.Add(7.4 / 24.0);
                strip.SunTicks.Add(16.9 / 24.0);
            }
            strip.InvalidateVisual();
        }

        protected override FrameworkElement? CreateCard(DockWidgetContext ctx)
        {
            var p = ctx.Palette;
            _cardStrip = NewStrip(ctx, true, 9.5);
            _cardStrip.Height = 58;
            _cardStrip.Margin = new Thickness(0, 0, 0, 12);
            if (!ctx.IsAuditMode) _cardStrip.GrowIn(900);
            _legend = new StackPanel();
            _cardTotal = Tabular(W.Text(p, "", 13, p.SecondaryForeground, FontWeights.SemiBold));
            _cardBuiltFor = DateTime.MinValue;
            return W.Card(p, Descriptor.Glyph, Descriptor.Name(ctx.Lang), W.Column(_cardStrip, _legend), 320, _cardTotal);
        }

        protected override void RefreshCard(DockWidgetContext ctx)
        {
            if (_cardStrip is null || _legend is null || _cardTotal is null) return;
            var p = ctx.Palette;
            _cardStrip.GlideTo(ctx.Now.TimeOfDay.TotalHours / 24.0, !ctx.IsAuditMode);
            if (_cardBuiltFor == _dataAt) return;
            _cardBuiltFor = _dataAt;
            Fill(ctx, _cardStrip, _day);
            double total = _day?.TotalSeconds ?? 0;
            _cardTotal.Text = Dur(ctx, total);
            _legend.Children.Clear();
            if (!ctx.Config.ScreenTimeEnabled && !ctx.IsAuditMode)
            {
                _legend.Children.Add(Wrap(W.Text(p, ctx.T("Ekran süresi takibi kapalı; saat renkleri boş görünür.",
                    "Screen-time tracking is off, so the hours stay empty."), 12, p.SecondaryForeground, ui: false)));
            }
            foreach (var k in Kinds)
            {
                double sec = 0;
                if (_day is not null && _day.KindHourSeconds.TryGetValue(k, out var arr)) sec = arr.Where(double.IsFinite).Sum();
                var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
                var dot = new Ellipse { Width = 9, Height = 9, Fill = W.Brush(KindColor(p, k)), Margin = new Thickness(0, 0, 9, 0), VerticalAlignment = VerticalAlignment.Center };
                DockPanel.SetDock(dot, Dock.Left);
                row.Children.Add(dot);
                string pct = total > 0 ? $"  {W.Pct(sec / total * 100)}" : "";
                var v = Tabular(W.Text(p, Dur(ctx, sec) + pct, 12.5, p.Foreground, FontWeights.SemiBold));
                DockPanel.SetDock(v, Dock.Right);
                row.Children.Add(v);
                row.Children.Add(W.Text(p, KindName(ctx, k), 12.5, p.SecondaryForeground, ui: false));
                _legend.Children.Add(row);
            }
            _legend.Children.Add(new Border { Height = 1, Background = p.Track, Margin = new Thickness(0, 8, 0, 6) });
            int rounds = _cardStrip.FocusMarks.Count;
            _legend.Children.Add(W.CardRow(p, ctx.T("Odak turları", "Focus rounds"), rounds.ToString(CultureInfo.InvariantCulture), W.Brush(p.Negative)));
            var m = CalendarTile.Meeting(ctx);
            if (m is not null)
                _legend.Children.Add(W.CardRow(p, ctx.T("Toplantı", "Meeting"), $"{m.StartUtc.ToLocalTime():HH:mm} · {m.Summary}", W.Brush(p.Accent)));
            var wx = ctx.Data.Weather;
            if (wx is not null)
                _legend.Children.Add(W.CardRow(p, ctx.T("Gün doğumu / batımı", "Sunrise / sunset"), $"{wx.Sunrise:HH:mm} / {wx.Sunset:HH:mm}", W.Brush(p.Warning)));
        }
    }

    // =============================================================================================
    // Screen time
    // =============================================================================================

    private sealed class ScreenTimeTile : DockWidgetTileBase
    {
        private TextBlock _total = null!;
        private TextBlock _caption = null!;
        private MiniBars _bars = null!;
        private ScreenTimeDay? _day;
        private DateTime _dataAt = DateTime.MinValue;

        private int _tab;
        private bool _appsWeek;
        private Border? _tabsHost;
        private Border? _bodyHost;
        private MiniBars? _cardBars;
        private TextBlock? _cardInfo;
        private StackPanel? _appList;
        private string _appsKey = "";
        private readonly List<(TextBlock Dur, BarGauge Bar)> _appRows = new();

        public ScreenTimeTile(DockWidgetDescriptor d, DockWidgetContext c) : base(d, c) { }

        private static MiniBars NewBars(DockWidgetPalette p, double labelSize) => new()
        {
            BarBrush = W.Brush(p.Accent, 0x8C),
            HighlightBrush = p.AccentBrush,
            TrackBrush = null,
            LabelBrush = p.TertiaryForeground,
            LabelFont = p.UiFont,
            LabelSize = labelSize,
            SnapsToDevicePixels = true
        };

        protected override FrameworkElement BuildContent(DockWidgetContext ctx)
        {
            var p = ctx.Palette;
            double h = ctx.TileHeight;
            _total = Tabular(W.Text(p, "", Fs(ctx, 0.3, 8.5), p.Foreground, FontWeights.SemiBold));
            _caption = W.Text(p, ctx.T("bugün", "today"), Fs(ctx, 0.2, 7), p.TertiaryForeground);
            _caption.Margin = new Thickness(4, 0, 0, 0);
            var head = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(_total, Dock.Left);
            head.Children.Add(_total);
            head.Children.Add(_caption);
            _bars = NewBars(p, 7);
            _bars.Height = Math.Max(6, Math.Round((h - 4) * 0.42));
            _bars.Margin = new Thickness(0, 1, 0, 0);
            var g = new Grid { VerticalAlignment = VerticalAlignment.Center };
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(_bars, 1);
            g.Children.Add(head);
            g.Children.Add(_bars);
            return g;
        }

        private static bool Enabled(DockWidgetContext ctx) => ctx.Config.ScreenTimeEnabled || ctx.IsAuditMode;

        private void EnsureData(DockWidgetContext ctx)
        {
            var now = ctx.Now;
            if (_day is not null && now >= _dataAt && (now - _dataAt).TotalSeconds < 10 && _day.Date == ScreenTimeLedger.DateKey(now)) return;
            _dataAt = now;
            _day = TodayData(ctx);
        }

        protected override void Refresh(DockWidgetContext ctx)
        {
            if (!Enabled(ctx))
            {
                W.SetTextAnimated(_total, ctx.T("Kapalı", "Off"));
                _caption.Text = ctx.T("ekran süresi", "screen time");
                _bars.Visibility = Visibility.Collapsed;
                return;
            }
            _bars.Visibility = Visibility.Visible;
            if (_caption.Text.Length == 0 || _caption.Text != ctx.T("bugün", "today")) _caption.Text = ctx.T("bugün", "today");
            EnsureData(ctx);
            var values = _day?.HourSeconds ?? new double[24];
            W.SetTextAnimated(_total, Dur(ctx, values.Where(double.IsFinite).Sum()));
            _bars.SetData(values, Math.Max(900, values.Where(double.IsFinite).DefaultIfEmpty(0).Max()), ctx.Now.Hour);
        }

        public override void OnAttached(DockWidgetContext ctx)
        {
            if (!ctx.IsAuditMode) _bars.Regrow();
        }

        // ---- card --------------------------------------------------------------------------

        protected override FrameworkElement? CreateCard(DockWidgetContext ctx)
        {
            _tabsHost = new Border { Margin = new Thickness(0, 0, 0, 6) };
            _bodyHost = new Border();
            BuildTabs(ctx);
            BuildBody(ctx);
            return W.Card(ctx.Palette, Descriptor.Glyph, Descriptor.Name(ctx.Lang), W.Column(_tabsHost, _bodyHost), 320);
        }

        private void BuildTabs(DockWidgetContext ctx)
        {
            if (_tabsHost is null) return;
            var p = ctx.Palette;
            var row = new WrapPanel();
            string[] names = [ctx.T("Bugün", "Today"), ctx.T("Hafta", "Week"), ctx.T("Uygulamalar", "Apps")];
            for (int i = 0; i < names.Length; i++)
            {
                int idx = i;
                row.Children.Add(W.Button(p, names[i], () =>
                {
                    if (_tab == idx) return;
                    _tab = idx;
                    BuildTabs(ctx);
                    BuildBody(ctx);
                    RefreshCard(ctx);
                }, primary: i == _tab));
            }
            _tabsHost.Child = row;
        }

        private void BuildBody(DockWidgetContext ctx)
        {
            if (_bodyHost is null) return;
            var p = ctx.Palette;
            _cardBars = null;
            _appList = null;
            _appRows.Clear();
            _appsKey = "";
            _cardInfo = Wrap(Tabular(W.Text(p, "", 12, p.SecondaryForeground, ui: false)));
            _cardInfo.Margin = new Thickness(0, 8, 0, 0);

            if (!Enabled(ctx))
            {
                var note = W.Column(
                    Wrap(W.Text(p, ctx.T("Ekran süresi takibi kapalı. Ayarlar'dan açabilirsin.",
                        "Screen-time tracking is turned off. You can turn it on in Settings."), 12.5, p.SecondaryForeground, ui: false)),
                    new WrapPanel
                    {
                        Margin = new Thickness(0, 10, 0, 0),
                        Children = { W.Button(p, ctx.T("Ayarlar", "Settings"), () => { if (!ctx.IsAuditMode) { ctx.CloseCard(); ctx.Orch.RequestOpenSettings(); } }, GlyphSettings) }
                    });
                _bodyHost.Child = note;
                return;
            }

            if (_tab == 2)
            {
                var toggle = new WrapPanel();
                toggle.Children.Add(W.Button(p, ctx.T("Gün", "Day"), () => { if (!_appsWeek) return; _appsWeek = false; BuildBody(ctx); RefreshCard(ctx); }, primary: !_appsWeek));
                toggle.Children.Add(W.Button(p, ctx.T("Hafta", "Week"), () => { if (_appsWeek) return; _appsWeek = true; BuildBody(ctx); RefreshCard(ctx); }, primary: _appsWeek));
                _appList = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
                _bodyHost.Child = W.Column(toggle, _appList);
                return;
            }

            _cardBars = NewBars(p, 9.5);
            _cardBars.Height = 104;
            _cardBars.TrackBrush = p.CardRowBackground;
            _bodyHost.Child = W.Column(_cardBars, _cardInfo);
            if (!ctx.IsAuditMode) _cardBars.Regrow();
        }

        private static string AppName(string app)
        {
            string n = app.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? app[..^4] : app;
            return n.Length == 0 ? app : char.ToUpperInvariant(n[0]) + n[1..];
        }

        protected override void RefreshCard(DockWidgetContext ctx)
        {
            if (_bodyHost is null || !Enabled(ctx)) return;
            var p = ctx.Palette;
            EnsureData(ctx);
            var now = ctx.Now;
            if (_tab == 0 && _cardBars is not null && _cardInfo is not null)
            {
                var values = _day?.HourSeconds ?? new double[24];
                var labels = new string[24];
                for (int i = 0; i < 24; i += 6) labels[i] = i.ToString("00", CultureInfo.InvariantCulture);
                _cardBars.SetData(values, Math.Max(900, values.DefaultIfEmpty(0).Max()), now.Hour, labels);
                double total = values.Where(double.IsFinite).Sum();
                int peak = Array.IndexOf(values, values.DefaultIfEmpty(0).Max());
                _cardInfo.Text = total <= 0
                    ? ctx.T("Bugün henüz kayıt yok.", "Nothing recorded yet today.")
                    : ctx.T($"Toplam {Dur(ctx, total)} · en yoğun saat {peak:00}:00", $"Total {Dur(ctx, total)} · busiest hour {peak:00}:00");
            }
            else if (_tab == 1 && _cardBars is not null && _cardInfo is not null)
            {
                double[] week;
                try { week = ctx.Data.ScreenTime.WeekTotals(now); }
                catch { week = new double[7]; }
                if (ctx.IsAuditMode && week.Take(6).Sum() < 1) week = SampleWeek(_day?.TotalSeconds ?? 0);
                var labels = new string[7];
                for (int i = 0; i < 7; i++) labels[i] = DayAbbrev(ctx, now.Date.AddDays(i - 6));
                _cardBars.SetData(week, null, 6, labels);
                double sum = week.Sum();
                int days = Math.Max(1, week.Count(x => x > 0));
                _cardInfo.Text = ctx.T($"7 gün toplam {Dur(ctx, sum)} · günlük ortalama {Dur(ctx, sum / days)}",
                    $"7-day total {Dur(ctx, sum)} · daily average {Dur(ctx, sum / days)}");
            }
            else if (_tab == 2 && _appList is not null)
            {
                IReadOnlyList<(string App, double Seconds)> apps;
                try { apps = ctx.Data.ScreenTime.TopApps(now, 6, _appsWeek); }
                catch { apps = []; }
                if (apps.Count == 0 && ctx.IsAuditMode && _day is not null)
                    apps = _day.AppSeconds.OrderByDescending(kv => kv.Value).Take(6)
                        .Select(kv => (kv.Key, kv.Value * (_appsWeek ? 5.6 : 1))).ToList();
                string key = string.Join("|", apps.Select(a => a.App));
                if (key != _appsKey)
                {
                    _appsKey = key;
                    _appList.Children.Clear();
                    _appRows.Clear();
                    if (apps.Count == 0)
                        _appList.Children.Add(W.Text(p, ctx.T("Henüz kayıt yok.", "Nothing recorded yet."), 12.5, p.SecondaryForeground, ui: false));
                    foreach (var a in apps)
                    {
                        var head = new DockPanel();
                        var dur = Tabular(W.Text(p, "", 12.5, p.Foreground, FontWeights.SemiBold));
                        DockPanel.SetDock(dur, Dock.Right);
                        head.Children.Add(dur);
                        var dot = new Ellipse { Width = 7, Height = 7, Fill = W.Brush(KindColor(p, AppKindClassifier.Classify(a.App))), Margin = new Thickness(0, 0, 7, 0) };
                        DockPanel.SetDock(dot, Dock.Left);
                        head.Children.Add(dot);
                        head.Children.Add(W.Text(p, AppName(a.App), 12.5, p.Foreground));
                        var bar = new BarGauge { Height = 5, Track = p.Track, Fill = W.Brush(KindColor(p, AppKindClassifier.Classify(a.App))), Margin = new Thickness(0, 4, 0, 9) };
                        _appList.Children.Add(W.Column(head, bar));
                        _appRows.Add((dur, bar));
                    }
                }
                double top = apps.Count > 0 ? Math.Max(1, apps[0].Seconds) : 1;
                for (int i = 0; i < Math.Min(apps.Count, _appRows.Count); i++)
                {
                    _appRows[i].Dur.Text = Dur(ctx, apps[i].Seconds);
                    _appRows[i].Bar.AnimateTo(apps[i].Seconds / top, 600 + i * 90);
                }
            }
        }
    }

    // =============================================================================================
    // Pomodoro
    // =============================================================================================

    private sealed class PomodoroTile : DockWidgetTileBase
    {
        private Grid _ringHost = null!;
        private RingGauge _ring = null!;
        private LiveBrush _fill = null!;
        private TextBlock _glyph = null!;
        private TextBlock _time = null!;
        private TextBlock _phase = null!;

        private Grid? _cardRingHost;
        private RingGauge? _cardRing;
        private LiveBrush? _cardFill;
        private TextBlock? _cardTime;
        private TextBlock? _cardPhase;
        private TextBlock? _cardRounds;
        private ButtonSlot? _cardToggle;

        public PomodoroTile(DockWidgetDescriptor d, DockWidgetContext c) : base(d, c) { }

        private static string FocusLabel(DockWidgetContext ctx) => ctx.T("Pomodoro · Odak", "Pomodoro · Focus");
        private static string BreakLabel(DockWidgetContext ctx) => ctx.T("Pomodoro · Mola", "Pomodoro · Break");
        private static bool IsBreakLabel(string l) => l is "Pomodoro · Mola" or "Pomodoro · Break";

        private static int Rounds(DockWidgetContext ctx)
        {
            if (Shared.PomodoroRoundsDate != ctx.Now.Date)
            {
                Shared.PomodoroRoundsDate = ctx.Now.Date;
                Shared.PomodoroRounds = 0;
                Shared.FocusLog.RemoveAll(x => x.Date != ctx.Now.Date);
            }
            return Shared.PomodoroRounds;
        }

        protected override FrameworkElement BuildContent(DockWidgetContext ctx)
        {
            var p = ctx.Palette;
            double h = ctx.TileHeight;
            double size = Math.Max(16, h - 6);
            _fill = new LiveBrush(p.Accent);
            _ring = W.Ring(p, size, Math.Max(2.2, h * 0.085), p.Accent);
            _ring.Fill = _fill.Brush;
            _glyph = W.Glyph(p, GlyphPlay, Math.Max(7, size * 0.36), p.Foreground);
            _ringHost = new Grid { Width = size, Height = size, Background = Brushes.Transparent, VerticalAlignment = VerticalAlignment.Center };
            _ringHost.Children.Add(_ring);
            _ringHost.Children.Add(_glyph);
            Clickable(_ringHost, () =>
            {
                W.Pulse(_glyph, 0.8);
                ToggleOrStart(ctx);
            });

            _time = Tabular(W.Text(p, "", Fs(ctx, 0.32, 9), p.Foreground, FontWeights.SemiBold));
            _phase = W.Text(p, "", Fs(ctx, 0.2, 7), p.SecondaryForeground);
            var col = W.Column(_time, _phase);
            col.Margin = new Thickness(Math.Max(4, h * 0.16), 0, 0, 0);
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(col, 1);
            g.Children.Add(_ringHost);
            g.Children.Add(col);
            return g;
        }

        private static TimerSnapshot? Current(DockWidgetContext ctx)
        {
            string? before = Shared.PomodoroId;
            var t = FindTimer(ctx, ref Shared.PomodoroId, x => x.IsPomodoro && !x.IsStopwatch && !x.IsCompleted);
            if (t is null && before is not null && Shared.PomodoroId is null)
            {
                // The timer was removed elsewhere (island, another tile): back to idle.
                Shared.PomodoroAwaiting = false;
            }
            else if (t is not null && before is null)
            {
                Shared.PomodoroBreak = IsBreakLabel(t.Label);
                Shared.PomodoroAwaiting = false;
            }
            return t;
        }

        private void Complete(DockWidgetContext ctx, TimerSnapshot t)
        {
            try { ctx.Orch.Timers.Remove(t.Id); } catch { /* already gone */ }
            Shared.PomodoroId = null;
            if (!Shared.PomodoroBreak)
            {
                Rounds(ctx);
                Shared.PomodoroRounds++;
                Shared.FocusLog.Add(ctx.Now);
            }
            Shared.PomodoroBreak = !Shared.PomodoroBreak;
            Shared.PomodoroAwaiting = true;
            Celebrate(_ringHost);
            if (_cardRingHost is not null) Celebrate(_cardRingHost);
        }

        private void ToggleOrStart(DockWidgetContext ctx)
        {
            if (ctx.IsAuditMode) return;
            var t = Current(ctx);
            if (t is { IsCompleted: false }) ctx.Orch.Timers.TogglePause(t.Id);
            else StartPhase(ctx, Shared.PomodoroBreak);
            Update(ctx);
        }

        private static void StartPhase(DockWidgetContext ctx, bool isBreak)
        {
            int minutes = Math.Clamp(isBreak ? ctx.Config.PomodoroBreakMinutes : ctx.Config.PomodoroFocusMinutes, 1, 180);
            Shared.PomodoroBreak = isBreak;
            Shared.PomodoroAwaiting = false;
            string id = ctx.Orch.Timers.StartCountdown(isBreak ? BreakLabel(ctx) : FocusLabel(ctx), TimeSpan.FromMinutes(minutes), isPomodoro: true);
            Shared.PomodoroId = string.IsNullOrEmpty(id) ? null : id;
        }

        private void Skip(DockWidgetContext ctx)
        {
            if (ctx.IsAuditMode) return;
            var t = Current(ctx);
            if (t is not null) ctx.Orch.Timers.Remove(t.Id);
            Shared.PomodoroId = null;
            StartPhase(ctx, !Shared.PomodoroBreak);
            Update(ctx);
        }

        private void Reset(DockWidgetContext ctx)
        {
            if (ctx.IsAuditMode) return;
            var t = Current(ctx);
            if (t is not null) ctx.Orch.Timers.Remove(t.Id);
            Shared.PomodoroId = null;
            Shared.PomodoroBreak = false;
            Shared.PomodoroAwaiting = false;
            Update(ctx);
        }

        private readonly record struct View(double Fraction, string Time, string Phase, bool Running, bool HasTimer, Color Color);

        private View Describe(DockWidgetContext ctx)
        {
            var p = ctx.Palette;
            var t = Current(ctx);
            if (t is { IsCompleted: true })
            {
                Complete(ctx, t);
                t = null;
            }
            int rounds = Rounds(ctx);
            bool brk = Shared.PomodoroBreak;
            Color color = brk ? p.Positive : p.Accent;
            string phaseName = brk ? ctx.T("Mola", "Break") : ctx.T("Odak", "Focus");
            if (t is not null)
            {
                double total = t.Total.TotalSeconds;
                double frac = total > 0 ? t.RemainingOrElapsed.TotalSeconds / total : 0;
                string phase = t.IsRunning ? $"{phaseName} · {rounds}" : ctx.T("Duraklatıldı", "Paused");
                return new View(frac, DockWidgetMath.FormatTimer(t.RemainingOrElapsed), phase, t.IsRunning, true, color);
            }
            if (ctx.IsAuditMode && !Shared.PomodoroAwaiting)
                return new View(0.62, "15:31", ctx.T("Odak · 3", "Focus · 3"), true, true, p.Accent);
            int minutes = Math.Clamp(brk ? ctx.Config.PomodoroBreakMinutes : ctx.Config.PomodoroFocusMinutes, 1, 180);
            string idle = Shared.PomodoroAwaiting
                ? (brk ? ctx.T("Mola hazır", "Break ready") : ctx.T("Odak hazır", "Focus ready"))
                : $"{phaseName} · {rounds}";
            return new View(1, DockWidgetMath.FormatTimer(TimeSpan.FromMinutes(minutes)), idle, false, false,
                Shared.PomodoroAwaiting ? color : WithAlpha(color, 0x90));
        }

        protected override void Refresh(DockWidgetContext ctx)
        {
            var v = Describe(ctx);
            _ring.AnimateTo(v.Fraction, 700);
            _fill.To(v.Color);
            W.SetTextAnimated(_time, v.Time, animate: false);
            W.SetTextAnimated(_phase, v.Phase);
            string g = v.Running ? GlyphPause : GlyphPlay;
            if (_glyph.Text != g) _glyph.Text = g;
        }

        protected override FrameworkElement? CreateCard(DockWidgetContext ctx)
        {
            var p = ctx.Palette;
            _cardFill = new LiveBrush(p.Accent);
            _cardRing = W.Ring(p, 96, 7, p.Accent);
            _cardRing.Fill = _cardFill.Brush;
            _cardTime = Tabular(W.Text(p, "", 22, p.Foreground, FontWeights.SemiBold));
            _cardTime.HorizontalAlignment = HorizontalAlignment.Center;
            _cardRingHost = new Grid { Width = 96, Height = 96, Margin = new Thickness(0, 0, 16, 0) };
            _cardRingHost.Children.Add(_cardRing);
            _cardRingHost.Children.Add(_cardTime);

            _cardPhase = W.Text(p, "", 15, p.Foreground, FontWeights.SemiBold);
            _cardRounds = W.Text(p, "", 12.5, p.SecondaryForeground, ui: false);
            var config = W.Text(p, ctx.T($"Odak {ctx.Config.PomodoroFocusMinutes} dk · Mola {ctx.Config.PomodoroBreakMinutes} dk",
                $"Focus {ctx.Config.PomodoroFocusMinutes} min · Break {ctx.Config.PomodoroBreakMinutes} min"), 12, p.TertiaryForeground, ui: false);
            config.Margin = new Thickness(0, 4, 0, 0);
            var info = W.Column(_cardPhase, _cardRounds, config);

            var top = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
            top.Children.Add(_cardRingHost);
            top.Children.Add(info);

            _cardToggle = new ButtonSlot();
            var buttons = new WrapPanel();
            buttons.Children.Add(_cardToggle.Host);
            buttons.Children.Add(W.Button(p, ctx.T("Atla", "Skip"), () => Skip(ctx), GlyphNext));
            buttons.Children.Add(W.Button(p, ctx.T("Sıfırla", "Reset"), () => Reset(ctx), GlyphReset));
            return W.Card(p, Descriptor.Glyph, Descriptor.Name(ctx.Lang), W.Column(top, buttons), 300);
        }

        protected override void RefreshCard(DockWidgetContext ctx)
        {
            if (_cardRing is null || _cardFill is null || _cardTime is null || _cardPhase is null || _cardRounds is null || _cardToggle is null) return;
            var p = ctx.Palette;
            var v = Describe(ctx);
            _cardRing.AnimateTo(v.Fraction, 700);
            _cardFill.To(v.Color);
            W.SetTextAnimated(_cardTime, v.Time, animate: false);
            W.SetTextAnimated(_cardPhase, v.Phase);
            int rounds = ctx.IsAuditMode && Rounds(ctx) == 0 ? 3 : Rounds(ctx);
            _cardRounds.Text = ctx.T($"Bugün tamamlanan: {rounds} tur", rounds == 1 ? "1 round done today" : $"{rounds} rounds done today");
            int state = v.Running ? 1 : v.HasTimer ? 2 : 0;
            _cardToggle.Set(state, () => state switch
            {
                1 => W.Button(p, ctx.T("Duraklat", "Pause"), () => ToggleOrStart(ctx), GlyphPause, primary: true),
                2 => W.Button(p, ctx.T("Devam", "Resume"), () => ToggleOrStart(ctx), GlyphPlay, primary: true),
                _ => W.Button(p, Shared.PomodoroBreak ? ctx.T("Molayı başlat", "Start break") : ctx.T("Odağı başlat", "Start focus"),
                    () => ToggleOrStart(ctx), GlyphPlay, primary: true)
            });
        }
    }

    // =============================================================================================
    // Timer
    // =============================================================================================

    private sealed class TimerTile : DockWidgetTileBase
    {
        private static readonly int[] Presets = [1, 3, 5, 10, 15, 25, 45, 60];

        private Grid _content = null!;
        private Grid _ringHost = null!;
        private RingGauge _ring = null!;
        private LiveBrush _fill = null!;
        private TextBlock _glyph = null!;
        private TextBlock _time = null!;
        private TextBlock _caption = null!;
        private bool _finished;
        private bool _attached;

        private RingGauge? _cardRing;
        private LiveBrush? _cardFill;
        private Grid? _cardRingHost;
        private TextBlock? _cardTime;
        private TextBlock? _cardCaption;
        private ButtonSlot? _cardToggle;
        private WrapPanel? _chips;

        public TimerTile(DockWidgetDescriptor d, DockWidgetContext c) : base(d, c) { }

        private static string Label(DockWidgetContext ctx) => ctx.T("Dock Zamanlayıcı", "Dock Timer");
        private static bool IsOurs(TimerSnapshot t) => !t.IsPomodoro && !t.IsStopwatch && t.Label is "Dock Zamanlayıcı" or "Dock Timer";
        private static TimerSnapshot? Current(DockWidgetContext ctx) => FindTimer(ctx, ref Shared.TimerId, IsOurs);
        private static int Minutes(DockWidgetContext ctx) => Math.Clamp(ctx.Config.DockTimerMinutes, 1, 600);

        protected override FrameworkElement BuildContent(DockWidgetContext ctx)
        {
            var p = ctx.Palette;
            double h = ctx.TileHeight;
            double size = Math.Max(16, h - 6);
            _fill = new LiveBrush(p.Accent);
            _ring = W.Ring(p, size, Math.Max(2.2, h * 0.085), p.Accent);
            _ring.Fill = _fill.Brush;
            _glyph = W.Glyph(p, GlyphPlay, Math.Max(7, size * 0.36), p.Foreground);
            _ringHost = new Grid { Width = size, Height = size, Background = Brushes.Transparent, VerticalAlignment = VerticalAlignment.Center };
            _ringHost.Children.Add(_ring);
            _ringHost.Children.Add(_glyph);
            Clickable(_ringHost, () =>
            {
                W.Pulse(_glyph, 0.8);
                Primary(ctx);
            });
            _time = Tabular(W.Text(p, "", Fs(ctx, 0.32, 9), p.Foreground, FontWeights.SemiBold));
            _caption = W.Text(p, "", Fs(ctx, 0.2, 7), p.SecondaryForeground);
            var col = W.Column(_time, _caption);
            col.Margin = new Thickness(Math.Max(4, h * 0.16), 0, 0, 0);
            _content = new Grid();
            _content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(col, 1);
            _content.Children.Add(_ringHost);
            _content.Children.Add(col);
            return _content;
        }

        private void Primary(DockWidgetContext ctx)
        {
            if (ctx.IsAuditMode) return;
            var t = Current(ctx);
            if (t is null) Start(ctx, Minutes(ctx));
            else if (t.IsCompleted) Cancel(ctx);
            else ctx.Orch.Timers.TogglePause(t.Id);
            Update(ctx);
        }

        private static void Start(DockWidgetContext ctx, double minutes)
        {
            var t = Current(ctx);
            if (t is not null) ctx.Orch.Timers.Remove(t.Id);
            var span = TimeSpan.FromMinutes(minutes);
            string id = ctx.Orch.Timers.StartCountdown(Label(ctx), span);
            Shared.TimerId = string.IsNullOrEmpty(id) ? null : id;
            Shared.TimerDisplayTotal = span;
        }

        private void AddMinute(DockWidgetContext ctx)
        {
            if (ctx.IsAuditMode) return;
            var t = Current(ctx);
            if (t is null || t.IsCompleted)
            {
                Start(ctx, 1);
            }
            else
            {
                var total = (t.Total > Shared.TimerDisplayTotal ? t.Total : Shared.TimerDisplayTotal) + TimeSpan.FromMinutes(1);
                bool running = t.IsRunning;
                ctx.Orch.Timers.Remove(t.Id);
                string id = ctx.Orch.Timers.StartCountdown(Label(ctx), t.RemainingOrElapsed + TimeSpan.FromMinutes(1));
                Shared.TimerId = string.IsNullOrEmpty(id) ? null : id;
                Shared.TimerDisplayTotal = total;
                if (!running && Shared.TimerId is not null) ctx.Orch.Timers.TogglePause(Shared.TimerId);
            }
            Update(ctx);
        }

        private void Cancel(DockWidgetContext ctx)
        {
            if (ctx.IsAuditMode) return;
            var t = Current(ctx);
            if (t is not null) ctx.Orch.Timers.Remove(t.Id);
            Shared.TimerId = null;
            Update(ctx);
        }

        private void TogglePause(DockWidgetContext ctx)
        {
            if (ctx.IsAuditMode) return;
            var t = Current(ctx);
            if (t is null || t.IsCompleted) Start(ctx, Minutes(ctx));
            else ctx.Orch.Timers.TogglePause(t.Id);
            Update(ctx);
        }

        private readonly record struct View(double Fraction, string Time, string Caption, int State, Color Color);

        private static View Describe(DockWidgetContext ctx)
        {
            var p = ctx.Palette;
            var t = Current(ctx);
            if (t is null)
            {
                if (ctx.IsAuditMode) return new View(0.58, "02:54", ctx.T("kalan", "left"), 1, p.Accent);
                return new View(1, DockWidgetMath.FormatTimer(TimeSpan.FromMinutes(Minutes(ctx))), ctx.T("Başlat", "Start"), 0, WithAlpha(p.Accent, 0x90));
            }
            if (t.IsCompleted) return new View(0, "00:00", ctx.T("Süre doldu", "Time's up"), 3, p.Negative);
            var total = t.Total > Shared.TimerDisplayTotal ? t.Total : Shared.TimerDisplayTotal;
            double frac = total.TotalSeconds > 0 ? t.RemainingOrElapsed.TotalSeconds / total.TotalSeconds : 0;
            var color = t.RemainingOrElapsed.TotalSeconds <= 10 ? p.Warning : p.Accent;
            return new View(frac, DockWidgetMath.FormatTimer(t.RemainingOrElapsed),
                t.IsRunning ? ctx.T("kalan", "left") : ctx.T("Duraklatıldı", "Paused"), t.IsRunning ? 1 : 2, color);
        }

        protected override void Refresh(DockWidgetContext ctx)
        {
            var v = Describe(ctx);
            _ring.AnimateTo(v.Fraction, 700);
            _fill.To(v.Color);
            W.SetTextAnimated(_time, v.Time, animate: false);
            W.SetTextAnimated(_caption, v.Caption);
            string g = v.State == 1 ? GlyphPause : v.State == 3 ? GlyphClose : GlyphPlay;
            if (_glyph.Text != g) _glyph.Text = g;

            bool finished = v.State == 3;
            if (finished && !_finished)
            {
                _finished = true;
                Shake(_content);
                if (_attached) PulseForever(_ringHost, 1.12, 0.45);
                if (_cardRingHost is not null) Shake(_cardRingHost);
            }
            else if (!finished && _finished)
            {
                _finished = false;
                StopScale(_ringHost);
            }
        }

        public override void OnAttached(DockWidgetContext ctx)
        {
            _attached = true;
            if (_finished) PulseForever(_ringHost, 1.12, 0.45);
        }

        public override void OnDetached()
        {
            _attached = false;
            StopScale(_ringHost);
        }

        protected override FrameworkElement? CreateCard(DockWidgetContext ctx)
        {
            var p = ctx.Palette;
            _cardFill = new LiveBrush(p.Accent);
            _cardRing = W.Ring(p, 88, 7, p.Accent);
            _cardRing.Fill = _cardFill.Brush;
            _cardTime = Tabular(W.Text(p, "", 20, p.Foreground, FontWeights.SemiBold));
            _cardTime.HorizontalAlignment = HorizontalAlignment.Center;
            _cardRingHost = new Grid { Width = 88, Height = 88, Margin = new Thickness(0, 0, 16, 0) };
            _cardRingHost.Children.Add(_cardRing);
            _cardRingHost.Children.Add(_cardTime);

            _cardCaption = W.Text(p, "", 14, p.Foreground, FontWeights.SemiBold);
            _cardCaption.Margin = new Thickness(0, 0, 0, 8);
            _cardToggle = new ButtonSlot();
            var actions = new WrapPanel();
            actions.Children.Add(_cardToggle.Host);
            actions.Children.Add(W.Button(p, ctx.T("+1 dk", "+1 min"), () => AddMinute(ctx), GlyphAdd));
            actions.Children.Add(W.Button(p, ctx.T("İptal", "Cancel"), () => Cancel(ctx), GlyphClose));
            var right = W.Column(_cardCaption, actions);
            right.Width = 300 - 88 - 16;

            var top = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
            top.Children.Add(_cardRingHost);
            top.Children.Add(right);

            var presetsLabel = W.Text(p, ctx.T("Hazır süreler", "Presets"), 12, p.SecondaryForeground, ui: false);
            presetsLabel.Margin = new Thickness(0, 0, 0, 6);
            _chips = new WrapPanel();
            BuildChips(ctx);
            return W.Card(p, Descriptor.Glyph, Descriptor.Name(ctx.Lang), W.Column(top, presetsLabel, _chips), 300);
        }

        private void BuildChips(DockWidgetContext ctx)
        {
            if (_chips is null) return;
            var p = ctx.Palette;
            _chips.Children.Clear();
            int current = Minutes(ctx);
            foreach (int m in Presets)
            {
                int minutes = m;
                _chips.Children.Add(W.Button(p, ctx.T($"{m} dk", $"{m} min"), () =>
                {
                    if (ctx.IsAuditMode) return;
                    ctx.EditConfig(c => c.DockTimerMinutes = minutes);
                    Start(ctx, minutes);
                    BuildChips(ctx);
                    Update(ctx);
                }, primary: m == current));
            }
        }

        protected override void RefreshCard(DockWidgetContext ctx)
        {
            if (_cardRing is null || _cardFill is null || _cardTime is null || _cardCaption is null || _cardToggle is null) return;
            var p = ctx.Palette;
            var v = Describe(ctx);
            _cardRing.AnimateTo(v.Fraction, 700);
            _cardFill.To(v.Color);
            W.SetTextAnimated(_cardTime, v.Time, animate: false);
            W.SetTextAnimated(_cardCaption, v.State == 0 ? ctx.T($"{Minutes(ctx)} dakikalık sayım", $"{Minutes(ctx)}-minute countdown") : v.Caption);
            _cardToggle.Set(v.State, () => v.State switch
            {
                1 => W.Button(p, ctx.T("Duraklat", "Pause"), () => TogglePause(ctx), GlyphPause, primary: true),
                2 => W.Button(p, ctx.T("Devam", "Resume"), () => TogglePause(ctx), GlyphPlay, primary: true),
                3 => W.Button(p, ctx.T("Yeniden", "Restart"), () => TogglePause(ctx), GlyphReset, primary: true),
                _ => W.Button(p, ctx.T("Başlat", "Start"), () => TogglePause(ctx), GlyphPlay, primary: true)
            });
        }
    }

    // =============================================================================================
    // Stopwatch
    // =============================================================================================

    private sealed class StopwatchTile : DockWidgetTileBase
    {
        private Ellipse _dot = null!;
        private TextBlock _time = null!;
        private TextBlock _laps = null!;
        private int _dotState = -1;
        private bool _attached;

        private TextBlock? _cardTime;
        private ButtonSlot? _cardToggle;
        private StackPanel? _lapList;
        private int _lapShown;
        private string _lapOwner = "\0";

        public StopwatchTile(DockWidgetDescriptor d, DockWidgetContext c) : base(d, c) { }

        private static string Label(DockWidgetContext ctx) => ctx.T("Dock Kronometre", "Dock Stopwatch");
        private static bool IsOurs(TimerSnapshot t) => t.IsStopwatch && t.Label is "Dock Kronometre" or "Dock Stopwatch";
        private static TimerSnapshot? Current(DockWidgetContext ctx) => FindTimer(ctx, ref Shared.StopwatchId, IsOurs);

        private static readonly TimeSpan[] SampleLaps = [TimeSpan.FromSeconds(61.4), TimeSpan.FromSeconds(128.9), TimeSpan.FromSeconds(187.2)];

        protected override FrameworkElement BuildContent(DockWidgetContext ctx)
        {
            var p = ctx.Palette;
            double h = ctx.TileHeight;
            double ds = Math.Max(5, Math.Round(h * 0.2));
            _dot = new Ellipse { Width = ds, Height = ds, Fill = p.Track, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, Math.Max(4, h * 0.14), 0) };
            _time = Tabular(W.Text(p, "", Fs(ctx, 0.32, 9), p.Foreground, FontWeights.SemiBold));
            _laps = W.Text(p, "", Fs(ctx, 0.2, 7), p.SecondaryForeground);
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var col = W.Column(_time, _laps);
            Grid.SetColumn(col, 1);
            g.Children.Add(_dot);
            g.Children.Add(col);
            return g;
        }

        private IReadOnlyList<TimeSpan> Laps(DockWidgetContext ctx, TimerSnapshot? t)
        {
            if (t is null) return ctx.IsAuditMode ? SampleLaps : [];
            try { return ctx.Data.GetLaps(t.Id); }
            catch { return []; }
        }

        protected override void Refresh(DockWidgetContext ctx)
        {
            var p = ctx.Palette;
            var t = Current(ctx);
            bool audit = t is null && ctx.IsAuditMode;
            var elapsed = t?.RemainingOrElapsed ?? (audit ? TimeSpan.FromSeconds(207) : TimeSpan.Zero);
            int laps = Laps(ctx, t).Count;
            W.SetTextAnimated(_time, DockWidgetMath.FormatTimer(elapsed), animate: false);
            string lapText = laps > 0
                ? ctx.T($"{laps} tur", laps == 1 ? "1 lap" : $"{laps} laps")
                : t is null && !audit ? ctx.T("Başlat", "Start") : ctx.T("tur yok", "no laps");
            W.SetTextAnimated(_laps, lapText);
            int state = t is null ? (audit ? 1 : 0) : t.IsRunning ? 1 : 2;
            SetDot(p, state);
        }

        private void SetDot(DockWidgetPalette p, int state)
        {
            if (state == _dotState) return;
            _dotState = state;
            _dot.Fill = state switch { 1 => W.Brush(p.Positive), 2 => W.Brush(p.Warning), _ => p.Track };
            if (state == 1 && _attached) Breathe(_dot, 0.25, 0.7);
            else StopBreathe(_dot);
        }

        public override void OnAttached(DockWidgetContext ctx)
        {
            _attached = true;
            if (_dotState == 1) Breathe(_dot, 0.25, 0.7);
        }

        public override void OnDetached()
        {
            _attached = false;
            StopBreathe(_dot);
        }

        private void Toggle(DockWidgetContext ctx)
        {
            if (ctx.IsAuditMode) return;
            var t = Current(ctx);
            if (t is null)
            {
                string id = ctx.Orch.Timers.StartStopwatch(Label(ctx));
                Shared.StopwatchId = string.IsNullOrEmpty(id) ? null : id;
            }
            else
            {
                ctx.Orch.Timers.TogglePause(t.Id);
            }
            Update(ctx);
        }

        private void Lap(DockWidgetContext ctx)
        {
            if (ctx.IsAuditMode) return;
            var t = Current(ctx);
            if (t is null || t.RemainingOrElapsed <= TimeSpan.Zero) return;
            ctx.Data.AddLap(t.Id, t.RemainingOrElapsed);
            Update(ctx);
        }

        private void Reset(DockWidgetContext ctx)
        {
            if (ctx.IsAuditMode) return;
            var t = Current(ctx);
            if (t is not null)
            {
                ctx.Data.ClearLaps(t.Id);
                ctx.Orch.Timers.Remove(t.Id);
            }
            Shared.StopwatchId = null;
            Update(ctx);
        }

        protected override FrameworkElement? CreateCard(DockWidgetContext ctx)
        {
            var p = ctx.Palette;
            _cardTime = Tabular(W.Text(p, "", 30, p.Foreground, FontWeights.SemiBold));
            _cardTime.Margin = new Thickness(0, 0, 0, 10);
            _cardToggle = new ButtonSlot();
            var buttons = new WrapPanel();
            buttons.Children.Add(_cardToggle.Host);
            buttons.Children.Add(W.Button(p, ctx.T("Tur", "Lap"), () => Lap(ctx), GlyphFlag));
            buttons.Children.Add(W.Button(p, ctx.T("Sıfırla", "Reset"), () => Reset(ctx), GlyphReset));
            _lapList = new StackPanel();
            _lapShown = 0;
            _lapOwner = "\0";
            var scroll = new ScrollViewer
            {
                Content = _lapList,
                MaxHeight = 190,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Margin = new Thickness(0, 6, 0, 0)
            };
            return W.Card(p, Descriptor.Glyph, Descriptor.Name(ctx.Lang), W.Column(_cardTime, buttons, scroll), 300);
        }

        private static FrameworkElement LapRow(DockWidgetContext ctx, int number, TimeSpan split, TimeSpan total)
        {
            var p = ctx.Palette;
            var g = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var n = W.Text(p, ctx.T($"Tur {number}", $"Lap {number}"), 12.5, p.SecondaryForeground, ui: false);
            var s = Tabular(W.Text(p, "+" + FormatLap(split), 12.5, p.Foreground));
            s.HorizontalAlignment = HorizontalAlignment.Right;
            var tt = Tabular(W.Text(p, FormatLap(total), 12.5, p.Foreground, FontWeights.SemiBold));
            tt.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(s, 1);
            Grid.SetColumn(tt, 2);
            g.Children.Add(n);
            g.Children.Add(s);
            g.Children.Add(tt);
            return new Border { Child = g, Background = p.CardRowBackground, CornerRadius = new CornerRadius(7), Padding = new Thickness(9, 4, 9, 4), Margin = new Thickness(0, 0, 0, 4) };
        }

        protected override void RefreshCard(DockWidgetContext ctx)
        {
            if (_cardTime is null || _cardToggle is null || _lapList is null) return;
            var p = ctx.Palette;
            var t = Current(ctx);
            bool audit = t is null && ctx.IsAuditMode;
            var elapsed = t?.RemainingOrElapsed ?? (audit ? TimeSpan.FromSeconds(207) : TimeSpan.Zero);
            W.SetTextAnimated(_cardTime, FormatLap(elapsed), animate: false);
            int state = t is null ? 0 : t.IsRunning ? 1 : 2;
            _cardToggle.Set(state, () => state switch
            {
                1 => W.Button(p, ctx.T("Duraklat", "Pause"), () => Toggle(ctx), GlyphPause, primary: true),
                2 => W.Button(p, ctx.T("Devam", "Resume"), () => Toggle(ctx), GlyphPlay, primary: true),
                _ => W.Button(p, ctx.T("Başlat", "Start"), () => Toggle(ctx), GlyphPlay, primary: true)
            });

            var laps = Laps(ctx, t);
            string owner = t?.Id ?? "";
            bool initial = owner != _lapOwner;
            if (initial || laps.Count < _lapShown)
            {
                _lapOwner = owner;
                _lapList.Children.Clear();
                _lapShown = 0;
            }
            for (int i = _lapShown; i < laps.Count; i++)
            {
                var split = laps[i] - (i > 0 ? laps[i - 1] : TimeSpan.Zero);
                var row = LapRow(ctx, i + 1, split, laps[i]);
                _lapList.Children.Insert(0, row);
                if (!initial) SlideIn(row);
            }
            _lapShown = laps.Count;
        }
    }

    // =============================================================================================
    // Todos
    // =============================================================================================

    /// <summary>Custom-drawn check circle whose tick draws itself in.</summary>
    private sealed class CheckCircle : Grid
    {
        private readonly Ellipse _ring;
        private readonly Ellipse _fill;
        private readonly Path _tick;
        private readonly double _dash;

        public CheckCircle(DockWidgetPalette p, double size)
        {
            Width = size;
            Height = size;
            Background = Brushes.Transparent;
            Cursor = Cursors.Hand;
            VerticalAlignment = VerticalAlignment.Center;
            double th = Math.Max(1.2, size * 0.11);
            _ring = new Ellipse { Stroke = p.SecondaryForeground, StrokeThickness = Math.Max(1, th * 0.85) };
            _fill = new Ellipse { Fill = p.AccentBrush, Opacity = 0 };
            var a = new Point(size * 0.28, size * 0.52);
            var b = new Point(size * 0.44, size * 0.68);
            var c = new Point(size * 0.73, size * 0.35);
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                g.BeginFigure(a, false, false);
                g.LineTo(b, true, true);
                g.LineTo(c, true, true);
            }
            geo.Freeze();
            double len = (b - a).Length + (c - b).Length;
            _dash = len / th + 1;
            _tick = new Path
            {
                Data = geo,
                Stroke = OnAccent(p),
                StrokeThickness = th,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeDashArray = new DoubleCollection { _dash, _dash + 2 },
                StrokeDashOffset = _dash,
                Opacity = 0
            };
            Children.Add(_ring);
            Children.Add(_fill);
            Children.Add(_tick);
        }

        public void SetChecked(bool on)
        {
            _fill.BeginAnimation(OpacityProperty, null);
            _ring.BeginAnimation(OpacityProperty, null);
            _tick.BeginAnimation(Shape.StrokeDashOffsetProperty, null);
            _fill.Opacity = on ? 1 : 0;
            _ring.Opacity = on ? 0 : 1;
            _tick.Opacity = on ? 1 : 0;
            _tick.StrokeDashOffset = on ? 0 : _dash;
        }

        public void AnimateCheck()
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            _tick.Opacity = 1;
            _fill.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(170)));
            _ring.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(170)));
            _tick.BeginAnimation(Shape.StrokeDashOffsetProperty, new DoubleAnimation(_dash, 0, TimeSpan.FromMilliseconds(300))
            {
                BeginTime = TimeSpan.FromMilliseconds(90),
                EasingFunction = ease
            });
            W.Pulse(this, 1.15);
        }
    }

    private sealed class TodosTile : DockWidgetTileBase
    {
        private RingGauge _ring = null!;
        private LiveBrush _fill = null!;
        private TextBlock _count = null!;
        private CheckCircle _check = null!;
        private TextBlock _text = null!;
        private string? _nextId;
        private bool _ticking;
        private TextBlock? _cardCount;

        public TodosTile(DockWidgetDescriptor d, DockWidgetContext c) : base(d, c) { }

        private static List<DockTodoItem> Items(DockWidgetContext ctx)
        {
            var list = ctx.Config.DockTodos ?? new List<DockTodoItem>();
            if (list.Count == 0 && ctx.IsAuditMode)
            {
                return
                [
                    new DockTodoItem { Id = "audit-1", Text = ctx.T("Haftalık raporu gönder", "Send the weekly report"), Done = true },
                    new DockTodoItem { Id = "audit-2", Text = ctx.T("Sunum slaytlarını bitir", "Finish the slide deck"), Done = false },
                    new DockTodoItem { Id = "audit-3", Text = ctx.T("Faturayı öde", "Pay the invoice"), Done = false },
                    new DockTodoItem { Id = "audit-4", Text = ctx.T("Spor salonu 19:00", "Gym at 7 pm"), Done = false }
                ];
            }
            return list;
        }

        private static DockTodoItem Clone(DockTodoItem x) => new() { Id = x.Id, Text = x.Text, Done = x.Done };

        private static void SetDone(DockWidgetContext ctx, string id, bool done)
        {
            if (ctx.IsAuditMode) return;
            ctx.EditConfig(c => c.DockTodos = (c.DockTodos ?? new List<DockTodoItem>())
                .Select(x => x.Id == id ? new DockTodoItem { Id = x.Id, Text = x.Text, Done = done } : Clone(x)).ToList());
        }

        protected override FrameworkElement BuildContent(DockWidgetContext ctx)
        {
            var p = ctx.Palette;
            double h = ctx.TileHeight;
            double size = Math.Max(16, h - 6);
            _fill = new LiveBrush(p.Accent);
            _ring = W.Ring(p, size, Math.Max(2, h * 0.08), p.Accent);
            _ring.Fill = _fill.Brush;
            _count = Tabular(W.Text(p, "", Math.Max(6.5, h * 0.2), p.Foreground, FontWeights.SemiBold));
            _count.HorizontalAlignment = HorizontalAlignment.Center;
            var ringHost = new Grid { Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };
            ringHost.Children.Add(_ring);
            ringHost.Children.Add(_count);

            _check = new CheckCircle(p, Math.Max(11, Math.Round(h * 0.42)));
            _check.Margin = new Thickness(Math.Max(4, h * 0.16), 0, Math.Max(3, h * 0.12), 0);
            Clickable(_check, () => TickNext(ctx));

            _text = W.Text(p, "", Fs(ctx, 0.27, 8), p.Foreground, FontWeights.SemiBold);
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(_check, 1);
            Grid.SetColumn(_text, 2);
            g.Children.Add(ringHost);
            g.Children.Add(_check);
            g.Children.Add(_text);
            return g;
        }

        private void TickNext(DockWidgetContext ctx)
        {
            if (_ticking || _nextId is null) return;
            string id = _nextId;
            _ticking = true;
            _check.AnimateCheck();
            Strike(_text, ctx.Palette.Foreground, animate: true);
            After(650, () =>
            {
                SetDone(ctx, id, true);
                _ticking = false;
                if (ctx.IsAuditMode)
                {
                    _check.SetChecked(false);
                    ClearStrike(_text);
                }
                Update(ctx);
            });
        }

        protected override void Refresh(DockWidgetContext ctx)
        {
            var p = ctx.Palette;
            var items = Items(ctx);
            int total = items.Count, done = items.Count(x => x.Done);
            _ring.AnimateTo(total == 0 ? 0 : done / (double)total, 750);
            _fill.To(total > 0 && done == total ? p.Positive : p.Accent);
            W.SetTextAnimated(_count, total == 0 ? "0" : $"{done}/{total}");
            if (_cardCount is not null) _cardCount.Text = $"{done}/{total}";
            if (_ticking) return;
            var next = items.FirstOrDefault(x => !x.Done);
            string text;
            if (next is null)
            {
                _check.Visibility = Visibility.Collapsed;
                _nextId = null;
                ClearStrike(_text);
                text = total == 0 ? ctx.T("Görev ekle", "Add a task") : ctx.T("Hepsi tamam", "All done");
            }
            else
            {
                _check.Visibility = Visibility.Visible;
                if (_nextId != next.Id)
                {
                    _nextId = next.Id;
                    _check.SetChecked(false);
                    ClearStrike(_text);
                }
                text = next.Text;
            }
            W.SetTextAnimated(_text, text);
        }

        protected override FrameworkElement? CreateCard(DockWidgetContext ctx)
        {
            var p = ctx.Palette;
            var items = Items(ctx);
            var list = new StackPanel();
            if (items.Count == 0)
                list.Children.Add(W.Text(p, ctx.T("Liste boş. Aşağıdan görev ekle.", "The list is empty. Add a task below."), 12.5, p.SecondaryForeground, ui: false));
            foreach (var item in items) list.Children.Add(Row(ctx, item));
            var scroll = new ScrollViewer
            {
                Content = list,
                MaxHeight = 250,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };

            var input = W.Input(p, ctx.T("Yeni görev yaz ve Enter'a bas", "Type a task and press Enter"), 300);
            input.Margin = new Thickness(0, 10, 0, 8);
            input.MaxLength = 200;
            input.KeyDown += (_, e) =>
            {
                if (e.Key != Key.Enter) return;
                e.Handled = true;
                string text = input.Text.Trim();
                if (text.Length == 0 || ctx.IsAuditMode) return;
                ctx.EditConfig(c => c.DockTodos = (c.DockTodos ?? new List<DockTodoItem>()).Select(Clone)
                    .Append(new DockTodoItem { Text = text }).ToList());
                Update(ctx);
                ReopenCard(ctx);
            };
            input.Loaded += (_, _) => input.Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                input.Focus();
                Keyboard.Focus(input);
            });
            input.Dispatcher.BeginInvoke(DispatcherPriority.Input, () => input.Focus());

            var footer = new WrapPanel();
            if (items.Any(x => x.Done))
            {
                footer.Children.Add(W.Button(p, ctx.T("Tamamlananları temizle", "Clear completed"), () =>
                {
                    if (ctx.IsAuditMode) return;
                    ctx.EditConfig(c => c.DockTodos = (c.DockTodos ?? new List<DockTodoItem>()).Where(x => !x.Done).Select(Clone).ToList());
                    Update(ctx);
                    ReopenCard(ctx);
                }, GlyphDelete));
            }

            _cardCount = Tabular(W.Text(p, $"{items.Count(x => x.Done)}/{items.Count}", 13, p.SecondaryForeground, FontWeights.SemiBold));
            return W.Card(p, Descriptor.Glyph, Descriptor.Name(ctx.Lang), W.Column(scroll, input, footer), 300, _cardCount);
        }

        private FrameworkElement Row(DockWidgetContext ctx, DockTodoItem item)
        {
            var p = ctx.Palette;
            string id = item.Id;
            var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3), LastChildFill = true };
            var check = new CheckCircle(p, 17) { Margin = new Thickness(0, 0, 10, 0) };
            check.SetChecked(item.Done);
            DockPanel.SetDock(check, Dock.Left);
            row.Children.Add(check);
            var del = W.IconButton(p, GlyphClose, 22, () =>
            {
                if (ctx.IsAuditMode) return;
                ctx.EditConfig(c => c.DockTodos = (c.DockTodos ?? new List<DockTodoItem>()).Where(x => x.Id != id).Select(Clone).ToList());
                Update(ctx);
                ReopenCard(ctx);
            }, foreground: p.TertiaryForeground);
            DockPanel.SetDock(del, Dock.Right);
            row.Children.Add(del);
            var text = W.Text(p, item.Text, 13, p.Foreground, ui: false);
            if (item.Done) Strike(text, p.Foreground, animate: false);
            row.Children.Add(text);

            bool busy = false;
            Clickable(check, () =>
            {
                if (busy || ctx.IsAuditMode) return;
                bool done = Items(ctx).FirstOrDefault(x => x.Id == id)?.Done ?? false;
                if (!done)
                {
                    busy = true;
                    check.AnimateCheck();
                    Strike(text, p.Foreground, animate: true);
                    After(420, () =>
                    {
                        busy = false;
                        SetDone(ctx, id, true);
                        Update(ctx);
                    });
                }
                else
                {
                    check.SetChecked(false);
                    ClearStrike(text);
                    SetDone(ctx, id, false);
                    Update(ctx);
                }
            });
            return row;
        }
    }

    // =============================================================================================
    // Notes
    // =============================================================================================

    private sealed record NoteInfo(string Path, string Title, string Preview, DateTime LastWrite);

    private static class NotesCache
    {
        public static NoteInfo? Info;
        public static DateTime At = DateTime.MinValue;
        public static bool Busy;
    }

    // No text-notes feature exists in the app (VoiceNotes only stores .wav recordings), so the
    // widget uses its own folder under Documents.
    private static string NotesFolder =>
        IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "DinamikAda Notes");

    private static NoteInfo? ScanNotes()
    {
        string dir = NotesFolder;
        if (!IO.Directory.Exists(dir)) return null;
        IO.FileInfo? newest = null;
        foreach (var f in new IO.DirectoryInfo(dir).EnumerateFiles())
        {
            if (!f.Extension.Equals(".txt", StringComparison.OrdinalIgnoreCase) &&
                !f.Extension.Equals(".md", StringComparison.OrdinalIgnoreCase)) continue;
            if (newest is null || f.LastWriteTime > newest.LastWriteTime) newest = f;
        }
        if (newest is null) return null;
        var lines = new List<string>();
        string title = "";
        using (var stream = new IO.FileStream(newest.FullName, IO.FileMode.Open, IO.FileAccess.Read, IO.FileShare.ReadWrite | IO.FileShare.Delete))
        using (var reader = new IO.StreamReader(stream))
        {
            string? line;
            int read = 0;
            while (read < 200 && lines.Count < 8 && (line = reader.ReadLine()) is not null)
            {
                read++;
                string t = line.Trim();
                if (t.Length == 0) continue;
                if (title.Length == 0) title = t.TrimStart('#', ' ', '-', '*', '>').Trim();
                lines.Add(t.Length > 140 ? t[..140] + "…" : t);
            }
        }
        if (title.Length == 0) title = IO.Path.GetFileNameWithoutExtension(newest.Name);
        return new NoteInfo(newest.FullName, title, string.Join("\n", lines), newest.LastWriteTime);
    }

    private sealed class NotesTile : DockWidgetTileBase
    {
        private TextBlock _a = null!;
        private TextBlock _b = null!;
        private bool _aActive = true;
        private TextBlock _sub = null!;
        private string _shownKey = "\0";
        private DockWidgetContext? _ctx;

        private TextBlock? _cardPreview;
        private TextBlock? _cardMeta;
        private string _cardKey = "\0";

        public NotesTile(DockWidgetDescriptor d, DockWidgetContext c) : base(d, c) { }

        private static NoteInfo? Note(DockWidgetContext ctx) => ctx.IsAuditMode
            ? new NoteInfo("", ctx.T("Lansman toplantısı notları", "Launch meeting notes"),
                ctx.T("# Lansman toplantısı notları\n- Tarih: Cuma 14:00\n- Basın bülteni taslağı hazır\n- Demo videosu çarşamba", "# Launch meeting notes\n- Date: Friday 2 pm\n- Press release draft ready\n- Demo video on Wednesday"),
                ctx.Now.AddMinutes(-12))
            : NotesCache.Info;

        protected override FrameworkElement BuildContent(DockWidgetContext ctx)
        {
            var p = ctx.Palette;
            double h = ctx.TileHeight;
            var glyph = W.Glyph(p, Descriptor.Glyph, Math.Max(10, h * 0.44), W.Brush(p.Warning));
            glyph.Margin = new Thickness(0, 0, Math.Max(4, h * 0.16), 0);
            double fs = Fs(ctx, 0.27, 8);
            _a = W.Text(p, "", fs, p.Foreground, FontWeights.SemiBold);
            _b = W.Text(p, "", fs, p.Foreground, FontWeights.SemiBold);
            _b.Opacity = 0;
            var titleGrid = new Grid();
            titleGrid.Children.Add(_a);
            titleGrid.Children.Add(_b);
            _sub = W.Text(p, "", Fs(ctx, 0.2, 7), p.SecondaryForeground);
            var col = W.Column(titleGrid, _sub);
            var textHost = new Border { Child = col, Background = Brushes.Transparent, VerticalAlignment = VerticalAlignment.Stretch };
            // A click on the text opens the note; the icon (or a click with no note yet) opens the card.
            textHost.MouseLeftButtonDown += (_, e) => { if (Note(ctx) is { Path.Length: > 0 }) e.Handled = true; };
            textHost.MouseLeftButtonUp += (_, e) =>
            {
                if (Note(ctx) is not { Path.Length: > 0 } note) return;
                e.Handled = true;
                W.Pulse(textHost, 0.96);
                DockWidgetActions.Run(ctx, note.Path);
            };
            textHost.Cursor = Cursors.Hand;
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(textHost, 1);
            g.Children.Add(glyph);
            g.Children.Add(textHost);
            return g;
        }

        private void EnsureScan(DockWidgetContext ctx, bool force = false)
        {
            if (ctx.IsAuditMode || NotesCache.Busy) return;
            var now = DateTime.Now;
            if (!force && now >= NotesCache.At && (now - NotesCache.At).TotalSeconds < 10) return;
            NotesCache.Busy = true;
            var dispatcher = Root.Dispatcher;
            _ = Task.Run(() =>
            {
                NoteInfo? result;
                try { result = ScanNotes(); }
                catch (Exception ex)
                {
                    CrashLogger.Log("DockWidget", ex, "Notes folder scan failed.");
                    result = NotesCache.Info;
                }
                dispatcher.BeginInvoke(() =>
                {
                    NotesCache.Info = result;
                    NotesCache.At = DateTime.Now;
                    NotesCache.Busy = false;
                    if (_ctx is { } c) Apply(c);
                });
            });
        }

        protected override void Refresh(DockWidgetContext ctx)
        {
            _ctx = ctx;
            EnsureScan(ctx);
            Apply(ctx);
        }

        private static string Ago(DockWidgetContext ctx, DateTime t)
        {
            var span = ctx.Now - t;
            if (span.TotalMinutes < 1) return ctx.T("az önce", "just now");
            if (span.TotalDays >= 2) return t.ToString(ctx.En ? "MMM d" : "d MMM", Culture(ctx));
            return ctx.T($"{Dur(ctx, span)} önce", $"{Dur(ctx, span)} ago");
        }

        private void Apply(DockWidgetContext ctx)
        {
            var note = Note(ctx);
            string title = note?.Title ?? ctx.T("Henüz not yok", "No notes yet");
            string key = note is null ? "" : note.Path + "|" + note.Title;
            if (key != _shownKey)
            {
                bool first = _shownKey == "\0";
                _shownKey = key;
                CrossFade(title, !first && !ctx.IsAuditMode);
            }
            _sub.Text = note is null ? ctx.T("Yeni not oluştur", "Create a new note") : Ago(ctx, note.LastWrite);
        }

        private void CrossFade(string text, bool animate)
        {
            var incoming = _aActive ? _b : _a;
            var outgoing = _aActive ? _a : _b;
            if (!animate)
            {
                outgoing.BeginAnimation(UIElement.OpacityProperty, null);
                outgoing.Text = text;
                outgoing.Opacity = 1;
                incoming.BeginAnimation(UIElement.OpacityProperty, null);
                incoming.Opacity = 0;
                return;
            }
            incoming.Text = text;
            var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
            incoming.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(380)) { EasingFunction = ease });
            outgoing.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(380)) { EasingFunction = ease });
            _aActive = !_aActive;
        }

        private void NewNote(DockWidgetContext ctx)
        {
            if (ctx.IsAuditMode) return;
            try
            {
                string dir = NotesFolder;
                IO.Directory.CreateDirectory(dir);
                var now = DateTime.Now;
                string path = IO.Path.Combine(dir, $"{ctx.T("Not", "Note")} {now:yyyy-MM-dd HHmmss}.md");
                IO.File.WriteAllText(path, $"# {ctx.T("Yeni not", "New note")} · {now.ToString("g", Culture(ctx))}\n\n", new System.Text.UTF8Encoding(false));
                NotesCache.At = DateTime.MinValue;
                ctx.CloseCard();
                DockWidgetActions.Run(ctx, path);
                EnsureScan(ctx, force: true);
            }
            catch (Exception ex)
            {
                CrashLogger.Log("DockWidget", ex, "Could not create a note.");
            }
        }

        private static void OpenFolder(DockWidgetContext ctx)
        {
            if (ctx.IsAuditMode) return;
            try { IO.Directory.CreateDirectory(NotesFolder); }
            catch (Exception ex) { CrashLogger.Log("DockWidget", ex, "Could not create the notes folder."); }
            ctx.CloseCard();
            DockWidgetActions.Run(ctx, NotesFolder);
        }

        protected override FrameworkElement? CreateCard(DockWidgetContext ctx)
        {
            var p = ctx.Palette;
            _cardKey = "\0";
            _cardPreview = Wrap(W.Text(p, "", 12.5, p.Foreground, ui: false));
            _cardPreview.MaxHeight = 150;
            var previewBox = new Border
            {
                Child = _cardPreview,
                Background = p.CardRowBackground,
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 0, 0, 8)
            };
            _cardMeta = W.Text(p, "", 11.5, p.SecondaryForeground, ui: false);
            _cardMeta.Margin = new Thickness(0, 0, 0, 10);
            var buttons = new WrapPanel();
            if (Note(ctx) is { Path.Length: > 0 } note)
                buttons.Children.Add(W.Button(p, ctx.T("Aç", "Open"), () => { ctx.CloseCard(); DockWidgetActions.Run(ctx, note.Path); }, GlyphOpen, primary: true));
            buttons.Children.Add(W.Button(p, ctx.T("Yeni not", "New note"), () => NewNote(ctx), GlyphAdd));
            buttons.Children.Add(W.Button(p, ctx.T("Klasörü aç", "Open folder"), () => OpenFolder(ctx), GlyphFolder));
            return W.Card(p, Descriptor.Glyph, Descriptor.Name(ctx.Lang), W.Column(previewBox, _cardMeta, buttons), 300);
        }

        protected override void RefreshCard(DockWidgetContext ctx)
        {
            if (_cardPreview is null || _cardMeta is null) return;
            var note = Note(ctx);
            string key = note is null ? "" : $"{note.Path}|{note.LastWrite.Ticks}";
            if (key != _cardKey)
            {
                _cardKey = key;
                _cardPreview.Text = note is null
                    ? ctx.T($"Henüz not yok. \"Yeni not\" ile {NotesFolder} klasöründe bir not oluşturabilirsin.",
                        $"No notes yet. \"New note\" creates one in {NotesFolder}.")
                    : note.Preview;
            }
            _cardMeta.Text = note is null ? "" : $"{(note.Path.Length > 0 ? IO.Path.GetFileName(note.Path) : note.Title)} · {Ago(ctx, note.LastWrite)}";
        }
    }

    // =============================================================================================
    // Calendar
    // =============================================================================================

    private sealed class CalendarTile : DockWidgetTileBase
    {
        private TextBlock _dow = null!;
        private TextBlock _day = null!;
        private TextBlock _title = null!;
        private TextBlock _when = null!;
        private Ellipse _dot = null!;
        private int _dotState = -1;
        private bool _attached;

        private Border? _cardHost;
        private string _cardKey = "\0";
        private TextBlock? _cardWhen;

        public CalendarTile(DockWidgetDescriptor d, DockWidgetContext c) : base(d, c) { }

        internal static MeetingOccurrence? Meeting(DockWidgetContext ctx)
        {
            MeetingOccurrence? m = null;
            try { m = ctx.Orch.Calendar.ActiveOrNextMeeting; }
            catch { /* calendar not ready */ }
            if (m is null && ctx.IsAuditMode)
            {
                var start = new DateTimeOffset(ctx.Now.AddMinutes(8));
                m = new MeetingOccurrence(ctx.T("Sprint planlama", "Sprint planning"), start.ToUniversalTime(),
                    start.AddMinutes(45).ToUniversalTime(), "https://meet.google.com/abc-defg-hij");
            }
            return m;
        }

        protected override FrameworkElement BuildContent(DockWidgetContext ctx)
        {
            var p = ctx.Palette;
            double h = ctx.TileHeight;
            double bh = Math.Max(16, h - 6);
            double band = Math.Round(bh * 0.36);
            _dow = W.Text(p, "", Math.Max(5.5, band * 0.7), Brushes.White, FontWeights.Bold);
            _dow.HorizontalAlignment = HorizontalAlignment.Center;
            var bandBorder = new Border
            {
                Height = band,
                Background = W.Brush(p.Negative),
                CornerRadius = new CornerRadius(5, 5, 0, 0),
                Child = _dow
            };
            _day = Tabular(W.Text(p, "", Math.Max(8, (bh - band) * 0.74), p.Foreground, FontWeights.SemiBold));
            _day.HorizontalAlignment = HorizontalAlignment.Center;
            var badgeGrid = new Grid();
            badgeGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            badgeGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(_day, 1);
            badgeGrid.Children.Add(bandBorder);
            badgeGrid.Children.Add(_day);
            var badge = new Border
            {
                Width = Math.Round(bh * 0.92),
                Height = bh,
                CornerRadius = new CornerRadius(5),
                Background = p.TileHoverBackground,
                BorderBrush = p.TileBorder,
                BorderThickness = new Thickness(0.6),
                Child = badgeGrid,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, Math.Max(4, h * 0.18), 0)
            };

            _title = W.Text(p, "", Fs(ctx, 0.27, 8), p.Foreground, FontWeights.SemiBold);
            double ds = Math.Max(4, Math.Round(h * 0.15));
            _dot = new Ellipse { Width = ds, Height = ds, Fill = W.Brush(p.Warning), Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
            _when = Tabular(W.Text(p, "", Fs(ctx, 0.21, 7), p.SecondaryForeground));
            var whenRow = new DockPanel();
            DockPanel.SetDock(_dot, Dock.Left);
            whenRow.Children.Add(_dot);
            whenRow.Children.Add(_when);
            var col = W.Column(_title, whenRow);

            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(col, 1);
            g.Children.Add(badge);
            g.Children.Add(col);
            return g;
        }

        private static string WhenText(DockWidgetContext ctx, MeetingOccurrence m, DateTime now)
        {
            var start = m.StartUtc.ToLocalTime().DateTime;
            var end = m.EndUtc.ToLocalTime().DateTime;
            if (m.IsAllDay)
                return start.Date <= now.Date && end > now ? ctx.T("Bugün · tüm gün", "Today · all day") : $"{DayAbbrev(ctx, start)} · {ctx.T("tüm gün", "all day")}";
            if (now >= start && now < end)
                return ctx.T($"Şimdi · {Dur(ctx, end - now)} kaldı", $"Now · {Dur(ctx, end - now)} left");
            var left = start - now;
            if (left.TotalHours < 12 || start.Date == now.Date)
                return ctx.T($"{Dur(ctx, left)} sonra", $"in {Dur(ctx, left)}");
            string day = start.Date == now.Date.AddDays(1) ? ctx.T("Yarın", "Tomorrow") : DayAbbrev(ctx, start);
            return $"{day} {start:HH:mm}";
        }

        private static bool IsActive(MeetingOccurrence m, DateTime now)
        {
            var s = m.StartUtc.ToLocalTime().DateTime;
            var e = m.EndUtc.ToLocalTime().DateTime;
            return now >= s && now < e;
        }

        protected override void Refresh(DockWidgetContext ctx)
        {
            var p = ctx.Palette;
            var now = ctx.Now;
            var m = Meeting(ctx);
            DateTime badgeDate = m is null || IsActive(m, now) ? now.Date : m.StartUtc.ToLocalTime().DateTime.Date;
            var culture = Culture(ctx);
            string dow = DayAbbrev(ctx, badgeDate).ToUpper(culture);
            if (_dow.Text != dow) _dow.Text = dow;
            W.SetTextAnimated(_day, badgeDate.Day.ToString(CultureInfo.InvariantCulture));
            if (m is null)
            {
                W.SetTextAnimated(_title, ctx.T("Etkinlik yok", "No events"));
                W.SetTextAnimated(_when, now.ToString(ctx.En ? "dddd, MMM d" : "d MMMM dddd", culture));
                SetDot(p, 0);
                return;
            }
            W.SetTextAnimated(_title, string.IsNullOrWhiteSpace(m.Summary) ? ctx.T("(Başlıksız)", "(Untitled)") : m.Summary);
            W.SetTextAnimated(_when, WhenText(ctx, m, now));
            bool active = IsActive(m, now) && !m.IsAllDay;
            var left = m.StartUtc.ToLocalTime().DateTime - now;
            SetDot(p, active ? 2 : !m.IsAllDay && left > TimeSpan.Zero && left <= TimeSpan.FromMinutes(10) ? 1 : 0);
        }

        private void SetDot(DockWidgetPalette p, int state)
        {
            if (state == _dotState) return;
            _dotState = state;
            _dot.Visibility = state == 0 ? Visibility.Collapsed : Visibility.Visible;
            _dot.Fill = W.Brush(state == 2 ? p.Positive : p.Warning);
            if (state != 0 && _attached) Breathe(_dot, 0.2, 0.6);
            else StopBreathe(_dot);
        }

        public override void OnAttached(DockWidgetContext ctx)
        {
            _attached = true;
            if (_dotState > 0) Breathe(_dot, 0.2, 0.6);
        }

        public override void OnDetached()
        {
            _attached = false;
            StopBreathe(_dot);
        }

        protected override FrameworkElement? CreateCard(DockWidgetContext ctx)
        {
            _cardHost = new Border();
            _cardKey = "\0";
            return W.Card(ctx.Palette, Descriptor.Glyph, Descriptor.Name(ctx.Lang), _cardHost, 300);
        }

        protected override void RefreshCard(DockWidgetContext ctx)
        {
            if (_cardHost is null) return;
            var p = ctx.Palette;
            var m = Meeting(ctx);
            var now = ctx.Now;
            string key = m is null ? "" : $"{m.Summary}|{m.StartUtc.Ticks}|{m.JoinUrl}";
            if (key != _cardKey)
            {
                _cardKey = key;
                _cardWhen = null;
                if (m is null)
                {
                    _cardHost.Child = W.Column(
                        Wrap(W.Text(p, ctx.T("Yaklaşan etkinlik yok.", "No upcoming events."), 13, p.Foreground, FontWeights.SemiBold)),
                        Wrap(W.Text(p, ctx.T("Takvim bağlantını (ICS) Ayarlar'dan ekleyebilirsin.", "You can add your calendar (ICS) link in Settings."), 12, p.SecondaryForeground, ui: false)),
                        new WrapPanel
                        {
                            Margin = new Thickness(0, 10, 0, 0),
                            Children = { W.Button(p, ctx.T("Ayarlar", "Settings"), () => { if (!ctx.IsAuditMode) { ctx.CloseCard(); ctx.Orch.RequestOpenSettings(); } }, GlyphSettings) }
                        });
                }
                else
                {
                    var culture = Culture(ctx);
                    var s = m.StartUtc.ToLocalTime().DateTime;
                    var e = m.EndUtc.ToLocalTime().DateTime;
                    var title = Wrap(W.Text(p, string.IsNullOrWhiteSpace(m.Summary) ? ctx.T("(Başlıksız)", "(Untitled)") : m.Summary, 15, p.Foreground, FontWeights.SemiBold));
                    title.Margin = new Thickness(0, 0, 0, 8);
                    var col = W.Column(title,
                        W.CardRow(p, ctx.T("Tarih", "Date"), s.ToString(ctx.En ? "dddd, MMMM d" : "d MMMM dddd", culture)),
                        W.CardRow(p, ctx.T("Saat", "Time"), m.IsAllDay ? ctx.T("Tüm gün", "All day") : $"{s:HH:mm} – {e:HH:mm}"),
                        W.CardRow(p, ctx.T("Süre", "Duration"), m.IsAllDay ? "—" : Dur(ctx, e - s)));
                    var whenRow = (DockPanel)W.CardRow(p, ctx.T("Başlangıç", "Starts"), "", W.Brush(p.Accent));
                    _cardWhen = whenRow.Children.OfType<TextBlock>().FirstOrDefault();
                    col.Children.Add(whenRow);
                    if (!string.IsNullOrWhiteSpace(m.JoinUrl))
                    {
                        col.Children.Add(new WrapPanel
                        {
                            Margin = new Thickness(0, 10, 0, 0),
                            Children =
                            {
                                W.Button(p, ctx.T("Toplantıya katıl", "Join meeting"), () =>
                                {
                                    if (ctx.IsAuditMode) return;
                                    ctx.CloseCard();
                                    ctx.Orch.JoinUpcomingMeeting();
                                }, GlyphVideo, primary: true)
                            }
                        });
                    }
                    _cardHost.Child = col;
                }
            }
            if (m is not null && _cardWhen is not null) W.SetTextAnimated(_cardWhen, WhenText(ctx, m, now));
        }
    }

    // =============================================================================================
    // Currency
    // =============================================================================================

    private sealed class CurrencyTile : DockWidgetTileBase
    {
        private static readonly string[] Common = ["USD", "EUR", "GBP", "TRY", "JPY"];
        private static readonly Dictionary<string, double> SampleUsdRates = new(StringComparer.OrdinalIgnoreCase)
        {
            ["USD"] = 1, ["TRY"] = 34.20, ["EUR"] = 0.92, ["GBP"] = 0.79, ["JPY"] = 151.30
        };

        private TextBlock _top = null!;
        private TextBlock _rate = null!;
        private TextBlock _code = null!;

        private TextBox? _amount;
        private TextBlock? _rateLine;
        private TextBlock? _fromCode;
        private TextBlock? _result;
        private WrapPanel? _chips;
        private RotateTransform? _swapRot;
        private double _swapAngle;
        private string _amountText = "100";

        public CurrencyTile(DockWidgetDescriptor d, DockWidgetContext c) : base(d, c) { }

        private static (string From, string To) Pair(DockWidgetContext ctx) =>
            ((ctx.Config.CurrencyFrom ?? "USD").Trim().ToUpperInvariant(), (ctx.Config.CurrencyTo ?? "TRY").Trim().ToUpperInvariant());

        private static double? Rate(DockWidgetContext ctx)
        {
            var (from, to) = Pair(ctx);
            if (from == to) return 1;
            var s = ctx.Data.Currency;
            if (s is not null && s.Rate > 0 && double.IsFinite(s.Rate))
            {
                if (string.Equals(s.From, from, StringComparison.OrdinalIgnoreCase) && string.Equals(s.To, to, StringComparison.OrdinalIgnoreCase)) return s.Rate;
                if (string.Equals(s.From, to, StringComparison.OrdinalIgnoreCase) && string.Equals(s.To, from, StringComparison.OrdinalIgnoreCase)) return 1 / s.Rate;
            }
            if (ctx.IsAuditMode && SampleUsdRates.TryGetValue(from, out var f) && SampleUsdRates.TryGetValue(to, out var t)) return t / f;
            return null;
        }

        private static string FormatAmount(DockWidgetContext ctx, double v)
        {
            double a = Math.Abs(v);
            string fmt = a >= 1000 ? "#,0.##" : a >= 1 ? "#,0.00" : a >= 0.01 ? "0.0000" : "0.000000";
            return v.ToString(fmt, Culture(ctx));
        }

        private static double? ParseAmount(string s)
        {
            s = s.Trim().Replace(" ", "");
            if (s.Length == 0) return null;
            int dot = s.LastIndexOf('.'), comma = s.LastIndexOf(',');
            if (dot >= 0 && comma >= 0) s = comma > dot ? s.Replace(".", "").Replace(',', '.') : s.Replace(",", "");
            else s = s.Replace(',', '.');
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : null;
        }

        protected override FrameworkElement BuildContent(DockWidgetContext ctx)
        {
            var p = ctx.Palette;
            _top = W.Text(p, "", Fs(ctx, 0.2, 7), p.SecondaryForeground);
            _rate = Tabular(W.Text(p, "", Fs(ctx, 0.31, 8.5), p.Foreground, FontWeights.SemiBold));
            _code = W.Text(p, "", Fs(ctx, 0.2, 7), p.SecondaryForeground);
            _code.Margin = new Thickness(3, 0, 0, 0);
            _code.VerticalAlignment = VerticalAlignment.Bottom;
            var bottom = new DockPanel { LastChildFill = false };
            DockPanel.SetDock(_rate, Dock.Left);
            DockPanel.SetDock(_code, Dock.Left);
            bottom.Children.Add(_rate);
            bottom.Children.Add(_code);
            return W.Column(_top, bottom);
        }

        protected override void Refresh(DockWidgetContext ctx)
        {
            var (from, to) = Pair(ctx);
            var rate = Rate(ctx);
            if (rate is { } r)
            {
                W.SetTextAnimated(_top, $"1 {from} =", animate: false);
                W.SetTextAnimated(_rate, FormatAmount(ctx, r));
            }
            else
            {
                W.SetTextAnimated(_top, ctx.Data.CurrencyStatus ?? $"1 {from} =", animate: false);
                W.SetTextAnimated(_rate, "—");
            }
            if (_code.Text != to) _code.Text = to;
        }

        protected override FrameworkElement? CreateCard(DockWidgetContext ctx)
        {
            var p = ctx.Palette;
            _rateLine = Tabular(W.Text(p, "", 12.5, p.SecondaryForeground, ui: false));
            _rateLine.Margin = new Thickness(0, 0, 0, 10);

            _amount = W.Input(p, ctx.T("Tutar", "Amount"), 150);
            _amount.Text = _amountText;
            _amount.TextChanged += (_, _) =>
            {
                if (_amount is null) return;
                _amountText = _amount.Text;
                UpdateCard(ctx);
            };
            _fromCode = W.Text(p, "", 14, p.Foreground, FontWeights.SemiBold);
            _fromCode.Margin = new Thickness(8, 0, 10, 0);
            var swap = W.IconButton(p, GlyphSwap, 32, () => Swap(ctx), p.CardRowBackground);
            swap.ToolTip = ctx.T("Para birimlerini değiştir", "Swap currencies");
            if (swap.Child is FrameworkElement glyph)
            {
                _swapRot = new RotateTransform(_swapAngle);
                glyph.RenderTransformOrigin = new Point(0.5, 0.5);
                glyph.RenderTransform = _swapRot;
            }
            var inputRow = new StackPanel { Orientation = Orientation.Horizontal };
            inputRow.Children.Add(_amount);
            inputRow.Children.Add(_fromCode);
            inputRow.Children.Add(swap);

            _result = Tabular(W.Text(p, "", 22, p.Foreground, FontWeights.SemiBold));
            _result.Margin = new Thickness(0, 10, 0, 12);

            var label = W.Text(p, ctx.T("Hedef para birimi", "Target currency"), 12, p.SecondaryForeground, ui: false);
            label.Margin = new Thickness(0, 0, 0, 6);
            _chips = new WrapPanel();
            BuildChips(ctx);
            UpdateCard(ctx);
            return W.Card(p, Descriptor.Glyph, Descriptor.Name(ctx.Lang), W.Column(_rateLine, inputRow, _result, label, _chips), 300);
        }

        private void BuildChips(DockWidgetContext ctx)
        {
            if (_chips is null) return;
            var p = ctx.Palette;
            var (_, to) = Pair(ctx);
            _chips.Children.Clear();
            foreach (var code in Common)
            {
                string c = code;
                _chips.Children.Add(W.Button(p, code, () => PickTarget(ctx, c), primary: code == to));
            }
        }

        private void PickTarget(DockWidgetContext ctx, string code)
        {
            var (_, to) = Pair(ctx);
            if (code == to || ctx.IsAuditMode) return;
            ctx.EditConfig(c =>
            {
                if (string.Equals(c.CurrencyFrom, code, StringComparison.OrdinalIgnoreCase)) c.CurrencyFrom = c.CurrencyTo;
                c.CurrencyTo = code;
            });
            ctx.Data.InvalidateNetworkData();
            BuildChips(ctx);
            Update(ctx);
            UpdateCard(ctx);
        }

        private void Swap(DockWidgetContext ctx)
        {
            _swapAngle += 180;
            _swapRot?.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(_swapAngle, TimeSpan.FromMilliseconds(420))
            {
                EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 }
            });
            if (ctx.IsAuditMode) return;
            ctx.EditConfig(c => (c.CurrencyFrom, c.CurrencyTo) = (c.CurrencyTo, c.CurrencyFrom));
            ctx.Data.InvalidateNetworkData();
            BuildChips(ctx);
            Update(ctx);
            UpdateCard(ctx);
        }

        private void UpdateCard(DockWidgetContext ctx)
        {
            if (_rateLine is null || _result is null || _fromCode is null) return;
            var (from, to) = Pair(ctx);
            var rate = Rate(ctx);
            if (_fromCode.Text != from) _fromCode.Text = from;
            W.SetTextAnimated(_rateLine, rate is { } r
                ? $"1 {from} = {FormatAmount(ctx, r)} {to}"
                : ctx.Data.CurrencyStatus ?? ctx.T("Kur yükleniyor…", "Loading rate…"));
            var amount = ParseAmount(_amountText);
            string result = amount is { } a && rate is { } rr ? $"{FormatAmount(ctx, a * rr)} {to}" : $"— {to}";
            W.SetTextAnimated(_result, result);
        }

        protected override void RefreshCard(DockWidgetContext ctx) => UpdateCard(ctx);
    }
}
