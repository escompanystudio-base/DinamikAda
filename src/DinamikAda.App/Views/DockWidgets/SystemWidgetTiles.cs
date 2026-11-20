using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using DinamikAda.Core.Abstractions;
using DinamikAda.Core.Widgets;

namespace DinamikAda.App.Views.DockWidgets;

/// <summary>System widgets: CPU/RAM rings, top processes, network, temperature, batteries, toggles, progress.</summary>
internal static class SystemWidgetTiles
{
    public static void Register(IDictionary<string, Func<DockWidgetDescriptor, DockWidgetContext, IDockWidgetTile>> map)
    {
        map["cpu"] = (d, c) => new LoadRingTile(d, c, isCpu: true);
        map["ram"] = (d, c) => new LoadRingTile(d, c, isCpu: false);
        map["top-cpu"] = (d, c) => new TopProcessTile(d, c);
        map["top-ram"] = (d, c) => new TopProcessTile(d, c);
        map["language"] = (d, c) => new LanguageTile(d, c);
        map["temperature"] = (d, c) => new TemperatureTile(d, c);
        map["network"] = (d, c) => new NetworkTile(d, c);
        map["battery"] = (d, c) => new BatteryBodyTile(d, c);
        map["battery-ring"] = (d, c) => new BatteryRingTile(d, c);
        map["battery-core"] = (d, c) => new BatteryCoreTile(d, c);
        map["battery-dots"] = (d, c) => new BatteryDotsTile(d, c);
        map["battery-gauge"] = (d, c) => new BatteryGaugeTile(d, c);
        map["quick-toggles"] = (d, c) => new QuickTogglesTile(d, c);
        map["brightness"] = (d, c) => new BrightnessTile(d, c);
        map["theme-toggle"] = (d, c) => new ThemeToggleTile(d, c);
        map["dnd"] = (d, c) => new DndTile(d, c);
        map["notifications"] = (d, c) => new NotificationsTile(d, c);
        map["show-desktop"] = (d, c) => new GlyphActionTile(d, c, "", x => DockWidgetActions.WinPlus(x, 'D'));
        map["keyboard"] = (d, c) => new GlyphActionTile(d, c, "", x => DockWidgetActions.Run(x, "osk.exe"));
        map["month-progress"] = (d, c) => new ProgressBarTile(d, c);
        map["day-progress"] = (d, c) => new ProgressBarTile(d, c);
        map["year-progress"] = (d, c) => new YearProgressTile(d, c);
    }

    // ---- Shared helpers ---------------------------------------------------------------------------

    private const string BoltGlyph = "";
    private const string PowerGlyph = "";

    private static double Fs(double h, double k, double min) => Math.Max(min, Math.Round(h * k * 2) / 2);

    /// <summary>"%42" in Turkish, "42%" in English.</summary>
    private static string Pct(DockWidgetContext ctx, double v)
    {
        int n = (int)Math.Round(double.IsFinite(v) ? Math.Clamp(v, 0, 100) : 0);
        return ctx.En ? $"{n}%" : $"%{n}";
    }

    private static string FormatMem(double mb) =>
        mb >= 1024
            ? (mb / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " GB"
            : mb.ToString("0", CultureInfo.InvariantCulture) + " MB";

    private static Color ColorOf(Brush b, Color fallback) => b is SolidColorBrush sb ? sb.Color : fallback;

    private static Color OnAccent(DockWidgetPalette p) => p.IsLight ? Colors.White : Color.FromRgb(0x05, 0x08, 0x10);

    /// <summary>Runs an async OS call without blocking the UI; failures are logged.</summary>
    private static void Fire(Func<Task> action, string what)
    {
        try
        {
            action().ContinueWith(t => Services.CrashLogger.Log("DockWidget", t.Exception, what),
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }
        catch (Exception ex)
        {
            Services.CrashLogger.Log("DockWidget", ex, what);
        }
    }

    private static void ColorTo(SolidColorBrush brush, Color c, double ms = 260)
    {
        brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(c, TimeSpan.FromMilliseconds(ms))
        {
            EasingFunction = Motion.Soft
        });
    }

    private static DoubleAnimation Forever(double from, double to, double seconds, bool autoReverse = true) =>
        new(from, to, TimeSpan.FromSeconds(seconds))
        {
            AutoReverse = autoReverse,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = autoReverse ? Motion.Gentle : null
        };

    /// <summary>Returns a frozen brush for a colour, reusing the last one while the colour is unchanged.</summary>
    private sealed class CachedBrush
    {
        private SolidColorBrush? _brush;
        public SolidColorBrush Get(Color c)
        {
            if (_brush is null || _brush.Color != c) _brush = W.Brush(c);
            return _brush;
        }
    }

    /// <summary>Card body made of CardRows; rebuilt only when the content changes.</summary>
    private sealed class RowsPanel
    {
        private string _sig = "\0";
        public StackPanel Panel { get; } = new();

        public void Set(DockWidgetPalette p, IReadOnlyList<(string Label, string Value)> rows)
        {
            string sig = string.Join("\u001F", rows.Select(r => r.Label + "\u001E" + r.Value));
            if (sig == _sig) return;
            _sig = sig;
            Panel.Children.Clear();
            foreach (var (label, value) in rows) Panel.Children.Add(W.CardRow(p, label, value));
        }
    }

    private static class Native
    {
        [DllImport("user32.dll")]
        public static extern nint GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

        [DllImport("user32.dll")]
        public static extern nint GetKeyboardLayout(uint threadId);
    }

    // ---- Battery state ----------------------------------------------------------------------------

    private readonly record struct BatteryState(bool Has, int Pct, bool Charging, double? Watts, TimeSpan? Remaining);

    private static BatteryState ReadBattery(DockWidgetContext ctx)
    {
        var t = ctx.Orch.Telemetry.Current;
        if (t.HasBattery) return new BatteryState(true, Math.Clamp(t.BatteryPercent, 0, 100), t.IsCharging, t.BatteryWatts, t.BatteryRemaining);
        if (ctx.IsAuditMode) return new BatteryState(true, 76, true, 18.4, TimeSpan.FromMinutes(42));
        return new BatteryState(false, 0, false, null, null);
    }

    private static Color BatteryColor(DockWidgetPalette p, BatteryState b) =>
        b.Charging ? p.Positive : b.Pct <= 15 ? p.Negative : b.Pct <= 30 ? p.Warning : p.Accent;

    private static readonly Dictionary<Color, Brush> BatteryBodies = new();

    /// <summary>
    /// Solid body colour for battery tiles: green while charging, red when low, amber when getting low, else the
    /// accent; always dark enough for white text.
    /// </summary>
    private static Brush BatteryBody(DockWidgetPalette p, BatteryState b)
    {
        Color c = !b.Has ? Color.FromRgb(0x5A, 0x60, 0x70)
            : b.Charging ? Color.FromRgb(0x2F, 0xC2, 0x5B)
            : b.Pct <= 20 ? Color.FromRgb(0xFF, 0x45, 0x3A)
            : b.Pct <= 30 ? Color.FromRgb(0xFF, 0x9F, 0x0A)
            : p.Accent;
        var safe = TileColors.ForWhiteText(new Rgb(c.R, c.G, c.B), 1.0);
        var key = Color.FromRgb(safe.R, safe.G, safe.B);
        if (!BatteryBodies.TryGetValue(key, out var brush)) BatteryBodies[key] = brush = Vivid.Solid(key);
        return brush;
    }

    // =============================================================================================
    // CPU / RAM ring (reference tile)
    // =============================================================================================

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
            if (_ring.Fill is not SolidColorBrush sb || sb.Color != color) _ring.Fill = W.Brush(color);
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
                W.Button(p, ctx.T("Görev Yöneticisi", "Task Manager"), () => DockWidgetActions.Run(ctx, "taskmgr.exe"), ""));
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

    // =============================================================================================
    // Top processes
    // =============================================================================================

    /// <summary>The three heaviest processes by CPU or memory, each with a tiny animated bar.</summary>
    private sealed class TopProcessTile : DockWidgetTileBase
    {
        private static readonly ProcessSummary[] SampleProcs =
        [
            new(1, "chrome", "", 1840, false, false, 18.4, 22),
            new(2, "Code", "", 960, false, false, 9.1, 8),
            new(3, "Teams", "", 720, false, false, 4.2, 5),
            new(4, "explorer", "", 310, false, false, 1.6),
            new(5, "DinamikAda", "", 180, false, false, 0.8)
        ];

        private sealed class ProcRow(Grid root, TextBlock name, TextBlock value, BarGauge bar)
        {
            public Grid Root { get; } = root;
            public TextBlock Name { get; } = name;
            public TextBlock Value { get; } = value;
            public BarGauge Bar { get; } = bar;
            public string Key { get; set; } = "";
        }

        private readonly ProcRow[] _rows = new ProcRow[3];
        private RowsPanel? _cardRows;

        public TopProcessTile(DockWidgetDescriptor d, DockWidgetContext c) : base(d, c) { }

        private bool IsCpu => Descriptor.Id == "top-cpu";

        protected override FrameworkElement BuildContent(DockWidgetContext ctx)
        {
            var p = ctx.Palette;
            double h = ctx.TileHeight;
            double rowH = Math.Max(7, (h - 4) / 3.0);
            double fs = Fs(h, 0.22, 6.5);
            var col = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var accent = W.Brush(IsCpu ? p.Accent : p.Positive);
            for (int i = 0; i < 3; i++)
            {
                var g = new Grid { Height = rowH };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Round(h * 0.42)) });
                var name = W.Text(p, "", fs, i == 0 ? p.Foreground : p.SecondaryForeground, FontWeights.SemiBold);
                var value = W.Text(p, "", fs, p.SecondaryForeground);
                value.Margin = new Thickness(3, 0, 3, 0);
                Grid.SetColumn(value, 1);
                var bar = new BarGauge { Height = Math.Max(2, rowH * 0.32), Track = p.Track, Fill = accent, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(bar, 2);
                g.Children.Add(name);
                g.Children.Add(value);
                g.Children.Add(bar);
                g.RenderTransform = new TranslateTransform();
                col.Children.Add(g);
                _rows[i] = new ProcRow(g, name, value, bar);
            }
            return col;
        }

        private List<ProcessSummary> Top(DockWidgetContext ctx, int n)
        {
            var arr = ctx.Orch.Telemetry.Current.TopOrHungProcesses;
            IEnumerable<ProcessSummary> src = arr.IsDefaultOrEmpty ? (ctx.IsAuditMode ? SampleProcs : []) : arr;
            bool cpu = IsCpu;
            return src.Where(x => !string.IsNullOrWhiteSpace(x.ProcessName))
                .OrderByDescending(x => cpu ? x.CpuPercent : x.MemoryMb)
                .Take(n)
                .ToList();
        }

        private string ValueText(DockWidgetContext ctx, ProcessSummary x) =>
            IsCpu ? Pct(ctx, x.CpuPercent) : FormatMem(x.MemoryMb);

        private static string CleanName(string n) =>
            n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n[..^4] : n;

        protected override void Refresh(DockWidgetContext ctx)
        {
            var top = Top(ctx, 3);
            double max = top.Count == 0 ? 1 : top.Max(x => IsCpu ? x.CpuPercent : x.MemoryMb);
            max = Math.Max(max, IsCpu ? 20 : 1024);
            for (int i = 0; i < 3; i++)
            {
                var r = _rows[i];
                var x = i < top.Count ? top[i] : null;
                string key = x is null ? "" : CleanName(x.ProcessName);
                string val = x is null ? "" : ValueText(ctx, x);
                if (key != r.Key)
                {
                    bool had = r.Key.Length > 0;
                    r.Key = key;
                    r.Name.Text = key.Length == 0 ? (i == 0 ? "—" : "") : key;
                    r.Value.Text = val;
                    if (had) SlideIn(r.Root, i);
                }
                else if (r.Value.Text != val)
                {
                    r.Value.Text = val;
                }
                r.Bar.AnimateTo(x is null ? 0 : (IsCpu ? x.CpuPercent : x.MemoryMb) / max);
            }
        }

        private static void SlideIn(FrameworkElement el, int index)
        {
            var begin = TimeSpan.FromMilliseconds(index * 45);
            var ease = Motion.Soft;
            if (el.RenderTransform is TranslateTransform tt && !tt.IsFrozen)
                tt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(7, 0, TimeSpan.FromMilliseconds(300)) { BeginTime = begin, EasingFunction = ease });
            el.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.1, 1, TimeSpan.FromMilliseconds(300)) { BeginTime = begin, EasingFunction = ease });
        }

        protected override FrameworkElement? CreateCard(DockWidgetContext ctx)
        {
            var p = ctx.Palette;
            _cardRows = new RowsPanel();
            return W.Card(p, Descriptor.Glyph, Descriptor.Name(ctx.Lang), _cardRows.Panel, 280,
                W.Button(p, ctx.T("Görev Yöneticisi", "Task Manager"), () => DockWidgetActions.Run(ctx, "taskmgr.exe"), ""));
        }

        protected override void RefreshCard(DockWidgetContext ctx)
        {
            if (_cardRows is null) return;
            var top = Top(ctx, 5);
            var rows = new List<(string, string)>();
            foreach (var x in top)
            {
                string name = CleanName(x.ProcessName) + (x.ProcessCount > 1 ? $" ×{x.ProcessCount}" : "");
                string val = IsCpu ? $"{Pct(ctx, x.CpuPercent)} · {FormatMem(x.MemoryMb)}" : $"{FormatMem(x.MemoryMb)} · {Pct(ctx, x.CpuPercent)}";
                rows.Add((name, val));
            }
            if (rows.Count == 0) rows.Add((ctx.T("Veri bekleniyor", "Waiting for data"), "—"));
            _cardRows.Set(ctx.Palette, rows);
        }
    }

    // =============================================================================================
    // Keyboard language
    // =============================================================================================

    /// <summary>Current keyboard layout of the foreground window as a key cap; click = Win+Space.</summary>
    private sealed class LanguageTile : DockWidgetTileBase
    {
        private TextBlock _text = null!;
        private ScaleTransform _flip = null!;
        private Border _cap = null!;
        private string _pending = "";
        private bool _flipping;

        public LanguageTile(DockWidgetDescriptor d, DockWidgetContext c) : base(d, c) { }

        protected override FrameworkElement BuildContent(DockWidgetContext ctx)
        {
            var p = ctx.Palette;
            double h = ctx.TileHeight;
            _text = W.Text(p, "", Fs(h, 0.34, 9), weight: FontWeights.SemiBold);
            _text.HorizontalAlignment = HorizontalAlignment.Center;
            _text.TextTrimming = TextTrimming.None;
            _flip = new ScaleTransform(1, 1);
            _text.RenderTransformOrigin = new Point(0.5, 0.5);
            _text.RenderTransform = _flip;
            _cap = new Border
            {
                Width = Math.Round(h * 0.74),
                Height = Math.Round(h * 0.6),
                CornerRadius = new CornerRadius(Math.Max(3, h * 0.12)),
                BorderBrush = p.SecondaryForeground,
                BorderThickness = new Thickness(1),
                Background = p.CardRowBackground,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Child = _text
            };
            return _cap;
        }

        private static string ReadLayout()
        {
            try
            {
                nint hwnd = Native.GetForegroundWindow();
                uint tid = hwnd == 0 ? 0 : Native.GetWindowThreadProcessId(hwnd, out _);
                nint hkl = Native.GetKeyboardLayout(tid);
                int langId = (int)((long)hkl & 0xFFFF);
                if (langId == 0) return "—";
                var ci = CultureInfo.GetCultureInfo(langId);
                string s = ci.TwoLetterISOLanguageName.ToUpperInvariant();
                return s.Length is > 0 and <= 3 ? s : "—";
            }
            catch (CultureNotFoundException)
            {
                return "—";
            }
            catch (Exception)
            {
                return "—";
            }
        }

        protected override void Refresh(DockWidgetContext ctx)
        {
            string lang = ctx.IsAuditMode ? (ctx.En ? "EN" : "TR") : ReadLayout();
            if (string.IsNullOrEmpty(_text.Text)) { _text.Text = lang; return; }
            if (lang != _text.Text || (_flipping && lang != _pending)) Flip(lang);
            _cap.ToolTip = ctx.T($"Klavye: {lang} · tıkla: değiştir", $"Keyboard: {lang} · click to switch");
        }

        private void Flip(string text)
        {
            _pending = text;
            if (_flipping) return;
            _flipping = true;
            var close = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(120)) { EasingFunction = Motion.Exit };
            close.Completed += (_, _) =>
            {
                _text.Text = _pending;
                _flipping = false;
                _flip.BeginAnimation(ScaleTransform.ScaleYProperty,
                    new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)) { EasingFunction = Motion.Soft });
            };
            _flip.BeginAnimation(ScaleTransform.ScaleYProperty, close);
        }

        public override bool OnPrimaryClick(DockWidgetContext ctx)
        {
            DockWidgetActions.SendChord(ctx, 0x5B, 0x20);
            return true;
        }
    }

    // =============================================================================================
    // Temperature
    // =============================================================================================

    /// <summary>Background, cached (30 s) CPU thermal-zone read through WMI; disables itself on failure.</summary>
    private static class ThermalProbe
    {
        private static double _cpuC = double.NaN;
        private static long _lastTicks;
        private static int _busy;
        private static volatile bool _disabled;

        public static double? CpuC
        {
            get
            {
                Kick();
                double v = Volatile.Read(ref _cpuC);
                return double.IsFinite(v) ? v : null;
            }
        }

        private static void Kick()
        {
            if (_disabled) return;
            long now = Environment.TickCount64;
            if (Interlocked.Read(ref _lastTicks) != 0 && now - Interlocked.Read(ref _lastTicks) < 30_000) return;
            if (Interlocked.Exchange(ref _busy, 1) == 1) return;
            Interlocked.Exchange(ref _lastTicks, now);
            Task.Run(() =>
            {
                try
                {
                    using var searcher = new System.Management.ManagementObjectSearcher(
                        "root\\WMI", "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");
                    using var results = searcher.Get();
                    double best = double.NaN;
                    foreach (var o in results)
                    {
                        using (o)
                        {
                            double raw = Convert.ToDouble(o["CurrentTemperature"], CultureInfo.InvariantCulture);
                            double c = raw / 10.0 - 273.15;
                            if (c > 1 && c < 130) best = double.IsNaN(best) ? c : Math.Max(best, c);
                        }
                    }
                    Volatile.Write(ref _cpuC, best);
                    if (double.IsNaN(best)) _disabled = true;
                }
                catch (Exception)
                {
                    // Access denied / not supported: silently stop trying.
                    _disabled = true;
                    Volatile.Write(ref _cpuC, double.NaN);
                }
                finally
                {
                    Interlocked.Exchange(ref _busy, 0);
                }
            });
        }
    }

    /// <summary>Temperature gauge (GPU or CPU thermal zone) with CPU and RAM load next to it.</summary>
    private sealed class TemperatureTile : DockWidgetTileBase
    {
        private RingGauge _ring = null!;
        private SolidColorBrush _fill = null!;
        private TextBlock _temp = null!;
        private TextBlock _cpu = null!;
        private TextBlock _ram = null!;
        private Color _fillTarget;
        private RowsPanel? _cardRows;

        public TemperatureTile(DockWidgetDescriptor d, DockWidgetContext c) : base(d, c) { }

        protected override FrameworkElement BuildContent(DockWidgetContext ctx)
        {
            var p = ctx.Palette;
            double h = ctx.TileHeight;
            double size = h - 4;
            _ring = W.Ring(p, size, Math.Max(2.2, h * 0.085), p.Accent);
            _ring.GapDegrees = 90;
            _fill = new SolidColorBrush(p.Accent);
            _fillTarget = p.Accent;
            _ring.Fill = _fill;
            _temp = W.Text(p, "", Fs(h, 0.25, 7.5), weight: FontWeights.SemiBold);
            _temp.HorizontalAlignment = HorizontalAlignment.Center;
            _temp.TextTrimming = TextTrimming.None;
            var gauge = new Grid { Width = size, Height = size };
            gauge.Children.Add(_ring);
            gauge.Children.Add(_temp);

            double fs = Fs(h, 0.23, 7);
            _cpu = W.Text(p, "", fs, p.SecondaryForeground);
            _ram = W.Text(p, "", fs, p.SecondaryForeground);
            var lines = W.Column(_cpu, _ram);
            lines.Margin = new Thickness(Math.Round(h * 0.14), 0, 0, 0);

            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.Children.Add(gauge);
            Grid.SetColumn(lines, 1);
            g.Children.Add(lines);
            return g;
        }

        protected override Thickness ContentPadding(double h) => new(Math.Round(h * 0.1), 2, Math.Round(h * 0.18), 2);

        private static (double? Temp, bool IsGpu, double? Cpu) ReadTemp(DockWidgetContext ctx)
        {
            var t = ctx.Orch.Telemetry.Current;
            if (ctx.IsAuditMode) return (t.GpuTemperatureC ?? 58, true, null);
            double? cpu = ThermalProbe.CpuC;
            if (t.GpuTemperatureC is double g && double.IsFinite(g) && g > 0) return (g, true, cpu);
            return (cpu, false, cpu);
        }

        protected override void Refresh(DockWidgetContext ctx)
        {
            var p = ctx.Palette;
            var t = ctx.Orch.Telemetry.Current;
            var (temp, _, _) = ReadTemp(ctx);
            if (temp is double v)
            {
                W.SetTextAnimated(_temp, $"{Math.Round(v):0}°");
                _ring.AnimateTo((v - 20) / 80.0, 700);
                var c = v >= 82 ? p.Negative : v >= 65 ? p.Warning : p.Accent;
                if (c != _fillTarget)
                {
                    _fillTarget = c;
                    ColorTo(_fill, c, 600);
                }
            }
            else
            {
                W.SetTextAnimated(_temp, "—", false);
                _ring.AnimateTo(0);
            }
            double cpuPct = t.CpuPercent, ramPct = t.MemoryPercent;
            if (ctx.IsAuditMode && cpuPct <= 0) { cpuPct = 23; ramPct = 54; }
            W.SetTextAnimated(_cpu, "CPU " + Pct(ctx, cpuPct));
            W.SetTextAnimated(_ram, "RAM " + Pct(ctx, ramPct));
        }

        protected override FrameworkElement? CreateCard(DockWidgetContext ctx)
        {
            _cardRows = new RowsPanel();
            return W.Card(ctx.Palette, Descriptor.Glyph, Descriptor.Name(ctx.Lang), _cardRows.Panel, 280);
        }

        protected override void RefreshCard(DockWidgetContext ctx)
        {
            if (_cardRows is null) return;
            var t = ctx.Orch.Telemetry.Current;
            var (temp, isGpu, cpu) = ReadTemp(ctx);
            string none = "—";
            var rows = new List<(string, string)>
            {
                (ctx.T("Kaynak", "Source"), temp is null ? ctx.T("Sensör yok", "No sensor") : isGpu ? "GPU" : ctx.T("CPU termal bölgesi", "CPU thermal zone")),
                ("GPU", string.IsNullOrWhiteSpace(t.GpuName) ? (ctx.IsAuditMode ? "GeForce RTX 4070" : none) : t.GpuName!),
                (ctx.T("GPU sıcaklığı", "GPU temperature"), t.GpuTemperatureC is double g ? $"{g:0} °C" : (ctx.IsAuditMode ? "58 °C" : none)),
                (ctx.T("CPU bölgesi", "CPU zone"), cpu is double cz ? $"{cz:0} °C" : none),
                (ctx.T("Fan", "Fan"), t.GpuFanRpm is int rpm ? $"{rpm} RPM" + (t.GpuFanPercent is int fp ? $" · {Pct(ctx, fp)}" : "") :
                    t.GpuFanPercent is int fp2 ? Pct(ctx, fp2) : (ctx.IsAuditMode ? "1450 RPM" : none)),
                (ctx.T("GPU yükü", "GPU load"), Pct(ctx, t.GpuPercent)),
                ("CPU / RAM", $"{Pct(ctx, t.CpuPercent)} · {Pct(ctx, t.MemoryPercent)}")
            };
            _cardRows.Set(ctx.Palette, rows);
        }
    }

    // =============================================================================================
    // Network
    // =============================================================================================

    /// <summary>Download/upload rates over a live, sliding sparkline.</summary>
    private sealed class NetworkTile : DockWidgetTileBase
    {
        private Sparkline _chart = null!;
        private TranslateTransform _slide = null!;
        private TextBlock _down = null!;
        private TextBlock _up = null!;
        private double[] _last = [];
        private Sparkline? _cardChart;
        private RowsPanel? _cardRows;

        public NetworkTile(DockWidgetDescriptor d, DockWidgetContext c) : base(d, c) { }

        protected override FrameworkElement BuildContent(DockWidgetContext ctx)
        {
            var p = ctx.Palette;
            double h = ctx.TileHeight;
            _slide = new TranslateTransform();
            _chart = new Sparkline
            {
                Stroke = p.AccentBrush,
                SecondStroke = W.Brush(p.Positive, 0xB0),
                StrokeThickness = 1.2,
                ShowHead = true,
                Opacity = 0.9,
                RenderTransform = _slide,
                Margin = new Thickness(0, h * 0.12, 0, 1)
            };
            var chartHost = new Border { ClipToBounds = true, Child = _chart };

            double fs = Fs(h, 0.23, 7);
            _down = W.Text(p, "", fs, weight: FontWeights.SemiBold);
            _up = W.Text(p, "", fs, p.SecondaryForeground);
            var texts = W.Column(_down, _up);
            texts.HorizontalAlignment = HorizontalAlignment.Left;

            var g = new Grid();
            g.Children.Add(chartHost);
            g.Children.Add(texts);
            return g;
        }

        private static (double[] Down, double[] Up) Series(DockWidgetContext ctx)
        {
            var down = ctx.Data.NetDownHistory;
            var up = ctx.Data.NetUpHistory;
            if (ctx.IsAuditMode && down.All(v => v <= 0))
            {
                int s = ctx.Now.Second;
                down = Enumerable.Range(0, 60).Select(i => 40 + 26 * Math.Sin((i + s) / 5.0) + 12 * Math.Sin((i + s) / 1.7)).ToArray();
                up = Enumerable.Range(0, 60).Select(i => 6 + 3 * Math.Sin((i + s) / 4.0)).ToArray();
            }
            return (down, up);
        }

        /// <summary>Telemetry rates are megabits per second; FormatRate expects megabytes per second.</summary>
        private static string Rate(double mbps) => DockWidgetMath.FormatRate(mbps / 8.0);

        protected override void Refresh(DockWidgetContext ctx)
        {
            var (down, up) = Series(ctx);
            var t = ctx.Orch.Telemetry.Current;
            double d = ctx.IsAuditMode && t.NetDownMbps <= 0 && down.Length > 0 ? down[^1] : t.NetDownMbps;
            double u = ctx.IsAuditMode && t.NetUpMbps <= 0 && up.Length > 0 ? up[^1] : t.NetUpMbps;
            W.SetTextAnimated(_down, "↓ " + Rate(d), false);
            W.SetTextAnimated(_up, "↑ " + Rate(u), false);

            if (!down.AsSpan().SequenceEqual(_last))
            {
                bool hadData = _last.Length > 0;
                _last = down;
                _chart.SetValues(down, up);
                if (hadData && _chart.ActualWidth > 0 && down.Length > 1)
                {
                    double step = _chart.ActualWidth / (down.Length - 1);
                    _slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(step, 0, TimeSpan.FromMilliseconds(850)));
                }
            }
        }

        protected override FrameworkElement? CreateCard(DockWidgetContext ctx)
        {
            var p = ctx.Palette;
            _cardChart = new Sparkline { Height = 80, Stroke = p.AccentBrush, SecondStroke = W.Brush(p.Positive, 0xC0), Margin = new Thickness(0, 4, 0, 8) };
            _cardRows = new RowsPanel();
            return W.Card(p, Descriptor.Glyph, Descriptor.Name(ctx.Lang), W.Column(_cardChart, _cardRows.Panel), 300,
                W.Button(p, "", () => DockWidgetActions.OpenSettings(ctx, "ms-settings:network-status"), ""));
        }

        protected override void RefreshCard(DockWidgetContext ctx)
        {
            var (down, up) = Series(ctx);
            _cardChart?.SetValues(down, up);
            if (_cardRows is null) return;
            var t = ctx.Orch.Telemetry.Current;
            var c = ctx.Orch.Connectivity.Current;
            double d = ctx.IsAuditMode && t.NetDownMbps <= 0 && down.Length > 0 ? down[^1] : t.NetDownMbps;
            double u = ctx.IsAuditMode && t.NetUpMbps <= 0 && up.Length > 0 ? up[^1] : t.NetUpMbps;
            string link;
            if (c.IsEthernetConnected)
                link = "Ethernet" + (string.IsNullOrWhiteSpace(c.EthernetSpeedText) ? "" : $" · {c.EthernetSpeedText}");
            else if (!string.IsNullOrWhiteSpace(c.WifiSsid))
                link = $"Wi-Fi · {c.WifiSsid}" + (c.WifiSignalPercent is int sig ? $" ({Pct(ctx, sig)})" : "");
            else if (ctx.IsAuditMode)
                link = "Wi-Fi · Ev-5G (82%)";
            else
                link = ctx.T("Bağlı değil", "Not connected");
            var rows = new List<(string, string)>
            {
                (ctx.T("İndirme", "Download"), Rate(d)),
                (ctx.T("Yükleme", "Upload"), Rate(u)),
                (ctx.T("Bağlantı", "Connection"), link)
            };
            if (t.PingMs is double ping)
                rows.Add(("Ping", $"{ping:0} ms" + (string.IsNullOrWhiteSpace(t.PingHost) ? "" : $" · {t.PingHost}")));
            else if (ctx.IsAuditMode)
                rows.Add(("Ping", "18 ms"));
            _cardRows.Set(ctx.Palette, rows);
        }
    }

    // =============================================================================================
    // Batteries
    // =============================================================================================

    /// <summary>Common battery tile: shared detail card and a charging-loop switch.</summary>
    private abstract class BatteryTileBase : DockWidgetTileBase
    {
        private RowsPanel? _cardRows;
        private bool _attached;
        private bool _loopOn;

        protected BatteryTileBase(DockWidgetDescriptor d, DockWidgetContext c) : base(d, c) { }

        /// <summary>Starts or stops the tile's looping charging animation.</summary>
        protected abstract void SetChargingLoop(bool on);

        protected void UpdateLoop(bool charging)
        {
            bool want = _attached && charging;
            if (want == _loopOn) return;
            _loopOn = want;
            SetChargingLoop(want);
        }

        public override void OnAttached(DockWidgetContext ctx)
        {
            _attached = true;
            UpdateLoop(ReadBattery(ctx).Charging);
        }

        public override void OnDetached()
        {
            _attached = false;
            UpdateLoop(false);
        }

        protected override FrameworkElement? CreateCard(DockWidgetContext ctx)
        {
            var p = ctx.Palette;
            _cardRows = new RowsPanel();
            return W.Card(p, Descriptor.Glyph, Descriptor.Name(ctx.Lang), _cardRows.Panel, 280,
                W.Button(p, "", () => DockWidgetActions.OpenSettings(ctx, "ms-settings:batterysaver"), ""));
        }

        protected override void RefreshCard(DockWidgetContext ctx)
        {
            if (_cardRows is null) return;
            var b = ReadBattery(ctx);
            var c = ctx.Orch.Connectivity.Current;
            var rows = new List<(string, string)>();
            if (!b.Has)
            {
                rows.Add((ctx.T("Güç", "Power"), ctx.T("AC adaptör (pil yok)", "AC power (no battery)")));
            }
            else
            {
                rows.Add((ctx.T("Şarj", "Charge"), Pct(ctx, b.Pct)));
                rows.Add((ctx.T("Durum", "State"),
                    b.Charging ? (b.Pct >= 100 ? ctx.T("Dolu, prizde", "Full, plugged in") : ctx.T("Şarj oluyor", "Charging"))
                               : ctx.T("Pilde", "On battery")));
                rows.Add((b.Charging ? ctx.T("Dolmasına", "Until full") : ctx.T("Kalan süre", "Time left"),
                    b.Remaining is TimeSpan r && r > TimeSpan.Zero ? DockWidgetMath.FormatDuration(r, ctx.Lang) : "—"));
                rows.Add((ctx.T("Güç", "Power"), b.Watts is double w && double.IsFinite(w) ? $"{Math.Abs(w):0.0} W" : "—"));
            }
            if (!string.IsNullOrWhiteSpace(c.ActivePowerPlanName)) rows.Add((ctx.T("Güç planı", "Power plan"), c.ActivePowerPlanName!));
            if (c.IsEnergySaverOn) rows.Add((ctx.T("Enerji tasarrufu", "Energy saver"), ctx.T("Açık", "On")));
            _cardRows.Set(ctx.Palette, rows);
        }
    }

    /// <summary>Horizontal battery body with a nub, animated fill, bolt and a sheen while charging.</summary>
    private sealed class BatteryBodyTile : BatteryTileBase
    {
        private Grid _body = null!;
        private BarGauge _fill = null!;
        private Rectangle _sheen = null!;
        private TranslateTransform _sheenMove = null!;
        private TextBlock _bolt = null!;
        private TextBlock _pct = null!;
        private TextBlock _plug = null!;
        private readonly CachedBrush _fillBrush = new();
        private double _bodyW;

        public BatteryBodyTile(DockWidgetDescriptor d, DockWidgetContext c) : base(d, c) { }

        protected override FrameworkElement BuildContent(DockWidgetContext ctx)
        {
            var p = ctx.Palette;
            double h = ctx.TileHeight;
            _bodyW = Math.Round(h * 0.84);
            double bodyH = Math.Round(h * 0.46);
            _fill = new BarGauge { Track = Brushes.Transparent, Fill = p.AccentBrush, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
            _sheenMove = new TranslateTransform(-_bodyW, 0);
            var sheenBrush = new LinearGradientBrush(
                new GradientStopCollection
                {
                    new(Color.FromArgb(0, 255, 255, 255), 0),
                    new(Color.FromArgb(0x70, 255, 255, 255), 0.5),
                    new(Color.FromArgb(0, 255, 255, 255), 1)
                }, 0);
            sheenBrush.Freeze();
            _sheen = new Rectangle { Width = _bodyW * 0.45, Fill = sheenBrush, HorizontalAlignment = HorizontalAlignment.Left, RenderTransform = _sheenMove, Opacity = 0 };
            var inner = new Grid { ClipToBounds = true };
            inner.Children.Add(_fill);
            inner.Children.Add(_sheen);
            var shell = new Border
            {
                Width = _bodyW,
                Height = bodyH,
                CornerRadius = new CornerRadius(bodyH * 0.3),
                BorderBrush = p.SecondaryForeground,
                BorderThickness = new Thickness(1.2),
                Padding = new Thickness(1.6),
                Child = inner
            };
            _bolt = W.Glyph(p, BoltGlyph, bodyH * 0.72, p.Foreground);
            _bolt.Visibility = Visibility.Collapsed;
            var nub = new Border
            {
                Width = Math.Max(2, h * 0.07),
                Height = bodyH * 0.42,
                CornerRadius = new CornerRadius(0, 2, 2, 0),
                Background = p.SecondaryForeground,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(1, 0, 0, 0)
            };
            _body = new Grid { VerticalAlignment = VerticalAlignment.Center };
            _body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _body.Children.Add(shell);
            _body.Children.Add(_bolt);
            Grid.SetColumn(nub, 1);
            _body.Children.Add(nub);

            _plug = W.Glyph(p, PowerGlyph, h * 0.4, p.SecondaryForeground);
            _plug.Visibility = Visibility.Collapsed;
            _pct = W.Text(p, "", Fs(h, 0.27, 8), weight: FontWeights.SemiBold);
            _pct.TextTrimming = TextTrimming.None;
            var row = W.Row(Math.Round(h * 0.14), _body, _plug, _pct);
            row.HorizontalAlignment = HorizontalAlignment.Center;
            return row;
        }

        protected override void Refresh(DockWidgetContext ctx)
        {
            var b = ReadBattery(ctx);
            if (!b.Has)
            {
                _body.Visibility = Visibility.Collapsed;
                _plug.Visibility = Visibility.Visible;
                W.SetTextAnimated(_pct, "AC", false);
                UpdateLoop(false);
                return;
            }
            _body.Visibility = Visibility.Visible;
            _plug.Visibility = Visibility.Collapsed;
            var fillBrush = _fillBrush.Get(BatteryColor(ctx.Palette, b));
            if (!ReferenceEquals(_fill.Fill, fillBrush))
            {
                _fill.Fill = fillBrush;
                _fill.InvalidateVisual();
            }
            _fill.AnimateTo(b.Pct / 100.0, 700);
            _bolt.Visibility = b.Charging ? Visibility.Visible : Visibility.Collapsed;
            W.SetTextAnimated(_pct, Pct(ctx, b.Pct));
            UpdateLoop(b.Charging);
        }

        protected override void SetChargingLoop(bool on)
        {
            if (on)
            {
                _sheen.Opacity = 1;
                _sheenMove.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-_bodyW * 0.5, _bodyW, TimeSpan.FromSeconds(1.8))
                {
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = Motion.Gentle
                });
            }
            else
            {
                _sheenMove.BeginAnimation(TranslateTransform.XProperty, null);
                _sheen.Opacity = 0;
            }
        }
    }

    /// <summary>Charge as a ring around the number; a spark runs round the ring while charging.</summary>
    private sealed class BatteryRingTile : BatteryTileBase
    {
        private RingGauge _ring = null!;
        private TextBlock _value = null!;
        private TextBlock _bolt = null!;
        private readonly CachedBrush _fill = new();

        public BatteryRingTile(DockWidgetDescriptor d, DockWidgetContext c) : base(d, c) { }

        protected override bool VividByDefault => true;

        protected override FrameworkElement BuildContent(DockWidgetContext ctx)
        {
            var p = Vivid.On(ctx.Palette);
            double h = ctx.TileHeight;
            _ring = W.Ring(p, Math.Round(h * 0.8), Math.Max(1.6, Math.Round(h * 0.06 * 2) / 2), Colors.White);
            _value = W.Text(p, "", Fs(h, 0.36, 10), Vivid.OnColor, FontWeights.Bold);
            _value.HorizontalAlignment = HorizontalAlignment.Center;
            _value.TextTrimming = TextTrimming.None;
            _bolt = W.Glyph(p, BoltGlyph, Math.Max(6, Math.Round(h * 0.17)), Vivid.OnColor);
            _bolt.VerticalAlignment = VerticalAlignment.Bottom;
            _bolt.Margin = new Thickness(0, 0, 0, Math.Round(h * 0.1));
            _bolt.Visibility = Visibility.Collapsed;
            var g = new Grid();
            g.Children.Add(_ring);
            g.Children.Add(_value);
            g.Children.Add(_bolt);
            return g;
        }

        protected override void Refresh(DockWidgetContext ctx)
        {
            var b = ReadBattery(ctx);
            if (!b.Has)
            {
                SetTileBackground(BatteryBody(ctx.Palette, b));
                _ring.AnimateTo(1);
                W.SetTextAnimated(_value, "AC", false);
                _bolt.Visibility = Visibility.Collapsed;
                UpdateLoop(false);
                return;
            }
            SetTileBackground(BatteryBody(ctx.Palette, b));
            _ring.AnimateTo(b.Pct / 100.0, 700);
            W.SetTextAnimated(_value, b.Pct.ToString(CultureInfo.InvariantCulture));
            _bolt.Visibility = b.Charging ? Visibility.Visible : Visibility.Collapsed;
            UpdateLoop(b.Charging);
        }

        protected override void SetChargingLoop(bool on)
        {
            if (on) _ring.BeginAnimation(RingGauge.SparkAngleProperty, Forever(0, 360, 2.4, autoReverse: false));
            else _ring.BeginAnimation(RingGauge.SparkAngleProperty, null);
        }
    }

    /// <summary>Twelve cells around a circle lit to the charge; staggered lighting and a charging pulse.</summary>
    private sealed class BatteryCoreTile : BatteryTileBase
    {
        private const int Cells = 12;
        private readonly Rectangle[] _cells = new Rectangle[Cells];
        private SolidColorBrush _lit = null!;
        private Brush _dim = null!;
        private TextBlock _value = null!;
        private int _litCount = -1;

        public BatteryCoreTile(DockWidgetDescriptor d, DockWidgetContext c) : base(d, c) { }

        protected override bool VividByDefault => true;

        protected override FrameworkElement BuildContent(DockWidgetContext ctx)
        {
            var p = Vivid.On(ctx.Palette);
            double h = ctx.TileHeight;
            double s = Math.Round(h * 0.84);
            _lit = new SolidColorBrush(Colors.White);
            _dim = Vivid.OnColorTrack;
            var canvas = new Canvas { Width = s, Height = s, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            double cw = Math.Max(1.6, s * 0.1), ch = Math.Max(3, s * 0.2);
            double r = s / 2 - ch / 2 - 0.5;
            var center = new Point(s / 2, s / 2);
            for (int i = 0; i < Cells; i++)
            {
                double angle = i * 360.0 / Cells;
                var pt = RingGauge.PointOn(center, r, angle);
                var rect = new Rectangle
                {
                    Width = cw,
                    Height = ch,
                    RadiusX = cw / 2,
                    RadiusY = cw / 2,
                    Fill = _dim,
                    RenderTransformOrigin = new Point(0.5, 0.5),
                    RenderTransform = new RotateTransform(angle)
                };
                Canvas.SetLeft(rect, pt.X - cw / 2);
                Canvas.SetTop(rect, pt.Y - ch / 2);
                canvas.Children.Add(rect);
                _cells[i] = rect;
            }
            _value = W.Text(p, "", Fs(h, 0.3, 8.5), Vivid.OnColor, FontWeights.Bold);
            _value.HorizontalAlignment = HorizontalAlignment.Center;
            _value.TextTrimming = TextTrimming.None;
            var g = new Grid();
            g.Children.Add(canvas);
            g.Children.Add(_value);
            return g;
        }

        protected override void Refresh(DockWidgetContext ctx)
        {
            var b = ReadBattery(ctx);
            int pct = b.Has ? b.Pct : 100;
            SetTileBackground(BatteryBody(ctx.Palette, b));
            W.SetTextAnimated(_value, b.Has ? pct.ToString(CultureInfo.InvariantCulture) : "AC", b.Has);
            int lit = DockWidgetMath.LitCells(pct, Cells);
            if (lit != _litCount)
            {
                int from = Math.Max(0, _litCount);
                for (int i = 0; i < Cells; i++)
                {
                    bool on = i < lit;
                    var cell = _cells[i];
                    cell.Fill = on ? _lit : _dim;
                    if (on && i >= from)
                    {
                        cell.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.1, 1, TimeSpan.FromMilliseconds(220))
                        {
                            BeginTime = TimeSpan.FromMilliseconds((i - from) * 55),
                            FillBehavior = FillBehavior.Stop
                        });
                    }
                }
                _litCount = lit;
            }
            UpdateLoop(b.Has && b.Charging);
        }

        protected override void SetChargingLoop(bool on)
        {
            if (on) _lit.BeginAnimation(Brush.OpacityProperty, Forever(1, 0.5, 1.1));
            else _lit.BeginAnimation(Brush.OpacityProperty, null);
        }
    }

    /// <summary>Dot-matrix battery: a 3×5 dot-font number above a ten-column dot bar.</summary>
    private sealed class BatteryDotsTile : BatteryTileBase
    {
        private static readonly string[][] Font =
        [
            ["111", "101", "101", "101", "111"],
            ["010", "110", "010", "010", "111"],
            ["111", "001", "111", "100", "111"],
            ["111", "001", "111", "001", "111"],
            ["101", "101", "111", "001", "001"],
            ["111", "100", "111", "001", "111"],
            ["111", "100", "111", "101", "111"],
            ["111", "001", "001", "001", "001"],
            ["111", "101", "111", "101", "111"],
            ["111", "101", "111", "001", "111"]
        ];

        private const int DigitCols = 11; // three 3-wide digits with one-column gaps
        private const int BarCols = 10;
        private readonly Ellipse[] _digits = new Ellipse[DigitCols * 5];
        private readonly Ellipse[] _bar = new Ellipse[BarCols];
        private readonly bool[] _digitOn = new bool[DigitCols * 5];
        private readonly bool[] _barOn = new bool[BarCols];
        private Canvas _canvas = null!;
        private TextBlock _ac = null!;
        private SolidColorBrush _barBrush = null!;
        private Brush _digitBrush = null!;
        private Brush _dim = null!;

        public BatteryDotsTile(DockWidgetDescriptor d, DockWidgetContext c) : base(d, c) { }

        protected override Thickness ContentPadding(double h) => new(3, 2, 3, 2);

        protected override FrameworkElement BuildContent(DockWidgetContext ctx)
        {
            var p = ctx.Palette;
            double h = ctx.TileHeight;
            double chipW = Math.Round(h * Descriptor.WidthUnits + (Descriptor.WidthUnits > 1 ? (Descriptor.WidthUnits - 1) * 6 : 0));
            double availW = chipW - 6, availH = h - 4;
            double pitch = Math.Min(availH / 7.0, availW / DigitCols);
            double dot = Math.Max(1.4, pitch * 0.72);
            _digitBrush = p.Foreground;
            _dim = W.Brush(ColorOf(p.Track, Colors.Gray));
            _barBrush = new SolidColorBrush(p.Positive);
            _canvas = new Canvas { Width = DigitCols * pitch, Height = 7 * pitch, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            for (int r = 0; r < 5; r++)
            {
                for (int c = 0; c < DigitCols; c++)
                {
                    var e = new Ellipse { Width = dot, Height = dot, Fill = _dim };
                    Canvas.SetLeft(e, c * pitch + (pitch - dot) / 2);
                    Canvas.SetTop(e, r * pitch + (pitch - dot) / 2);
                    _canvas.Children.Add(e);
                    _digits[r * DigitCols + c] = e;
                }
            }
            double barLeft = (DigitCols - BarCols) * pitch / 2;
            for (int c = 0; c < BarCols; c++)
            {
                var e = new Ellipse { Width = dot, Height = dot, Fill = _dim };
                Canvas.SetLeft(e, barLeft + c * pitch + (pitch - dot) / 2);
                Canvas.SetTop(e, 6 * pitch + (pitch - dot) / 2);
                _canvas.Children.Add(e);
                _bar[c] = e;
            }
            _ac = W.Text(p, "AC", Fs(h, 0.3, 9), p.SecondaryForeground, FontWeights.SemiBold);
            _ac.HorizontalAlignment = HorizontalAlignment.Center;
            _ac.Visibility = Visibility.Collapsed;
            var g = new Grid();
            g.Children.Add(_canvas);
            g.Children.Add(_ac);
            return g;
        }

        protected override void Refresh(DockWidgetContext ctx)
        {
            var b = ReadBattery(ctx);
            if (!b.Has)
            {
                _canvas.Visibility = Visibility.Collapsed;
                _ac.Visibility = Visibility.Visible;
                UpdateLoop(false);
                return;
            }
            _canvas.Visibility = Visibility.Visible;
            _ac.Visibility = Visibility.Collapsed;
            var color = BatteryColor(ctx.Palette, b);
            if (_barBrush.Color != color) _barBrush.Color = color;

            string num = b.Pct.ToString(CultureInfo.InvariantCulture).PadLeft(3);
            var digitTarget = new bool[DigitCols * 5];
            for (int d = 0; d < 3; d++)
            {
                if (num[d] == ' ') continue;
                var glyph = Font[num[d] - '0'];
                for (int r = 0; r < 5; r++)
                    for (int c = 0; c < 3; c++)
                        if (glyph[r][c] == '1') digitTarget[r * DigitCols + d * 4 + c] = true;
            }
            int barLit = DockWidgetMath.LitCells(b.Pct, BarCols);

            int order = 0;
            // Column-major so the number "types" in from left to right.
            for (int c = 0; c < DigitCols; c++)
                for (int r = 0; r < 5; r++)
                {
                    int i = r * DigitCols + c;
                    Apply(_digits[i], ref _digitOn[i], digitTarget[i], _digitBrush, ref order);
                }
            for (int c = 0; c < BarCols; c++) Apply(_bar[c], ref _barOn[c], c < barLit, _barBrush, ref order);
            UpdateLoop(b.Charging);
        }

        private void Apply(Ellipse e, ref bool state, bool on, Brush litBrush, ref int order)
        {
            if (state == on) return;
            state = on;
            e.Fill = on ? litBrush : _dim;
            if (!on) return;
            e.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160))
            {
                BeginTime = TimeSpan.FromMilliseconds(order++ * 14),
                FillBehavior = FillBehavior.Stop
            });
            e.Opacity = 1;
        }

        protected override void SetChargingLoop(bool on)
        {
            if (on) _barBrush.BeginAnimation(Brush.OpacityProperty, Forever(1, 0.45, 0.9));
            else _barBrush.BeginAnimation(Brush.OpacityProperty, null);
        }
    }

    /// <summary>Car fuel gauge, E to F, with a springy needle.</summary>
    private sealed class BatteryGaugeTile : BatteryTileBase
    {
        private RotateTransform _needle = null!;
        private TextBlock _bolt = null!;
        private Line _needleLine = null!;
        private readonly CachedBrush _needleBrush = new();
        private double _target = double.NaN;

        public BatteryGaugeTile(DockWidgetDescriptor d, DockWidgetContext c) : base(d, c) { }

        protected override Thickness ContentPadding(double h) => new(2);

        protected override FrameworkElement BuildContent(DockWidgetContext ctx)
        {
            var p = ctx.Palette;
            double h = ctx.TileHeight;
            double chipW = Math.Round(h * Descriptor.WidthUnits + (Descriptor.WidthUnits > 1 ? (Descriptor.WidthUnits - 1) * 6 : 0));
            double w = chipW - 4, hh = h - 4;
            double thick = Math.Max(1.8, h * 0.07);
            double r = Math.Min(w / 2 - 3, hh - 4);
            var c = new Point(w / 2, hh / 2 + r / 2);
            var canvas = new Canvas { Width = w, Height = hh, ClipToBounds = false };

            canvas.Children.Add(new System.Windows.Shapes.Path
            {
                Data = RingGauge.Arc(c, r, 270, 180),
                Stroke = p.Track,
                StrokeThickness = thick,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            });
            canvas.Children.Add(new System.Windows.Shapes.Path
            {
                Data = RingGauge.Arc(c, r, 270, 30),
                Stroke = W.Brush(p.Negative, 0xD0),
                StrokeThickness = thick,
                StrokeStartLineCap = PenLineCap.Round
            });
            var tickBrush = p.SecondaryForeground;
            for (int i = 0; i <= 4; i++)
            {
                double a = 270 + i * 45;
                var p1 = RingGauge.PointOn(c, r - thick, a);
                var p2 = RingGauge.PointOn(c, r - thick - Math.Max(2, h * 0.08), a);
                canvas.Children.Add(new Line { X1 = p1.X, Y1 = p1.Y, X2 = p2.X, Y2 = p2.Y, Stroke = tickBrush, StrokeThickness = i % 2 == 0 ? 1.2 : 0.8 });
            }
            double fs = Fs(h, 0.19, 6);
            var e = W.Text(p, "E", fs, p.SecondaryForeground, FontWeights.SemiBold);
            var f = W.Text(p, "F", fs, p.SecondaryForeground, FontWeights.SemiBold);
            Canvas.SetLeft(e, c.X - r - fs * 0.3);
            Canvas.SetTop(e, c.Y - fs * 0.2);
            Canvas.SetLeft(f, c.X + r - fs * 0.35);
            Canvas.SetTop(f, c.Y - fs * 0.2);
            canvas.Children.Add(e);
            canvas.Children.Add(f);

            _bolt = W.Glyph(p, BoltGlyph, Math.Max(6, h * 0.2), W.Brush(p.Positive));
            Canvas.SetLeft(_bolt, c.X - h * 0.1);
            Canvas.SetTop(_bolt, c.Y - r * 0.62);
            _bolt.Visibility = Visibility.Collapsed;
            canvas.Children.Add(_bolt);

            _needle = new RotateTransform(-90, c.X, c.Y);
            _needleLine = new Line
            {
                X1 = c.X, Y1 = c.Y, X2 = c.X, Y2 = c.Y - (r - thick * 0.5),
                Stroke = W.Brush(p.Negative),
                StrokeThickness = Math.Max(1.4, h * 0.05),
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                RenderTransform = _needle
            };
            canvas.Children.Add(_needleLine);
            double hub = Math.Max(3, h * 0.12);
            var hubDot = new Ellipse { Width = hub, Height = hub, Fill = p.Foreground };
            Canvas.SetLeft(hubDot, c.X - hub / 2);
            Canvas.SetTop(hubDot, c.Y - hub / 2);
            canvas.Children.Add(hubDot);

            var host = new Grid { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            host.Children.Add(canvas);
            return host;
        }

        protected override void Refresh(DockWidgetContext ctx)
        {
            var b = ReadBattery(ctx);
            double pct = b.Has ? b.Pct : 100;
            double angle = -90 + pct * 1.8;
            _needleLine.Stroke = _needleBrush.Get(b.Has && b.Pct <= 15 && !b.Charging ? ctx.Palette.Negative : ctx.Palette.Accent);
            _bolt.Visibility = b.Has && b.Charging ? Visibility.Visible : Visibility.Collapsed;
            if (double.IsNaN(_target) || Math.Abs(_target - angle) > 0.3)
            {
                _target = angle;
                _needle.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(angle, TimeSpan.FromMilliseconds(1100))
                {
                    EasingFunction = Motion.Soft
                }, HandoffBehavior.SnapshotAndReplace);
            }
            UpdateLoop(b.Has && b.Charging);
        }

        protected override void SetChargingLoop(bool on)
        {
            if (on) _bolt.BeginAnimation(UIElement.OpacityProperty, Forever(1, 0.35, 0.9));
            else _bolt.BeginAnimation(UIElement.OpacityProperty, null);
        }
    }

    // =============================================================================================
    // Quick toggles
    // =============================================================================================

    /// <summary>Wi-Fi, Bluetooth, hotspot and airplane-mode round buttons with cross-fading state.</summary>
    private sealed class QuickTogglesTile : DockWidgetTileBase
    {
        private sealed class Toggle(Border button, SolidColorBrush background, SolidColorBrush outline, SolidColorBrush glyph)
        {
            public Border Button { get; } = button;
            public SolidColorBrush Background { get; } = background;
            public SolidColorBrush Outline { get; } = outline;
            public SolidColorBrush GlyphBrush { get; } = glyph;
            public bool? On { get; set; }
        }

        private readonly Toggle[] _toggles = new Toggle[2];
        private DockWidgetContext _ctx = null!;
        private RowsPanel? _cardRows;
        private bool _auditWifi = true, _auditBt = true;

        public QuickTogglesTile(DockWidgetDescriptor d, DockWidgetContext c) : base(d, c) { }

        protected override FrameworkElement BuildContent(DockWidgetContext ctx)
        {
            _ctx = ctx;
            var p = ctx.Palette;
            double h = ctx.TileHeight;
            double pillH = Math.Round(h * 0.74);
            double gap = Math.Max(4, Math.Round(h * 0.16));
            string[] glyphs = ["\uE701", "\uE702"];
            string[] tips = ["Wi-Fi", "Bluetooth"];
            var g = new Grid { VerticalAlignment = VerticalAlignment.Center };
            for (int i = 0; i < 2; i++)
            {
                int idx = i;
                g.ColumnDefinitions.Add(new ColumnDefinition());
                var bg = new SolidColorBrush(Colors.Transparent);
                var outline = new SolidColorBrush(ColorOf(p.TertiaryForeground, Colors.White));
                var fg = new SolidColorBrush(ColorOf(p.Foreground, Colors.White));
                var glyph = W.Glyph(p, glyphs[i], Math.Round(pillH * 0.48), fg);
                var chevron = W.Glyph(p, "\uE70D", Math.Max(6, Math.Round(pillH * 0.3)), fg);
                chevron.Opacity = 0.7;
                var content = new DockPanel { LastChildFill = false, Margin = new Thickness(Math.Round(pillH * 0.34), 0, Math.Round(pillH * 0.28), 0) };
                DockPanel.SetDock(glyph, Dock.Left);
                DockPanel.SetDock(chevron, Dock.Right);
                content.Children.Add(glyph);
                content.Children.Add(chevron);
                var b = new Border
                {
                    Height = pillH,
                    CornerRadius = new CornerRadius(pillH / 2),
                    Background = bg,
                    BorderBrush = outline,
                    BorderThickness = new Thickness(1.1),
                    Child = content,
                    Cursor = Cursors.Hand,
                    ToolTip = tips[i],
                    Margin = new Thickness(i == 0 ? 0 : gap / 2, 0, i == 0 ? gap / 2 : 0, 0)
                };
                System.Windows.Automation.AutomationProperties.SetName(b, tips[i]);
                b.MouseLeftButtonDown += (_, e) => e.Handled = true;
                b.MouseLeftButtonUp += (_, e) =>
                {
                    e.Handled = true;
                    W.Pulse(b, 0.92);
                    OnToggle(idx);
                };
                Grid.SetColumn(b, i);
                g.Children.Add(b);
                _toggles[i] = new Toggle(b, bg, outline, fg);
            }
            return g;
        }

        private void OnToggle(int idx)
        {
            var ctx = _ctx;
            if (ctx.IsAuditMode)
            {
                if (idx == 0) _auditWifi = !_auditWifi;
                else if (idx == 1) _auditBt = !_auditBt;
                Refresh(ctx);
                return;
            }
            switch (idx)
            {
                case 0: Fire(() => ctx.Orch.Connectivity.ToggleWifiAsync(), "Wi-Fi toggle failed."); break;
                case 1: Fire(() => ctx.Orch.Connectivity.ToggleBluetoothAsync(), "Bluetooth toggle failed."); break;
                case 2: DockWidgetActions.OpenSettings(ctx, "ms-settings:network-mobilehotspot"); break;
                default: DockWidgetActions.OpenSettings(ctx, "ms-settings:network-airplanemode"); break;
            }
        }

        protected override void Refresh(DockWidgetContext ctx)
        {
            _ctx = ctx;
            var c = ctx.Orch.Connectivity.Current;
            bool wifi = ctx.IsAuditMode ? _auditWifi : c.IsWifiOn;
            bool bt = ctx.IsAuditMode ? _auditBt : c.IsBluetoothOn;
            SetState(ctx, _toggles[0], wifi, ctx.IsAuditMode || c.IsWifiAvailable);
            SetState(ctx, _toggles[1], bt, ctx.IsAuditMode || c.IsBluetoothAvailable);
        }

        private static void SetState(DockWidgetContext ctx, Toggle t, bool on, bool available)
        {
            t.Button.Opacity = available ? 1 : 0.45;
            if (t.On == on) return;
            bool first = t.On is null;
            t.On = on;
            var p = ctx.Palette;
            // On: accent outline with a soft accent wash; off: a quiet neutral outline.
            var bg = on ? Color.FromArgb(0x30, p.Accent.R, p.Accent.G, p.Accent.B) : Color.FromArgb(0, p.Accent.R, p.Accent.G, p.Accent.B);
            var outline = on ? p.Accent : ColorOf(p.TertiaryForeground, Colors.White);
            var fg = on ? (p.IsLight ? p.Accent : Colors.White) : ColorOf(p.SecondaryForeground, Colors.White);
            if (first)
            {
                t.Background.Color = bg;
                t.Outline.Color = outline;
                t.GlyphBrush.Color = fg;
            }
            else
            {
                ColorTo(t.Background, bg, 280);
                ColorTo(t.Outline, outline, 280);
                ColorTo(t.GlyphBrush, fg, 280);
            }
        }

        protected override FrameworkElement? CreateCard(DockWidgetContext ctx)
        {
            var p = ctx.Palette;
            _cardRows = new RowsPanel();
            var buttons = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            buttons.Children.Add(W.Button(p, ctx.T("Mobil etkin nokta", "Mobile hotspot"), () => DockWidgetActions.OpenSettings(ctx, "ms-settings:network-mobilehotspot"), "\uE88A"));
            buttons.Children.Add(W.Button(p, ctx.T("Uçak modu", "Airplane mode"), () => DockWidgetActions.OpenSettings(ctx, "ms-settings:network-airplanemode"), "\uE709"));
            return W.Card(p, Descriptor.Glyph, Descriptor.Name(ctx.Lang), W.Column(_cardRows.Panel, buttons), 280,
                W.Button(p, "", () => DockWidgetActions.OpenSettings(ctx, "ms-settings:network"), ""));
        }

        protected override void RefreshCard(DockWidgetContext ctx)
        {
            if (_cardRows is null) return;
            var c = ctx.Orch.Connectivity.Current;
            string on = ctx.T("Açık", "On"), off = ctx.T("Kapalı", "Off");
            bool wifi = ctx.IsAuditMode ? _auditWifi : c.IsWifiOn;
            bool bt = ctx.IsAuditMode ? _auditBt : c.IsBluetoothOn;
            string ssid = !string.IsNullOrWhiteSpace(c.WifiSsid) ? c.WifiSsid! : ctx.IsAuditMode ? "Ev-5G" : "—";
            List<string> devices = c.PairedBluetoothDevices.IsDefault ? [] : c.PairedBluetoothDevices.Where(x => x.IsConnected).Select(x => x.Name).ToList();
            var rows = new List<(string, string)>
            {
                ("Wi-Fi", wifi ? ssid : off),
                (ctx.T("Sinyal", "Signal"), c.WifiSignalPercent is int s ? Pct(ctx, s) : "—"),
                ("Ethernet", c.IsEthernetConnected ? (c.EthernetName ?? on) : off),
                ("Bluetooth", bt ? on : off),
                (ctx.T("Bağlı cihazlar", "Connected devices"), devices.Count == 0 ? "—" : string.Join(", ", devices))
            };
            _cardRows.Set(ctx.Palette, rows);
        }
    }

    // =============================================================================================
    // Brightness
    // =============================================================================================

    /// <summary>Sun glyph and a draggable bar that sets the first display's brightness.</summary>
    private sealed class BrightnessTile : DockWidgetTileBase
    {
        private TextBlock _sun = null!;
        private ScaleTransform _sunScale = null!;
        private BarGauge _bar = null!;
        private Border _host = null!;
        private TextBlock _pct = null!;
        private DockWidgetContext _ctx = null!;
        private RowsPanel? _cardRows;
        private bool _dragging;
        private int _value = -1;
        private int _written = -1;
        private long _lastWriteTicks;
        private string? _deviceId;

        public BrightnessTile(DockWidgetDescriptor d, DockWidgetContext c) : base(d, c) { }

        protected override FrameworkElement BuildContent(DockWidgetContext ctx)
        {
            _ctx = ctx;
            var p = ctx.Palette;
            double h = ctx.TileHeight;
            _sunScale = new ScaleTransform(1, 1);
            _sun = W.Glyph(p, "", h * 0.42, p.Foreground);
            _sun.RenderTransformOrigin = new Point(0.5, 0.5);
            _sun.RenderTransform = _sunScale;
            _sun.Width = h * 0.5;

            _bar = new BarGauge { Height = Math.Max(4, h * 0.2), Track = p.Track, Fill = p.AccentBrush, VerticalAlignment = VerticalAlignment.Center };
            _host = new Border
            {
                Background = Brushes.Transparent,
                Child = _bar,
                Margin = new Thickness(Math.Round(h * 0.12), 0, Math.Round(h * 0.12), 0),
                Cursor = Cursors.SizeWE
            };
            _host.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                if (!CanWrite()) return;
                _dragging = true;
                _host.CaptureMouse();
                ApplyFromMouse(e.GetPosition(_bar).X);
            };
            _host.MouseMove += (_, e) =>
            {
                if (_dragging) ApplyFromMouse(e.GetPosition(_bar).X);
            };
            _host.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                if (!_dragging) return;
                _dragging = false;
                _host.ReleaseMouseCapture();
                Write(force: true);
            };
            _host.LostMouseCapture += (_, _) =>
            {
                if (_dragging) { _dragging = false; Write(force: true); }
            };

            _pct = W.Text(p, "", Fs(h, 0.25, 7.5), weight: FontWeights.SemiBold);
            _pct.TextTrimming = TextTrimming.None;
            _pct.MinWidth = h * 0.6;
            _pct.TextAlignment = TextAlignment.Right;

            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(_sun);
            Grid.SetColumn(_host, 1);
            g.Children.Add(_host);
            Grid.SetColumn(_pct, 2);
            g.Children.Add(_pct);
            return g;
        }

        private bool CanWrite() => _ctx.IsAuditMode || _deviceId is not null;

        private void ApplyFromMouse(double x)
        {
            double w = _bar.ActualWidth;
            if (w <= 1) return;
            int pct = (int)Math.Round(Math.Clamp(x / w, 0, 1) * 100);
            Show(pct, 110);
            Write(force: false);
        }

        private void Write(bool force)
        {
            if (_value < 0 || _value == _written) return;
            long now = Environment.TickCount64;
            if (!force && now - _lastWriteTicks < 90) return;
            _lastWriteTicks = now;
            _written = _value;
            if (_ctx.IsAuditMode || _deviceId is null) return;
            try
            {
                _ctx.Orch.Brightness.SetHardwareBrightness(_deviceId, _value);
            }
            catch (Exception ex)
            {
                Services.CrashLogger.Log("DockWidget", ex, "Brightness write failed.");
            }
        }

        private void Show(int pct, double ms)
        {
            if (pct == _value) return;
            _value = pct;
            _bar.AnimateTo(pct / 100.0, ms);
            double s = 0.78 + 0.32 * pct / 100.0;
            var anim = new DoubleAnimation(s, TimeSpan.FromMilliseconds(220)) { EasingFunction = Motion.Soft };
            _sunScale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
            _sunScale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
            _pct.Text = Pct(_ctx, pct);
        }

        private IReadOnlyList<DisplayBrightnessInfo> Displays()
        {
            try { return _ctx.Orch.Brightness.GetDisplays(); }
            catch (Exception) { return []; }
        }

        protected override void Refresh(DockWidgetContext ctx)
        {
            _ctx = ctx;
            if (_dragging || Environment.TickCount64 - _lastWriteTicks < 1500) return;
            IReadOnlyList<DisplayBrightnessInfo> displays = ctx.IsAuditMode ? [] : Displays();
            if (displays.Count > 0)
            {
                _deviceId = displays[0].DeviceId;
                _host.Opacity = 1;
                int v = Math.Clamp(displays[0].HardwareBrightness01To100, 0, 100);
                _written = v;
                Show(v, 480);
            }
            else if (ctx.IsAuditMode)
            {
                if (_value < 0) Show(70, 480);
            }
            else
            {
                _deviceId = null;
                _host.Opacity = 0.45;
                _value = -1;
                _bar.AnimateTo(0);
                _pct.Text = "—";
            }
        }

        protected override FrameworkElement? CreateCard(DockWidgetContext ctx)
        {
            var p = ctx.Palette;
            _cardRows = new RowsPanel();
            return W.Card(p, Descriptor.Glyph, Descriptor.Name(ctx.Lang), _cardRows.Panel, 280,
                W.Button(p, "", () => DockWidgetActions.OpenSettings(ctx, "ms-settings:display"), ""));
        }

        protected override void RefreshCard(DockWidgetContext ctx)
        {
            if (_cardRows is null) return;
            var rows = new List<(string, string)>();
            IReadOnlyList<DisplayBrightnessInfo> displays = ctx.IsAuditMode ? [] : Displays();
            foreach (var d in displays)
            {
                string kind = d.IsInternal ? ctx.T("dahili", "internal") : d.SupportsDdcCi ? "DDC/CI" : ctx.T("desteklenmiyor", "unsupported");
                rows.Add((string.IsNullOrWhiteSpace(d.FriendlyName) ? ctx.T("Ekran", "Display") : d.FriendlyName, $"{Pct(ctx, d.HardwareBrightness01To100)} · {kind}"));
            }
            if (rows.Count == 0)
                rows.Add(ctx.IsAuditMode ? (ctx.T("Dahili ekran", "Built-in display"), Pct(ctx, _value)) : (ctx.T("Ekran", "Display"), ctx.T("Parlaklık denetimi yok", "No brightness control")));
            _cardRows.Set(ctx.Palette, rows);
        }
    }

    // =============================================================================================
    // Theme / DND / notifications / single-glyph actions
    // =============================================================================================

    /// <summary>Moon/sun glyph for the Windows app theme; rotates and cross-fades when it changes.</summary>
    private sealed class ThemeToggleTile : DockWidgetTileBase
    {
        private Grid _wrap = null!;
        private RotateTransform _rot = null!;
        private TextBlock _moon = null!;
        private TextBlock _sun = null!;
        private bool? _dark;
        private bool _auditDark = true;
        private double _angle;

        public ThemeToggleTile(DockWidgetDescriptor d, DockWidgetContext c) : base(d, c) { }

        protected override FrameworkElement BuildContent(DockWidgetContext ctx)
        {
            var p = ctx.Palette;
            double h = ctx.TileHeight;
            _moon = W.Glyph(p, "", h * 0.46, p.Foreground);
            _sun = W.Glyph(p, "", h * 0.5, W.Brush(p.Warning));
            _rot = new RotateTransform();
            var spin = new Grid { RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = _rot };
            spin.Children.Add(_sun);
            spin.Children.Add(_moon);
            _wrap = new Grid();
            _wrap.Children.Add(spin);
            return _wrap;
        }

        protected override void Refresh(DockWidgetContext ctx)
        {
            bool dark = ctx.IsAuditMode ? _auditDark : ctx.Orch.Connectivity.Current.IsDarkTheme;
            Chip.ToolTip = dark ? ctx.T("Koyu tema · tıkla: açık", "Dark theme · click for light") : ctx.T("Açık tema · tıkla: koyu", "Light theme · click for dark");
            if (_dark == dark) return;
            bool first = _dark is null;
            _dark = dark;
            if (first)
            {
                _moon.Opacity = dark ? 1 : 0;
                _sun.Opacity = dark ? 0 : 1;
                return;
            }
            _angle += 180;
            var ease = Motion.Gentle;
            _rot.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(_angle, TimeSpan.FromMilliseconds(520)) { EasingFunction = ease });
            _moon.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(dark ? 1 : 0, TimeSpan.FromMilliseconds(380)));
            _sun.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(dark ? 0 : 1, TimeSpan.FromMilliseconds(380)));
        }

        public override bool OnPrimaryClick(DockWidgetContext ctx)
        {
            W.Pulse(_wrap);
            if (ctx.IsAuditMode)
            {
                _auditDark = !_auditDark;
                Refresh(ctx);
            }
            else
            {
                Fire(() => ctx.Orch.Connectivity.ToggleDarkThemeAsync(), "Theme toggle failed.");
            }
            return true;
        }
    }

    /// <summary>Do Not Disturb indicator (Windows focus profile); glows while quiet, opens settings on click.</summary>
    private sealed class DndTile : DockWidgetTileBase
    {
        private Grid _wrap = null!;
        private TextBlock _moon = null!;
        private DropShadowEffect _glow = null!;
        private bool? _on;
        private bool _attached;
        private bool _pulsing;

        public DndTile(DockWidgetDescriptor d, DockWidgetContext c) : base(d, c) { }

        protected override FrameworkElement BuildContent(DockWidgetContext ctx)
        {
            var p = ctx.Palette;
            double h = ctx.TileHeight;
            _glow = new DropShadowEffect { ShadowDepth = 0, BlurRadius = Math.Max(6, h * 0.4), Color = p.Accent, Opacity = 0 };
            _moon = W.Glyph(p, "", h * 0.48, p.TertiaryForeground);
            _moon.Effect = _glow;
            _wrap = new Grid();
            _wrap.Children.Add(_moon);
            return _wrap;
        }

        protected override void Refresh(DockWidgetContext ctx)
        {
            var profile = ctx.IsAuditMode ? FocusQuietProfile.PriorityOnly : ctx.Orch.Connectivity.Current.WindowsFocusProfile;
            bool on = profile is FocusQuietProfile.PriorityOnly or FocusQuietProfile.AlarmsOnly;
            if (_on != on)
            {
                _on = on;
                _moon.Foreground = on ? ctx.Palette.AccentBrush : ctx.Palette.TertiaryForeground;
                Chip.ToolTip = profile switch
                {
                    FocusQuietProfile.PriorityOnly => ctx.T("Rahatsız etme: yalnızca öncelikli", "Do not disturb: priority only"),
                    FocusQuietProfile.AlarmsOnly => ctx.T("Rahatsız etme: yalnızca alarmlar", "Do not disturb: alarms only"),
                    _ => ctx.T("Rahatsız etme kapalı · tıkla: ayarlar", "Do not disturb off · click for settings")
                };
            }
            UpdatePulse();
        }

        private void UpdatePulse()
        {
            bool want = _attached && _on == true;
            if (want == _pulsing) return;
            _pulsing = want;
            if (want) _glow.BeginAnimation(DropShadowEffect.OpacityProperty, Forever(0.25, 0.95, 1.4));
            else
            {
                _glow.BeginAnimation(DropShadowEffect.OpacityProperty, null);
                _glow.Opacity = 0;
            }
        }

        public override void OnAttached(DockWidgetContext ctx)
        {
            _attached = true;
            UpdatePulse();
        }

        public override void OnDetached()
        {
            _attached = false;
            UpdatePulse();
        }

        public override bool OnPrimaryClick(DockWidgetContext ctx)
        {
            W.Pulse(_wrap);
            DockWidgetActions.OpenSettings(ctx, "ms-settings:notifications");
            return true;
        }
    }

    /// <summary>Bell with a count badge; shakes when new notifications arrive. Click = Win+N.</summary>
    private sealed class NotificationsTile : DockWidgetTileBase
    {
        private Grid _wrap = null!;
        private RotateTransform _shake = null!;
        private Border _badge = null!;
        private TextBlock _count = null!;
        private int _last = -1;

        public NotificationsTile(DockWidgetDescriptor d, DockWidgetContext c) : base(d, c) { }

        protected override FrameworkElement BuildContent(DockWidgetContext ctx)
        {
            var p = ctx.Palette;
            double h = ctx.TileHeight;
            _shake = new RotateTransform();
            var bell = W.Glyph(p, "", h * 0.46, p.Foreground);
            bell.RenderTransformOrigin = new Point(0.5, 0.12);
            bell.RenderTransform = _shake;
            double bs = Math.Max(9, h * 0.36);
            _count = W.Text(p, "", Math.Max(6, bs * 0.66), Brushes.White, FontWeights.Bold);
            _count.HorizontalAlignment = HorizontalAlignment.Center;
            _count.TextTrimming = TextTrimming.None;
            _badge = new Border
            {
                MinWidth = bs,
                Height = bs,
                CornerRadius = new CornerRadius(bs / 2),
                Background = W.Brush(p.Negative),
                Padding = new Thickness(2, 0, 2, 0),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 1, 1, 0),
                Child = _count,
                Visibility = Visibility.Collapsed
            };
            _wrap = new Grid();
            _wrap.Children.Add(bell);
            _wrap.Children.Add(_badge);
            return _wrap;
        }

        protected override void Refresh(DockWidgetContext ctx)
        {
            int n = ctx.Orch.Notifications.Current.Count;
            if (ctx.IsAuditMode && n == 0) n = 3;
            if (n == _last) return;
            bool increased = _last >= 0 && n > _last;
            bool appeared = _last <= 0 && n > 0;
            _last = n;
            _count.Text = n > 99 ? "99+" : n.ToString(CultureInfo.InvariantCulture);
            _badge.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
            Chip.ToolTip = n > 0 ? ctx.T($"{n} bildirim", $"{n} notifications") : ctx.T("Bildirim yok", "No notifications");
            if (increased || appeared) W.PopIn(_badge);
            if (increased) Shake();
        }

        private void Shake()
        {
            var kf = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(700) };
            double[] angles = [0, -18, 15, -11, 7, -3, 0];
            for (int i = 0; i < angles.Length; i++)
                kf.KeyFrames.Add(new EasingDoubleKeyFrame(angles[i], KeyTime.FromPercent(i / (double)(angles.Length - 1)), Motion.Soft));
            _shake.BeginAnimation(RotateTransform.AngleProperty, kf);
        }

        public override bool OnPrimaryClick(DockWidgetContext ctx)
        {
            W.Pulse(_wrap);
            DockWidgetActions.WinPlus(ctx, 'N');
            return true;
        }
    }

    /// <summary>A single glyph that runs an action on click (Show Desktop, on-screen keyboard).</summary>
    private sealed class GlyphActionTile : DockWidgetTileBase
    {
        private readonly Action<DockWidgetContext> _action;
        private TextBlock _glyph = null!;

        public GlyphActionTile(DockWidgetDescriptor d, DockWidgetContext c, string glyph, Action<DockWidgetContext> action) : base(d, c)
        {
            _action = action;
            _glyph.Text = glyph;
        }

        protected override FrameworkElement BuildContent(DockWidgetContext ctx)
        {
            _glyph = W.Glyph(ctx.Palette, Descriptor.Glyph, ctx.TileHeight * 0.46, ctx.Palette.Foreground);
            Chip.ToolTip = Descriptor.Name(ctx.Lang);
            return new Grid { Children = { _glyph } };
        }

        protected override void Refresh(DockWidgetContext ctx) { }

        public override bool OnPrimaryClick(DockWidgetContext ctx)
        {
            W.Pulse(_glyph, 0.82);
            _action(ctx);
            return true;
        }
    }

    // =============================================================================================
    // Progress (day / month / year)
    // =============================================================================================

    /// <summary>Month or day progress: label, percentage and an animated bar.</summary>
    private sealed class ProgressBarTile : DockWidgetTileBase
    {
        private TextBlock _label = null!;
        private TextBlock _pct = null!;
        private BarGauge _bar = null!;
        private RowsPanel? _cardRows;
        private bool _first = true;

        public ProgressBarTile(DockWidgetDescriptor d, DockWidgetContext c) : base(d, c) { }

        private bool IsMonth => Descriptor.Id == "month-progress";

        protected override FrameworkElement BuildContent(DockWidgetContext ctx)
        {
            var p = ctx.Palette;
            double h = ctx.TileHeight;
            double fs = Fs(h, 0.25, 7.5);
            _label = W.Text(p, "", fs, p.SecondaryForeground, FontWeights.SemiBold);
            _pct = W.Text(p, "", fs, weight: FontWeights.SemiBold);
            _pct.TextTrimming = TextTrimming.None;
            var top = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(_pct, Dock.Right);
            top.Children.Add(_pct);
            top.Children.Add(_label);
            _bar = new BarGauge
            {
                Height = Math.Max(3, Math.Round(h * 0.17)),
                Track = p.Track,
                Fill = W.Brush(IsMonth ? p.Accent : p.Warning),
                Margin = new Thickness(0, Math.Max(2, h * 0.08), 0, 0)
            };
            var col = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            col.Children.Add(top);
            col.Children.Add(_bar);
            return col;
        }

        private static CultureInfo Culture(DockWidgetContext ctx) => CultureInfo.GetCultureInfo(ctx.En ? "en-US" : "tr-TR");

        protected override void Refresh(DockWidgetContext ctx)
        {
            var now = ctx.Now;
            double v = IsMonth ? DockWidgetMath.MonthProgress(now) : DockWidgetMath.DayProgress(now);
            string label = IsMonth ? Culture(ctx).DateTimeFormat.GetMonthName(now.Month) : ctx.T("Bugün", "Today");
            if (_label.Text != label) _label.Text = label;
            W.SetTextAnimated(_pct, Pct(ctx, v * 100));
            _bar.AnimateTo(v, _first ? 1100 : 480);
            _first = false;
        }

        protected override FrameworkElement? CreateCard(DockWidgetContext ctx)
        {
            _cardRows = new RowsPanel();
            return W.Card(ctx.Palette, Descriptor.Glyph, Descriptor.Name(ctx.Lang), _cardRows.Panel, 260);
        }

        protected override void RefreshCard(DockWidgetContext ctx)
        {
            if (_cardRows is null) return;
            var now = ctx.Now;
            var rows = new List<(string, string)>();
            if (IsMonth)
            {
                int days = DateTime.DaysInMonth(now.Year, now.Month);
                var end = new DateTime(now.Year, now.Month, days);
                rows.Add((ctx.T("Gün", "Day"), $"{now.Day} / {days}"));
                rows.Add((ctx.T("Kalan", "Remaining"), ctx.T($"{days - now.Day} gün", $"{days - now.Day} days")));
                rows.Add((ctx.T("Ay sonu", "Month ends"), end.ToString("d MMMM dddd", Culture(ctx))));
                rows.Add((ctx.T("İlerleme", "Progress"), Pct(ctx, DockWidgetMath.MonthProgress(now) * 100)));
            }
            else
            {
                var elapsed = now.TimeOfDay;
                var left = TimeSpan.FromDays(1) - elapsed;
                rows.Add((ctx.T("Geçen", "Elapsed"), DockWidgetMath.FormatDuration(elapsed, ctx.Lang)));
                rows.Add((ctx.T("Gece yarısına", "Until midnight"), DockWidgetMath.FormatDuration(left, ctx.Lang)));
                rows.Add((ctx.T("İlerleme", "Progress"), Pct(ctx, DockWidgetMath.DayProgress(now) * 100)));
            }
            _cardRows.Set(ctx.Palette, rows);
        }
    }

    /// <summary>The year as 52 week dots (4 × 13) lit up to this week; this week's dot blinks.</summary>
    private sealed class YearProgressTile : DockWidgetTileBase
    {
        private const int Rows = 4, Cols = 13, Dots = Rows * Cols;
        private readonly Ellipse[] _dots = new Ellipse[Dots];
        private TextBlock _year = null!;
        private TextBlock _pct = null!;
        private Brush _lit = null!;
        private Brush _dim = null!;
        private RowsPanel? _cardRows;
        private int _week = -1;
        private int _blinking = -1;
        private bool _attached;

        public YearProgressTile(DockWidgetDescriptor d, DockWidgetContext c) : base(d, c) { }

        protected override FrameworkElement BuildContent(DockWidgetContext ctx)
        {
            var p = ctx.Palette;
            double h = ctx.TileHeight;
            double pitch = Math.Max(4, (h - 4) / Rows);
            double dot = Math.Max(2.2, pitch * 0.64);
            _lit = p.AccentBrush;
            _dim = p.Track;
            var canvas = new Canvas { Width = Cols * pitch, Height = Rows * pitch, VerticalAlignment = VerticalAlignment.Center };
            for (int i = 0; i < Dots; i++)
            {
                int r = i / Cols, c = i % Cols;
                var e = new Ellipse { Width = dot, Height = dot, Fill = _dim };
                Canvas.SetLeft(e, c * pitch + (pitch - dot) / 2);
                Canvas.SetTop(e, r * pitch + (pitch - dot) / 2);
                canvas.Children.Add(e);
                _dots[i] = e;
            }
            _year = W.Text(p, "", Fs(h, 0.22, 7), p.SecondaryForeground);
            _pct = W.Text(p, "", Fs(h, 0.3, 8.5), weight: FontWeights.SemiBold);
            _year.TextTrimming = TextTrimming.None;
            _pct.TextTrimming = TextTrimming.None;
            var texts = W.Column(_year, _pct);
            texts.Margin = new Thickness(Math.Round(h * 0.22), 0, 0, 0);
            var g = new Grid { HorizontalAlignment = HorizontalAlignment.Center };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(canvas);
            Grid.SetColumn(texts, 1);
            g.Children.Add(texts);
            return g;
        }

        private static int WeekIndex(DateTime now) => Math.Min(Dots - 1, (now.DayOfYear - 1) / 7);

        protected override void Refresh(DockWidgetContext ctx)
        {
            var now = ctx.Now;
            W.SetTextAnimated(_year, now.Year.ToString(CultureInfo.InvariantCulture), false);
            W.SetTextAnimated(_pct, Pct(ctx, DockWidgetMath.YearProgress(now) * 100));
            int week = WeekIndex(now);
            if (week != _week)
            {
                _week = week;
                for (int i = 0; i < Dots; i++) _dots[i].Fill = i <= week ? _lit : _dim;
                if (_attached) StartBlink(0);
            }
        }

        private void StartBlink(double delayMs)
        {
            if (_blinking >= 0 && _blinking != _week) _dots[_blinking].BeginAnimation(UIElement.OpacityProperty, null);
            if (_week < 0) return;
            _blinking = _week;
            var anim = Forever(1, 0.3, 0.9);
            anim.BeginTime = TimeSpan.FromMilliseconds(delayMs);
            _dots[_week].BeginAnimation(UIElement.OpacityProperty, anim);
        }

        public override void OnAttached(DockWidgetContext ctx)
        {
            _attached = true;
            if (_week < 0) Refresh(ctx);
            for (int i = 0; i <= _week && i < Dots; i++)
            {
                if (i == _week) continue;
                _dots[i].BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220))
                {
                    BeginTime = TimeSpan.FromMilliseconds(i * 16),
                    FillBehavior = FillBehavior.Stop
                });
            }
            if (_week >= 0) _dots[_week].Opacity = 1;
            StartBlink(Math.Max(0, _week) * 16 + 200);
        }

        public override void OnDetached()
        {
            _attached = false;
            if (_blinking >= 0) _dots[_blinking].BeginAnimation(UIElement.OpacityProperty, null);
            _blinking = -1;
        }

        protected override FrameworkElement? CreateCard(DockWidgetContext ctx)
        {
            _cardRows = new RowsPanel();
            return W.Card(ctx.Palette, Descriptor.Glyph, Descriptor.Name(ctx.Lang), _cardRows.Panel, 260);
        }

        protected override void RefreshCard(DockWidgetContext ctx)
        {
            if (_cardRows is null) return;
            var now = ctx.Now;
            int days = DateTime.IsLeapYear(now.Year) ? 366 : 365;
            var rows = new List<(string, string)>
            {
                (ctx.T("Gün", "Day"), $"{now.DayOfYear} / {days}"),
                (ctx.T("Kalan", "Remaining"), ctx.T($"{days - now.DayOfYear} gün", $"{days - now.DayOfYear} days")),
                (ctx.T("Hafta", "Week"), ISOWeek.GetWeekOfYear(now).ToString(CultureInfo.InvariantCulture)),
                (ctx.T("İlerleme", "Progress"), Pct(ctx, DockWidgetMath.YearProgress(now) * 100))
            };
            _cardRows.Set(ctx.Palette, rows);
        }
    }
}
