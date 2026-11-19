using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using DinamikAda.App.Services;
using DinamikAda.App.Services.DockWidgets;
using DinamikAda.Core.Abstractions;
using DinamikAda.Core.Widgets;
using IOPath = System.IO.Path;

namespace DinamikAda.App.Views.DockWidgets;

/// <summary>Media, apps &amp; files and finance widgets.</summary>
internal static class MediaAppsFinanceWidgetTiles
{
    public static void Register(IDictionary<string, Func<DockWidgetDescriptor, DockWidgetContext, IDockWidgetTile>> map)
    {
        map["now-playing"] = (d, c) => new NowPlayingTile(d, c, NowPlayingLayout.Full);
        map["np-compact"] = (d, c) => new NowPlayingTile(d, c, NowPlayingLayout.Compact);
        map["np-bar"] = (d, c) => new NowPlayingTile(d, c, NowPlayingLayout.Bar);
        map["np-mini"] = (d, c) => new MiniPlayerTile(d, c);
        map["audio-output"] = (d, c) => new AudioOutputTile(d, c);
        map["microphone"] = (d, c) => new MicrophoneTile(d, c);
        map["recent"] = (d, c) => new RecentTile(d, c);
        map["recycle-bin"] = (d, c) => new RecycleBinTile(d, c);
        map["search"] = (d, c) => new SearchTile(d, c);
        map["commands"] = (d, c) => new CommandsTile(d, c);
        map["storage"] = (d, c) => new StorageTile(d, c);
        map["screenshot"] = (d, c) => new ScreenshotTile(d, c);
        map["crypto"] = (d, c) => new QuoteTile(d, c, isCrypto: true);
        map["stock-chart"] = (d, c) => new QuoteTile(d, c, isCrypto: false);
        map["stock-watchlist"] = (d, c) => new WatchlistTile(d, c);
    }

    // =============================================================================================
    // Shared helpers
    // =============================================================================================

    private const string GlyphPlay = "\uE768";
    private const string GlyphPause = "\uE769";
    private const string GlyphPrev = "\uE892";
    private const string GlyphNext = "\uE893";
    private const string GlyphMusic = "\uE8D6";

    private static class Fx
    {
        public static IEasingFunction EaseOut() => Motion.Soft;

        /// <summary>Animates opacity; ms = 0 sets it immediately (and clears any held animation).</summary>
        public static void FadeTo(UIElement el, double to, double ms, double? from = null, double beginMs = 0)
        {
            if (ms <= 0)
            {
                el.BeginAnimation(UIElement.OpacityProperty, null);
                el.Opacity = to;
                return;
            }
            var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms))
            {
                EasingFunction = EaseOut(),
                BeginTime = TimeSpan.FromMilliseconds(beginMs)
            };
            if (from.HasValue) a.From = from.Value;
            el.BeginAnimation(UIElement.OpacityProperty, a, HandoffBehavior.SnapshotAndReplace);
        }

        public static void SetForeground(TextBlock tb, Color c)
        {
            if (tb.Foreground is SolidColorBrush sb && sb.Color == c) return;
            tb.Foreground = W.Brush(c);
        }

        public static void SetFill(BarGauge b, Color c)
        {
            if (b.Fill is SolidColorBrush sb && sb.Color == c) return;
            b.Fill = W.Brush(c);
            b.InvalidateVisual();
        }

        public static void SetFill(RingGauge r, Color c)
        {
            if (r.Fill is SolidColorBrush sb && sb.Color == c) return;
            r.Fill = W.Brush(c);
            r.InvalidateVisual();
        }

        /// <summary>Rows/columns sliding up and fading in one after another.</summary>
        public static void StaggerIn(FrameworkElement el, int index, double stepMs = 45, double dy = 6)
        {
            var tt = new TranslateTransform(0, dy);
            el.RenderTransform = tt;
            el.Opacity = 0;
            var begin = TimeSpan.FromMilliseconds(index * stepMs);
            tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(dy, 0, TimeSpan.FromMilliseconds(320)) { BeginTime = begin, EasingFunction = EaseOut() });
            el.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(280)) { BeginTime = begin, EasingFunction = EaseOut() });
        }

        /// <summary>A coloured overlay that lights up and fades out (price ticks, shutter).</summary>
        public static void Flash(Border overlay, Color c, double peak = 0.32, double ms = 650)
        {
            if (overlay.Background is not SolidColorBrush sb || sb.Color != c) overlay.Background = W.Brush(c);
            overlay.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(peak, 0, TimeSpan.FromMilliseconds(ms)) { EasingFunction = EaseOut() });
        }

        /// <summary>A short lid-like wobble around the element's bottom centre.</summary>
        public static void Wobble(FrameworkElement el)
        {
            if (el.RenderTransform is not RotateTransform rt || rt.IsFrozen)
            {
                rt = new RotateTransform();
                el.RenderTransform = rt;
                el.RenderTransformOrigin = new Point(0.5, 0.95);
            }
            var kf = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(620) };
            kf.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            kf.KeyFrames.Add(new EasingDoubleKeyFrame(-16, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(110))));
            kf.KeyFrames.Add(new EasingDoubleKeyFrame(11, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(250))));
            kf.KeyFrames.Add(new EasingDoubleKeyFrame(-6, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(390))));
            kf.KeyFrames.Add(new EasingDoubleKeyFrame(3, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(510))));
            kf.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(620))));
            rt.BeginAnimation(RotateTransform.AngleProperty, kf);
        }

        /// <summary>Round button around any child (a <see cref="CrossGlyph"/> usually). Clicks don't bubble to the tile.</summary>
        public static Border RoundButton(FrameworkElement child, double size, Action onClick, Brush? background = null)
        {
            var b = new Border
            {
                Width = size,
                Height = size,
                CornerRadius = new CornerRadius(size / 2),
                Background = background ?? Brushes.Transparent,
                Child = child,
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            b.MouseEnter += (_, _) => b.Opacity = 0.8;
            b.MouseLeave += (_, _) => b.Opacity = 1.0;
            b.MouseLeftButtonDown += (_, e) => e.Handled = true;
            b.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                W.Pulse(b, 0.86);
                onClick();
            };
            return b;
        }

        /// <summary>Clickable full-width row for card lists.</summary>
        public static Border ListRow(DockWidgetPalette p, UIElement content, Action onClick)
        {
            var b = new Border
            {
                Child = content,
                Background = Brushes.Transparent,
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(0, 1, 0, 1),
                Cursor = Cursors.Hand
            };
            b.MouseEnter += (_, _) => b.Background = p.CardRowBackground;
            b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
            b.MouseLeftButtonDown += (_, e) => e.Handled = true;
            b.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                onClick();
            };
            return b;
        }

        /// <summary>Focuses an input once its card is shown (the dock window never activates by itself).</summary>
        public static void FocusInput(TextBox tb, bool activateWindow)
        {
            void Go()
            {
                try
                {
                    if (activateWindow)
                    {
                        var w = Window.GetWindow(tb);
                        if (w is not null && !w.IsActive) w.Activate();
                        else if (PresentationSource.FromVisual(tb) is HwndSource src && src.Handle != 0)
                            DinamikAda.Platform.Win32.Interop.NativeMethods.SetForegroundWindow(src.Handle);
                    }
                    tb.Focus();
                    Keyboard.Focus(tb);
                    tb.CaretIndex = tb.Text.Length;
                }
                catch
                {
                    // Focus is best effort.
                }
            }

            if (tb.IsLoaded)
            {
                tb.Dispatcher.BeginInvoke(new Action(Go), DispatcherPriority.Input);
                return;
            }
            RoutedEventHandler? onLoaded = null;
            onLoaded = (_, _) =>
            {
                tb.Loaded -= onLoaded;
                tb.Dispatcher.BeginInvoke(new Action(Go), DispatcherPriority.Input);
            };
            tb.Loaded += onLoaded;
        }

        /// <summary>Slides an element up and fades it in when it is first shown.</summary>
        public static void AnimateInOnLoad(FrameworkElement el)
        {
            el.Opacity = 0;
            RoutedEventHandler? onLoaded = null;
            onLoaded = (_, _) =>
            {
                el.Loaded -= onLoaded;
                StaggerIn(el, 0, 0, 8);
            };
            el.Loaded += onLoaded;
        }

        public static Color ParseHex(string? hex, Color fallback)
        {
            if (string.IsNullOrWhiteSpace(hex)) return fallback;
            try
            {
                return ColorConverter.ConvertFromString(hex.Trim()) is Color c ? Color.FromRgb(c.R, c.G, c.B) : fallback;
            }
            catch
            {
                return fallback;
            }
        }

        /// <summary>Keeps very bright accents readable on the light style.</summary>
        public static Color ForPalette(DockWidgetPalette p, Color c)
        {
            if (!p.IsLight) return c;
            double lum = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
            if (lum < 0.55) return c;
            double k = 0.55 / lum;
            return Color.FromRgb((byte)(c.R * k), (byte)(c.G * k), (byte)(c.B * k));
        }

        public static string FormatBytes(long bytes)
        {
            if (bytes < 0) bytes = 0;
            double gb = bytes / 1024d / 1024d / 1024d;
            if (gb >= 1000) return (gb / 1024d).ToString(gb / 1024d >= 10 ? "0" : "0.0", CultureInfo.InvariantCulture) + " TB";
            if (gb >= 100) return gb.ToString("0", CultureInfo.InvariantCulture) + " GB";
            if (gb >= 1) return gb.ToString("0.0", CultureInfo.InvariantCulture) + " GB";
            double mb = bytes / 1024d / 1024d;
            if (mb >= 1) return mb.ToString("0", CultureInfo.InvariantCulture) + " MB";
            return (bytes / 1024d).ToString("0", CultureInfo.InvariantCulture) + " KB";
        }

        public static string FormatPercent(double pct) =>
            (pct >= 0 ? "+" : "") + pct.ToString("0.0", CultureInfo.InvariantCulture) + "%";

        /// <summary>Runs an async OS command without blocking the UI; skipped in audit mode.</summary>
        public static void Fire(DockWidgetContext ctx, Func<Task> op, string what)
        {
            if (ctx.IsAuditMode) return;
            _ = RunSafe(op, what);
        }

        private static async Task RunSafe(Func<Task> op, string what)
        {
            try
            {
                await op();
            }
            catch (Exception ex)
            {
                CrashLogger.Log("DockWidget", ex, what);
            }
        }

        public static void Safe(DockWidgetContext ctx, Action op, string what)
        {
            if (ctx.IsAuditMode) return;
            try
            {
                op();
            }
            catch (Exception ex)
            {
                CrashLogger.Log("DockWidget", ex, what);
            }
        }
    }

    /// <summary>A glyph that cross-fades (with a small pop) to a new glyph.</summary>
    private sealed class CrossGlyph : Grid
    {
        private readonly TextBlock _a;
        private readonly TextBlock _b;
        private bool _aFront = true;
        private string? _glyph;

        public CrossGlyph(DockWidgetPalette p, double size, Brush brush)
        {
            _a = W.Glyph(p, "", size, brush);
            _b = W.Glyph(p, "", size, brush);
            _b.Opacity = 0;
            Children.Add(_a);
            Children.Add(_b);
            IsHitTestVisible = false;
            HorizontalAlignment = HorizontalAlignment.Center;
            VerticalAlignment = VerticalAlignment.Center;
        }

        private TextBlock Front => _aFront ? _a : _b;
        private TextBlock Back => _aFront ? _b : _a;

        public void Set(string glyph, Color? color = null)
        {
            if (_glyph == glyph)
            {
                if (color.HasValue) Fx.SetForeground(Front, color.Value);
                return;
            }
            bool first = _glyph is null;
            _glyph = glyph;
            if (first)
            {
                Front.Text = glyph;
                if (color.HasValue) Fx.SetForeground(Front, color.Value);
                return;
            }
            var back = Back;
            var front = Front;
            back.Text = glyph;
            if (color.HasValue) Fx.SetForeground(back, color.Value);
            var st = new ScaleTransform(0.55, 0.55);
            back.RenderTransformOrigin = new Point(0.5, 0.5);
            back.RenderTransform = st;
            var pop = Motion.Soft;
            st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.55, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = pop });
            st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.55, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = pop });
            Fx.FadeTo(back, 1, 220, 0);
            Fx.FadeTo(front, 0, 170);
            _aFront = !_aFront;
        }
    }

    /// <summary>Rounded (or circular) cover art that cross-fades between tracks.</summary>
    private sealed class CoverView : Grid
    {
        private readonly Border _placeholder;
        private readonly Border _a;
        private readonly Border _b;
        private bool _aFront = true;
        private bool _initialized;
        private BitmapImage? _current;

        public CoverView(DockWidgetPalette p, double size, double radius)
        {
            Width = size;
            Height = size;
            var cr = new CornerRadius(radius);
            _placeholder = new Border
            {
                CornerRadius = cr,
                Background = p.Track,
                Child = W.Glyph(p, GlyphMusic, Math.Max(7, size * 0.42), p.SecondaryForeground)
            };
            _a = new Border { CornerRadius = cr, Opacity = 0 };
            _b = new Border { CornerRadius = cr, Opacity = 0 };
            Children.Add(_placeholder);
            Children.Add(_a);
            Children.Add(_b);
            VerticalAlignment = VerticalAlignment.Center;
            HorizontalAlignment = HorizontalAlignment.Center;
        }

        public bool HasImage => _current is not null;

        public void SetImage(BitmapImage? img)
        {
            if (_initialized && ReferenceEquals(img, _current)) return;
            bool first = !_initialized;
            _initialized = true;
            _current = img;
            var front = _aFront ? _a : _b;
            var back = _aFront ? _b : _a;
            double ms = first ? 0 : 380;
            if (img is null)
            {
                Fx.FadeTo(front, 0, ms);
                Fx.FadeTo(back, 0, ms);
                Fx.FadeTo(_placeholder, 1, ms);
                return;
            }
            var brush = new ImageBrush(img) { Stretch = Stretch.UniformToFill };
            brush.Freeze();
            back.Background = brush;
            Fx.FadeTo(back, 1, ms, first ? null : 0);
            Fx.FadeTo(front, 0, ms);
            Fx.FadeTo(_placeholder, 0, ms);
            _aFront = !_aFront;
        }
    }

    /// <summary>Decodes thumbnail bytes once per bytes reference.</summary>
    private sealed class CoverCache
    {
        private byte[]? _bytes;
        private BitmapImage? _image;

        public BitmapImage? Get(byte[]? bytes, int decodeWidth)
        {
            if (bytes is null || bytes.Length == 0)
            {
                _bytes = null;
                _image = null;
                return null;
            }
            if (ReferenceEquals(bytes, _bytes)) return _image;
            _bytes = bytes;
            _image = Decode(bytes, decodeWidth);
            return _image;
        }

        private static BitmapImage? Decode(byte[] bytes, int decodeWidth)
        {
            try
            {
                using var ms = new System.IO.MemoryStream(bytes, writable: false);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = decodeWidth;
                bmp.StreamSource = ms;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>Single-line text that slides in on change and marquee-scrolls only when it overflows.</summary>
    private sealed class MarqueeText : Canvas
    {
        private readonly TranslateTransform _tt = new();
        private bool _running;
        private double _overflow;

        public MarqueeText(TextBlock tb)
        {
            Text = tb;
            tb.TextTrimming = TextTrimming.None;
            tb.VerticalAlignment = VerticalAlignment.Top;
            tb.RenderTransform = _tt;
            ClipToBounds = true;
            Height = Math.Ceiling(tb.FontSize * 1.36);
            VerticalAlignment = VerticalAlignment.Center;
            Children.Add(tb);
            SizeChanged += (_, _) => Reevaluate();
        }

        public TextBlock Text { get; }

        public void SetText(string text, bool slide = true)
        {
            if (string.Equals(Text.Text, text, StringComparison.Ordinal)) return;
            bool had = !string.IsNullOrEmpty(Text.Text);
            Text.Text = text;
            Stop();
            if (slide && had)
            {
                var a = new DoubleAnimation(16, 0, TimeSpan.FromMilliseconds(340)) { EasingFunction = Fx.EaseOut() };
                a.Completed += (_, _) => Reevaluate();
                _tt.BeginAnimation(TranslateTransform.XProperty, a);
                Text.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300)) { EasingFunction = Fx.EaseOut() });
            }
            else
            {
                Reevaluate();
            }
        }

        public void Reevaluate()
        {
            if (ActualWidth <= 1) return;
            Text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double overflow = Text.DesiredSize.Width - ActualWidth;
            if (overflow <= 1)
            {
                Stop();
                return;
            }
            if (_running && Math.Abs(overflow - _overflow) < 0.5) return;
            _overflow = overflow;
            _running = true;
            double dist = overflow + 8;
            double move = Math.Max(1.6, dist / 24.0);
            const double hold = 1.8;
            var kf = new DoubleAnimationUsingKeyFrames
            {
                RepeatBehavior = RepeatBehavior.Forever,
                Duration = TimeSpan.FromSeconds(hold * 2 + move * 2)
            };
            var ease = Motion.Gentle;
            kf.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            kf.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(hold))));
            kf.KeyFrames.Add(new EasingDoubleKeyFrame(-dist, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(hold + move)), ease));
            kf.KeyFrames.Add(new LinearDoubleKeyFrame(-dist, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(hold * 2 + move))));
            kf.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(hold * 2 + move * 2)), ease));
            _tt.BeginAnimation(TranslateTransform.XProperty, kf);
        }

        public void Stop()
        {
            _running = false;
            _tt.BeginAnimation(TranslateTransform.XProperty, null);
            _tt.X = 0;
        }
    }

    // =============================================================================================
    // Media
    // =============================================================================================

    private static class MediaState
    {
        public const string AuditSessionId = "__dock_audit__";

        private static MediaPlaybackSnapshot? _sample;

        /// <summary>Audit-only sample track, with generated warm cover art so the album-colour path is exercised.</summary>
        private static MediaPlaybackSnapshot Sample => _sample ??= new(
            HasSession: true, SessionId: AuditSessionId, AppName: "Spotify", Title: "Midnight City", Artist: "Night Drive",
            IsPlaying: true, CanPlayPause: true, CanNext: true, CanPrev: true, CanSeek: true,
            Position: TimeSpan.FromSeconds(83), Duration: TimeSpan.FromSeconds(225),
            LastUpdatedUtc: DateTimeOffset.UtcNow, PlaybackRate: 1.0, AccentHex: "#FF6B8B", ThumbnailBytes: SampleArt(),
            AllSessions: ImmutableArray<MediaSessionSummary>.Empty);

        /// <summary>A small abstract cover: an amber sun over burnt-orange dunes on a plum sky.</summary>
        private static byte[]? SampleArt()
        {
            try
            {
                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen())
                {
                    dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0x5A, 0x2A, 0x48), Color.FromRgb(0xD9, 0x6A, 0x2B), 90), null, new Rect(0, 0, 96, 96));
                    dc.DrawEllipse(new RadialGradientBrush(Color.FromRgb(0xFF, 0xD0, 0x6A), Color.FromRgb(0xF2, 0x8A, 0x2E)), null, new Point(48, 50), 22, 22);
                    var dune = new StreamGeometry();
                    using (var g = dune.Open())
                    {
                        g.BeginFigure(new Point(0, 70), true, true);
                        g.BezierTo(new Point(30, 52), new Point(60, 84), new Point(96, 62), true, true);
                        g.LineTo(new Point(96, 96), true, true);
                        g.LineTo(new Point(0, 96), true, true);
                    }
                    dc.DrawGeometry(new SolidColorBrush(Color.FromRgb(0x8C, 0x3B, 0x1E)), null, dune);
                }
                var rtb = new RenderTargetBitmap(96, 96, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(dv);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(rtb));
                using var ms = new System.IO.MemoryStream();
                enc.Save(ms);
                return ms.ToArray();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Album-colour gradient for the current track (graphite when nothing plays).</summary>
        public static Brush Body(AlbumColors colors, MediaPlaybackSnapshot? m) =>
            m is null ? Idle : colors.Get(m.ThumbnailBytes, m.AccentHex, m.SecondaryAccentHex).Brush;

        private static readonly Brush Idle = Vivid.Gradient(TileColors.GradientFrom([]));

        /// <summary>"Title · Artist" for one-line media tiles.</summary>
        public static string TitleLine(DockWidgetContext ctx, MediaPlaybackSnapshot? m)
        {
            string t = Title(ctx, m), a = Artist(m);
            return string.IsNullOrWhiteSpace(a) || a == t ? t : t + " · " + a;
        }

        public static MediaPlaybackSnapshot? Get(DockWidgetContext ctx)
        {
            var m = ctx.Orch.Media.Current;
            if (m is { HasSession: true }) return m;
            return ctx.IsAuditMode ? Sample : null;
        }

        public static TimeSpan Position(MediaPlaybackSnapshot m)
        {
            var pos = m.Position;
            if (m.IsPlaying && m.SessionId != AuditSessionId)
            {
                var elapsed = DateTimeOffset.UtcNow - m.LastUpdatedUtc;
                if (elapsed > TimeSpan.Zero)
                {
                    double rate = m.PlaybackRate > 0 && double.IsFinite(m.PlaybackRate) ? m.PlaybackRate : 1.0;
                    pos += TimeSpan.FromTicks((long)(elapsed.Ticks * rate));
                }
            }
            if (pos < TimeSpan.Zero) pos = TimeSpan.Zero;
            if (m.Duration > TimeSpan.Zero && pos > m.Duration) pos = m.Duration;
            return pos;
        }

        public static double Progress(MediaPlaybackSnapshot m) =>
            m.Duration > TimeSpan.Zero ? Math.Clamp(Position(m).TotalSeconds / m.Duration.TotalSeconds, 0, 1) : 0;

        public static string Title(DockWidgetContext ctx, MediaPlaybackSnapshot? m)
        {
            if (m is null) return ctx.T("Çalan yok", "Nothing playing");
            if (!string.IsNullOrWhiteSpace(m.Title)) return m.Title;
            return string.IsNullOrWhiteSpace(m.AppName) ? ctx.T("Bilinmeyen parça", "Unknown track") : m.AppName;
        }

        public static string Artist(MediaPlaybackSnapshot? m) =>
            m is null ? string.Empty : !string.IsNullOrWhiteSpace(m.Artist) ? m.Artist : m.AppName;

        public static void Toggle(DockWidgetContext ctx) =>
            Fx.Fire(ctx, () => ctx.Orch.Media.TogglePlayPauseAsync(), "Play/pause failed.");

        public static void Next(DockWidgetContext ctx) =>
            Fx.Fire(ctx, () => ctx.Orch.Media.NextAsync(), "Next track failed.");

        public static void Previous(DockWidgetContext ctx) =>
            Fx.Fire(ctx, () => ctx.Orch.Media.PreviousAsync(), "Previous track failed.");

        public static void Seek(DockWidgetContext ctx, TimeSpan pos) =>
            Fx.Fire(ctx, () => ctx.Orch.Media.SeekAsync(pos), "Seek failed.");
    }

    /// <summary>Per-tile accent cache so the media colour is parsed only when it changes.</summary>
    private sealed class MediaAccent
    {
        private string? _hex;
        private Color _color;
        private bool _light;

        public Color Get(DockWidgetContext ctx, MediaPlaybackSnapshot? m)
        {
            var p = ctx.Palette;
            string? hex = m?.AccentHex;
            if (string.IsNullOrWhiteSpace(hex)) return p.Accent;
            if (hex != _hex || _light != p.IsLight)
            {
                _hex = hex;
                _light = p.IsLight;
                _color = Fx.ForPalette(p, Fx.ParseHex(hex, p.Accent));
            }
            return _color;
        }
    }

    /// <summary>The shared Now Playing detail card: big cover, seekable bar with times, controls.</summary>
    private sealed class NowPlayingCard
    {
        private const double Width = 280;
        private readonly CoverCache _cache = new();
        private readonly MediaAccent _accent = new();
        private CoverView _cover = null!;
        private MarqueeText _title = null!;
        private TextBlock _artist = null!;
        private TextBlock _elapsed = null!;
        private TextBlock _remaining = null!;
        private BarGauge _bar = null!;
        private CrossGlyph _play = null!;
        private FrameworkElement _controls = null!;
        private FrameworkElement _seek = null!;
        private MediaPlaybackSnapshot? _last;

        public FrameworkElement Build(DockWidgetContext ctx, DockWidgetDescriptor d)
        {
            var p = ctx.Palette;
            _cover = new CoverView(p, 168, 14) { Margin = new Thickness(0, 0, 0, 12) };
            _title = new MarqueeText(W.Text(p, "", 15, weight: FontWeights.SemiBold));
            _artist = W.Text(p, "", 12.5, p.SecondaryForeground, ui: false);
            _artist.Margin = new Thickness(0, 2, 0, 10);

            _bar = W.Bar(p, Width, 5, p.Accent);
            _bar.HorizontalAlignment = HorizontalAlignment.Stretch;
            var seekHost = new Border { Background = Brushes.Transparent, Height = 16, Cursor = Cursors.Hand, Child = _bar };
            seekHost.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                var m = _last;
                if (m is null || m.Duration <= TimeSpan.Zero || seekHost.ActualWidth <= 0) return;
                double f = Math.Clamp(e.GetPosition(seekHost).X / seekHost.ActualWidth, 0, 1);
                _bar.AnimateTo(f, 200);
                MediaState.Seek(ctx, TimeSpan.FromSeconds(m.Duration.TotalSeconds * f));
            };
            seekHost.MouseLeftButtonUp += (_, e) => e.Handled = true;
            _seek = seekHost;

            _elapsed = W.Text(p, "0:00", 11, p.SecondaryForeground, mono: true);
            _remaining = W.Text(p, "-0:00", 11, p.SecondaryForeground, mono: true);
            var times = new DockPanel { Margin = new Thickness(0, 2, 0, 8) };
            DockPanel.SetDock(_remaining, Dock.Right);
            times.Children.Add(_remaining);
            times.Children.Add(_elapsed);

            _play = new CrossGlyph(p, 18, p.IsLight ? Brushes.White : W.Brush(Color.FromRgb(0x05, 0x08, 0x10)));
            var playBtn = Fx.RoundButton(_play, 44, () => MediaState.Toggle(ctx), p.AccentBrush);
            var prev = W.IconButton(p, GlyphPrev, 36, () => MediaState.Previous(ctx));
            var next = W.IconButton(p, GlyphNext, 36, () => MediaState.Next(ctx));
            var controls = W.Row(18, prev, playBtn, next);
            controls.HorizontalAlignment = HorizontalAlignment.Center;
            _controls = controls;

            var body = W.Column(_cover, _title, _artist, _seek, times, controls);
            return W.Card(p, d.Glyph, d.Name(ctx.Lang), body, Width);
        }

        public void Refresh(DockWidgetContext ctx)
        {
            var m = MediaState.Get(ctx);
            _last = m;
            _title.SetText(MediaState.Title(ctx, m));
            W.SetTextAnimated(_artist, MediaState.Artist(m));
            _cover.SetImage(m is null ? null : _cache.Get(m.ThumbnailBytes, 256));
            _play.Set(m is { IsPlaying: true } ? GlyphPause : GlyphPlay);
            var accent = _accent.Get(ctx, m);
            Fx.SetFill(_bar, accent);
            if (_controls is Panel panel && panel.Children.Count > 1 && panel.Children[1] is Border pb &&
                (pb.Background is not SolidColorBrush sb || sb.Color != accent))
            {
                pb.Background = W.Brush(accent);
            }
            if (m is null)
            {
                _bar.AnimateTo(0);
                _elapsed.Text = "–:––";
                _remaining.Text = "–:––";
            }
            else
            {
                var pos = MediaState.Position(m);
                _bar.AnimateTo(MediaState.Progress(m));
                _elapsed.Text = DockWidgetMath.FormatTimer(pos);
                _remaining.Text = m.Duration > TimeSpan.Zero ? "-" + DockWidgetMath.FormatTimer(m.Duration - pos) : "–:––";
            }
            double op = m is null ? 0.4 : 1;
            _controls.IsHitTestVisible = m is not null;
            _seek.IsHitTestVisible = m is { CanSeek: true };
            if (Math.Abs(_controls.Opacity - op) > 0.01) Fx.FadeTo(_controls, op, 200);
        }
    }

    private enum NowPlayingLayout { Full, Compact, Bar }

    /// <summary>
    /// now-playing / np-compact / np-bar: a tile coloured from the album art (the gradient crossfades when the track
    /// changes), with white text and transport glyphs.
    /// </summary>
    private sealed class NowPlayingTile : DockWidgetTileBase
    {
        private readonly NowPlayingLayout _layout;
        private readonly CoverCache _cache = new();
        private readonly AlbumColors _colors = new();
        private readonly NowPlayingCard _card = new();
        private CoverView? _cover;
        private MarqueeText _title = null!;
        private TextBlock? _artist;
        private BarGauge? _bar;
        private CrossGlyph? _play;
        private FrameworkElement _controls = null!;
        private bool? _hadSession;
        private bool _painted;

        public NowPlayingTile(DockWidgetDescriptor d, DockWidgetContext c, NowPlayingLayout layout) : base(d, c)
        {
            _layout = layout;
            // BuildContent ran in the base constructor before _layout was set; rebuild for the real layout.
            SetContent(Build(c));
            Refresh(c);
        }

        protected override bool VividByDefault => true;

        protected override Thickness ContentPadding(double h) => new(Math.Round(h * 0.24), 1, Math.Round(h * 0.24), 1);

        protected override FrameworkElement BuildContent(DockWidgetContext ctx) => new Grid();

        private static Border Transport(DockWidgetPalette p, string glyph, double size, Action onClick) =>
            W.IconButton(p, glyph, size, onClick, foreground: Vivid.OnColor);

        private FrameworkElement Build(DockWidgetContext ctx)
        {
            var p = ctx.Palette;
            double h = ctx.TileHeight;
            var g = new Grid { ClipToBounds = false };

            switch (_layout)
            {
                case NowPlayingLayout.Full:
                {
                    // "Title · Artist" centred, transport glyphs centred under it, a hairline progress at the foot.
                    var titleText = W.Text(p, "", Math.Max(9, Math.Round(h * 0.3)), Vivid.OnColor, FontWeights.Bold);
                    _title = new MarqueeText(titleText) { HorizontalAlignment = HorizontalAlignment.Stretch };
                    _play = new CrossGlyph(p, Math.Max(9, Math.Round(h * 0.34)), Vivid.OnColor);
                    double btn = Math.Max(13, Math.Round(h * 0.44));
                    var prev = Transport(p, GlyphPrev, btn, () => MediaState.Previous(ctx));
                    var play = Fx.RoundButton(_play, btn, () => MediaState.Toggle(ctx));
                    var next = Transport(p, GlyphNext, btn, () => MediaState.Next(ctx));
                    var row = W.Row(Math.Round(h * 0.34), prev, play, next);
                    row.HorizontalAlignment = HorizontalAlignment.Center;
                    _controls = row;
                    var col = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                    col.Children.Add(_title);
                    col.Children.Add(row);
                    row.Margin = new Thickness(0, -1, 0, 0);
                    g.Children.Add(col);
                    _bar = new BarGauge
                    {
                        Height = Math.Max(1.5, Math.Round(h * 0.05 * 2) / 2),
                        Track = Vivid.OnColorTrack,
                        Fill = W.Brush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF)),
                        VerticalAlignment = VerticalAlignment.Bottom,
                        Margin = new Thickness(Math.Round(h * 0.06), 0, Math.Round(h * 0.06), 0),
                        IsHitTestVisible = false,
                        Opacity = 0.9
                    };
                    g.Children.Add(_bar);
                    break;
                }
                case NowPlayingLayout.Compact:
                {
                    _title = new MarqueeText(W.Text(p, "", Math.Max(9, Math.Round(h * 0.3)), Vivid.OnColor, FontWeights.Bold));
                    _artist = W.Text(p, "", Vivid.LabelSize(h), Vivid.OnColorSecondary, FontWeights.Medium, ui: false);
                    _cover = new CoverView(p, Math.Round(h * 0.7), Math.Max(4, Math.Round(h * 0.18)));
                    _play = new CrossGlyph(p, Math.Max(9, Math.Round(h * 0.34)), Vivid.OnColor);
                    var play = Fx.RoundButton(_play, Math.Max(18, Math.Round(h * 0.62)), () => MediaState.Toggle(ctx), W.Brush(Colors.White, 0x2A));
                    _controls = play;
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    _cover.Margin = new Thickness(-Math.Round(h * 0.1), 0, Math.Round(h * 0.18), 0);
                    g.Children.Add(_cover);
                    var text = W.Column(_title, _artist);
                    text.Margin = new Thickness(0, 0, 4, 0);
                    Grid.SetColumn(text, 1);
                    g.Children.Add(text);
                    Grid.SetColumn(play, 2);
                    g.Children.Add(play);
                    break;
                }
                default:
                {
                    _title = new MarqueeText(W.Text(p, "", Math.Max(9, Math.Round(h * 0.3)), Vivid.OnColor, FontWeights.Bold));
                    _bar = new BarGauge
                    {
                        Height = Math.Max(2.5, Math.Round(h * 0.09)),
                        Track = Vivid.OnColorTrack,
                        Fill = Vivid.OnColor,
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        Margin = new Thickness(0, Math.Max(2, Math.Round(h * 0.1)), 0, 0)
                    };
                    var skip = Transport(p, GlyphNext, Math.Max(18, Math.Round(h * 0.6)), () => MediaState.Next(ctx));
                    _controls = skip;
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    var col = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
                    col.Children.Add(_title);
                    col.Children.Add(_bar);
                    g.Children.Add(col);
                    Grid.SetColumn(skip, 1);
                    g.Children.Add(skip);
                    break;
                }
            }
            if (_artist is not null) _artist.HorizontalAlignment = HorizontalAlignment.Left;
            return g;
        }

        protected override void Refresh(DockWidgetContext ctx)
        {
            if (_title is null) return; // still inside the base constructor
            var m = MediaState.Get(ctx);
            SetTileBackground(MediaState.Body(_colors, m), animate: _painted);
            _painted = true;
            if (_layout == NowPlayingLayout.Full)
            {
                _title.SetText(MediaState.TitleLine(ctx, m));
                CenterTitle();
            }
            else
            {
                _title.SetText(MediaState.Title(ctx, m));
            }
            if (_artist is not null) W.SetTextAnimated(_artist, MediaState.Artist(m));
            _cover?.SetImage(m is null ? null : _cache.Get(m.ThumbnailBytes, 96));
            _play?.Set(m is { IsPlaying: true } ? GlyphPause : GlyphPlay);
            _bar?.AnimateTo(m is null ? 0 : MediaState.Progress(m));
            bool has = m is not null;
            if (_hadSession != has)
            {
                _hadSession = has;
                _controls.IsHitTestVisible = has;
                Fx.FadeTo(_controls, has ? 1 : 0.45, 220);
            }
        }

        /// <summary>Centres a title that fits; long titles keep the marquee (left-aligned while scrolling).</summary>
        private void CenterTitle()
        {
            var tb = _title.Text;
            tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double room = _title.ActualWidth > 1 ? _title.ActualWidth : Chip.Width - ContentPadding(Chip.Height).Left * 2;
            double x = Math.Max(0, (room - tb.DesiredSize.Width) / 2);
            Canvas.SetLeft(tb, Math.Round(x));
        }

        protected override FrameworkElement? CreateCard(DockWidgetContext ctx) => _card.Build(ctx, Descriptor);
        protected override void RefreshCard(DockWidgetContext ctx) => _card.Refresh(ctx);

        public override void OnAttached(DockWidgetContext ctx)
        {
            _title.Reevaluate();
            if (_layout == NowPlayingLayout.Full) _title.SizeChanged += (_, _) => CenterTitle();
        }

        public override void OnDetached() => _title.Stop();
    }

    /// <summary>np-mini: an album-coloured squircle with a white progress ring round a white play glyph.</summary>
    private sealed class MiniPlayerTile : DockWidgetTileBase
    {
        private readonly AlbumColors _colors = new();
        private RingGauge _ring = null!;
        private CrossGlyph _play = null!;
        private Grid _button = null!;
        private bool _painted;

        public MiniPlayerTile(DockWidgetDescriptor d, DockWidgetContext c) : base(d, c) { }

        protected override bool VividByDefault => true;

        protected override FrameworkElement BuildContent(DockWidgetContext ctx)
        {
            var p = ctx.Palette;
            double h = ctx.TileHeight;
            double ringSize = Math.Round(h * 0.7);
            double stroke = Math.Max(1.8, Math.Round(h * 0.07 * 2) / 2);
            _ring = new RingGauge
            {
                Width = ringSize,
                Height = ringSize,
                StrokeThickness = stroke,
                Track = Vivid.OnColorTrack,
                Fill = Vivid.OnColor,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            _play = new CrossGlyph(p, Math.Max(8, Math.Round(h * 0.28)), Vivid.OnColor);
            _button = new Grid { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            _button.Children.Add(_ring);
            _button.Children.Add(_play);
            return _button;
        }

        protected override void Refresh(DockWidgetContext ctx)
        {
            var m = MediaState.Get(ctx);
            SetTileBackground(MediaState.Body(_colors, m), animate: _painted);
            _painted = true;
            _play.Set(m is { IsPlaying: true } ? GlyphPause : GlyphPlay);
            _ring.AnimateTo(m is null ? 0 : MediaState.Progress(m), 900);
            _button.Opacity = m is null ? 0.6 : 1;
        }

        public override bool OnPrimaryClick(DockWidgetContext ctx)
        {
            W.Pulse(_button, 0.84);
            MediaState.Toggle(ctx);
            return true;
        }
    }

    // ---- Audio ----------------------------------------------------------------------------------

    private static class AudioState
    {
        private static readonly ImmutableArray<AudioEndpointSnapshot> SampleOutputs =
        [
            new AudioEndpointSnapshot("audit-spk", "Hoparlörler (Realtek Audio)", true, false, false),
            new AudioEndpointSnapshot("audit-bt", "AirPods Pro", false, false, true),
            new AudioEndpointSnapshot("audit-hdmi", "LG ULTRAGEAR (HDMI)", false, false, false)
        ];

        public static (double Volume, bool Muted, ImmutableArray<AudioEndpointSnapshot> Outputs) Read(DockWidgetContext ctx)
        {
            var a = ctx.Orch.Audio.Current;
            var outs = a is null || a.OutputDevices.IsDefault ? ImmutableArray<AudioEndpointSnapshot>.Empty : a.OutputDevices;
            if (ctx.IsAuditMode && outs.IsEmpty) return (0.62, false, SampleOutputs);
            if (a is null) return (0, false, outs);
            double v = double.IsFinite(a.MasterVolume01) ? Math.Clamp(a.MasterVolume01, 0, 1) : 0;
            return (v, a.IsMasterMuted, outs);
        }

        public static string VolumeGlyph(double v, bool muted) =>
            muted ? "\uE74F" : v < 0.01 ? "\uE992" : v < 0.34 ? "\uE993" : v < 0.67 ? "\uE994" : "\uE995";
    }

    /// <summary>audio-output: speaker glyph (mute toggle) and a draggable volume bar; the card switches devices.</summary>
    private sealed class AudioOutputTile : DockWidgetTileBase
    {
        private CrossGlyph _glyph = null!;
        private Border _host = null!;
        private BarGauge _bar = null!;
        private TextBlock _pct = null!;
        private Color _barFill;
        private bool _dragging;
        private StackPanel? _deviceList;
        private TextBlock? _cardVolume;
        private string _deviceSig = string.Empty;

        public AudioOutputTile(DockWidgetDescriptor d, DockWidgetContext c) : base(d, c) { }

        protected override FrameworkElement BuildContent(DockWidgetContext ctx)
        {
            var p = ctx.Palette;
            double h = ctx.TileHeight;
            // A wide speaker pill: solid accent fill from the left, the speaker glyph riding inside it.
            double pill = Math.Round(h * 0.74);
            _glyph = new CrossGlyph(p, Math.Max(9, Math.Round(pill * 0.46)), Vivid.OnColor);
            var glyphBtn = Fx.RoundButton(_glyph, pill, () =>
            {
                var (_, muted, _) = AudioState.Read(ctx);
                Fx.Safe(ctx, () => ctx.Orch.Audio.SetMasterMute(!muted), "Mute toggle failed.");
            });

            _bar = new BarGauge
            {
                Height = pill,
                Track = p.IsLight ? W.Brush(Colors.Black, 0x14) : W.Brush(Colors.White, 0x1E),
                Fill = Vivid.Solid(p.Accent),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Center
            };
            _host = new Border { Background = Brushes.Transparent, Height = pill, Child = _bar, Cursor = Cursors.SizeWE, VerticalAlignment = VerticalAlignment.Center };
            _host.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                _dragging = true;
                _host.CaptureMouse();
                Apply(ctx, e.GetPosition(_host).X);
            };
            _host.MouseMove += (_, e) =>
            {
                if (_dragging) Apply(ctx, e.GetPosition(_host).X);
            };
            _host.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                if (_dragging) _host.ReleaseMouseCapture();
                _dragging = false;
            };
            _host.LostMouseCapture += (_, _) => _dragging = false;
            _host.MouseWheel += (_, e) =>
            {
                e.Handled = true;
                var (v, _, _) = AudioState.Read(ctx);
                SetVolume(ctx, Math.Clamp(v + (e.Delta > 0 ? 0.02 : -0.02), 0, 1));
            };

            _pct = W.Text(p, "", Math.Max(8, Math.Round(h * 0.25)), p.SecondaryForeground, FontWeights.SemiBold);
            _pct.HorizontalAlignment = HorizontalAlignment.Right;
            _pct.Margin = new Thickness(0, 0, Math.Round(pill * 0.4), 0);
            _pct.IsHitTestVisible = false;
            glyphBtn.HorizontalAlignment = HorizontalAlignment.Left;

            var g = new Grid { VerticalAlignment = VerticalAlignment.Center };
            g.Children.Add(_host);
            g.Children.Add(_pct);
            g.Children.Add(glyphBtn);
            return g;
        }

        private void Apply(DockWidgetContext ctx, double x)
        {
            if (_host.ActualWidth <= 0) return;
            SetVolume(ctx, Math.Clamp(x / _host.ActualWidth, 0, 1));
        }

        private void SetVolume(DockWidgetContext ctx, double v)
        {
            _bar.AnimateTo(v, 70);
            _pct.Text = W.Pct(v * 100);
            _glyph.Set(AudioState.VolumeGlyph(v, false));
            Fx.Safe(ctx, () =>
            {
                ctx.Orch.Audio.SetMasterVolume((float)v);
                if (ctx.Orch.Audio.Current is { IsMasterMuted: true } && v > 0) ctx.Orch.Audio.SetMasterMute(false);
            }, "Volume change failed.");
        }

        protected override void Refresh(DockWidgetContext ctx)
        {
            if (_dragging) return;
            var (v, muted, _) = AudioState.Read(ctx);
            _glyph.Set(AudioState.VolumeGlyph(v, muted));
            var p = ctx.Palette;
            var fill = muted ? Color.FromRgb(0x6E, 0x72, 0x80) : p.Accent;
            if (_barFill != fill)
            {
                _barFill = fill;
                _bar.Fill = Vivid.Solid(fill);
                _bar.InvalidateVisual();
            }
            _bar.AnimateTo(v);
            W.SetTextAnimated(_pct, muted ? ctx.T("Kapalı", "Off") : W.Pct(v * 100));
        }

        protected override FrameworkElement? CreateCard(DockWidgetContext ctx)
        {
            var p = ctx.Palette;
            _deviceSig = string.Empty;
            _deviceList = new StackPanel();
            _cardVolume = W.Text(p, "", 12.5, p.SecondaryForeground, ui: false);
            _cardVolume.Margin = new Thickness(0, 0, 0, 8);
            var caption = W.Text(p, ctx.T("Çıkış aygıtı", "Output device"), 11.5, p.TertiaryForeground, FontWeights.SemiBold);
            caption.Margin = new Thickness(0, 0, 0, 4);
            return W.Card(p, Descriptor.Glyph, Descriptor.Name(ctx.Lang), W.Column(_cardVolume, caption, _deviceList), 290);
        }

        protected override void RefreshCard(DockWidgetContext ctx)
        {
            if (_deviceList is null || _cardVolume is null) return;
            var p = ctx.Palette;
            var (v, muted, outs) = AudioState.Read(ctx);
            _cardVolume.Text = muted ? ctx.T("Ses kapalı", "Sound muted") : ctx.T($"Ses düzeyi {W.Pct(v * 100)}", $"Volume {W.Pct(v * 100)}");
            string sig = string.Join("|", outs.Select(o => o.Id + (o.IsDefault ? "*" : "")));
            if (sig == _deviceSig) return;
            _deviceSig = sig;
            _deviceList.Children.Clear();
            if (outs.IsEmpty)
            {
                _deviceList.Children.Add(W.Text(p, ctx.T("Çıkış aygıtı bulunamadı", "No output devices found"), 12.5, p.SecondaryForeground, ui: false));
                return;
            }
            int i = 0;
            foreach (var o in outs)
            {
                var dp = new DockPanel();
                var icon = W.Glyph(p, o.IsBluetooth ? "\uE7F6" : "\uE7F5", 13, o.IsDefault ? p.AccentBrush : p.SecondaryForeground);
                icon.Margin = new Thickness(0, 0, 10, 0);
                DockPanel.SetDock(icon, Dock.Left);
                dp.Children.Add(icon);
                if (o.IsDefault)
                {
                    var check = W.Glyph(p, "\uE73E", 12, p.AccentBrush);
                    DockPanel.SetDock(check, Dock.Right);
                    dp.Children.Add(check);
                }
                dp.Children.Add(W.Text(p, o.FriendlyName, 12.5, o.IsDefault ? p.Foreground : p.SecondaryForeground,
                    o.IsDefault ? FontWeights.SemiBold : FontWeights.Normal, ui: false));
                string id = o.Id;
                var row = Fx.ListRow(p, dp, () =>
                    Fx.Fire(ctx, () => ctx.Orch.Audio.SetDefaultOutputDeviceAsync(id), "Switching output device failed."));
                _deviceList.Children.Add(row);
                Fx.StaggerIn(row, i++, 35);
            }
        }
    }

    /// <summary>microphone: mic mute and sound mute, with a live pulse ring while the mic is in use.</summary>
    private sealed class MicrophoneTile : DockWidgetTileBase
    {
        private CrossGlyph _micGlyph = null!;
        private CrossGlyph _spkGlyph = null!;
        private SolidColorBrush _micBg = null!;
        private SolidColorBrush _spkBg = null!;
        private Border _micBtn = null!;
        private Ellipse _pulse = null!;
        private ScaleTransform _pulseScale = null!;
        private bool _attached;
        private bool _pulsing;
        private bool? _micMuted;
        private bool? _spkMuted;

        public MicrophoneTile(DockWidgetDescriptor d, DockWidgetContext c) : base(d, c) { }

        protected override FrameworkElement BuildContent(DockWidgetContext ctx)
        {
            var p = ctx.Palette;
            double h = ctx.TileHeight;
            double size = Math.Max(18, h * 0.78);
            _micBg = new SolidColorBrush(Colors.Transparent);
            _spkBg = new SolidColorBrush(Colors.Transparent);
            _micGlyph = new CrossGlyph(p, Math.Max(10, h * 0.38), p.Foreground);
            _spkGlyph = new CrossGlyph(p, Math.Max(10, h * 0.38), p.Foreground);
            _micBtn = Fx.RoundButton(_micGlyph, size, () => Fx.Safe(ctx, ctx.Orch.ToggleMicMuteWithHud, "Mic toggle failed."), _micBg);
            var spkBtn = Fx.RoundButton(_spkGlyph, size, () =>
            {
                var a = ctx.Orch.Audio.Current;
                if (a is not null) Fx.Safe(ctx, () => ctx.Orch.Audio.SetMasterMute(!a.IsMasterMuted), "Mute toggle failed.");
            }, _spkBg);

            _pulseScale = new ScaleTransform(1, 1);
            _pulse = new Ellipse
            {
                Width = size,
                Height = size,
                Stroke = W.Brush(p.Positive),
                StrokeThickness = 1.6,
                Opacity = 0,
                IsHitTestVisible = false,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = _pulseScale,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            var micCell = new Grid();
            micCell.Children.Add(_pulse);
            micCell.Children.Add(_micBtn);

            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.Children.Add(micCell);
            Grid.SetColumn(spkBtn, 1);
            g.Children.Add(spkBtn);
            return g;
        }

        private static void AnimateBg(SolidColorBrush b, Color to) =>
            b.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(to, TimeSpan.FromMilliseconds(260)) { EasingFunction = Fx.EaseOut() });

        protected override void Refresh(DockWidgetContext ctx)
        {
            var p = ctx.Palette;
            var a = ctx.Orch.Audio.Current;
            bool hasMic = ctx.IsAuditMode || a is { HasMicDevice: true };
            bool micMuted = !ctx.IsAuditMode && (a?.IsMicMuted ?? false);
            bool spkMuted = !ctx.IsAuditMode && (a?.IsMasterMuted ?? false);
            bool inUse = ctx.IsAuditMode || ctx.Orch.Privacy.Current is { IsMicActive: true };
            var fg = (p.Foreground as SolidColorBrush)?.Color ?? Colors.White;
            var red = Color.FromArgb(0x38, p.Negative.R, p.Negative.G, p.Negative.B);

            if (_micMuted != micMuted)
            {
                _micMuted = micMuted;
                _micGlyph.Set(micMuted ? "\uEC54" : "\uE720", micMuted ? p.Negative : fg);
                AnimateBg(_micBg, micMuted ? red : Colors.Transparent);
            }
            if (_spkMuted != spkMuted)
            {
                _spkMuted = spkMuted;
                _spkGlyph.Set(spkMuted ? "\uE74F" : "\uE767", spkMuted ? p.Negative : fg);
                AnimateBg(_spkBg, spkMuted ? red : Colors.Transparent);
            }
            _micBtn.IsHitTestVisible = hasMic;
            if (!_micBtn.IsMouseOver) _micBtn.Opacity = hasMic ? 1 : 0.45;
            UpdatePulse(hasMic && !micMuted && inUse);
        }

        private void UpdatePulse(bool live)
        {
            bool want = live && _attached;
            if (want == _pulsing) return;
            _pulsing = want;
            if (want)
            {
                var dur = TimeSpan.FromSeconds(1.5);
                var grow = new DoubleAnimation(1, 1.5, dur) { RepeatBehavior = RepeatBehavior.Forever, EasingFunction = Fx.EaseOut() };
                _pulseScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
                _pulseScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
                _pulse.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.85, 0, dur) { RepeatBehavior = RepeatBehavior.Forever });
            }
            else
            {
                _pulseScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                _pulseScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                _pulse.BeginAnimation(UIElement.OpacityProperty, null);
                _pulse.Opacity = 0;
            }
        }

        public override bool OnPrimaryClick(DockWidgetContext ctx) => true;

        public override void OnAttached(DockWidgetContext ctx)
        {
            _attached = true;
            _micMuted = null;
            Refresh(ctx);
        }

        public override void OnDetached()
        {
            _attached = false;
            UpdatePulse(false);
        }
    }

    // =============================================================================================
    // Apps & files
    // =============================================================================================

    private sealed record RecentItem(string Name, string Path, DateTime Opened);

    /// <summary>recent: the 10 most recently opened items from the shell's Recent folder.</summary>
    private sealed class RecentTile : DockWidgetTileBase
    {
        private StackPanel? _list;

        public RecentTile(DockWidgetDescriptor d, DockWidgetContext c) : base(d, c) { }

        protected override FrameworkElement BuildContent(DockWidgetContext ctx) =>
            W.Glyph(ctx.Palette, "\uE81C", Math.Max(12, ctx.TileHeight * 0.48));

        protected override void Refresh(DockWidgetContext ctx) { }

        protected override FrameworkElement? CreateCard(DockWidgetContext ctx)
        {
            var p = ctx.Palette;
            var list = new StackPanel();
            _list = list;
            list.Children.Add(W.Text(p, ctx.T("Yükleniyor…", "Loading…"), 12.5, p.SecondaryForeground, ui: false));
            _ = LoadAsync(ctx, list);
            return W.Card(p, Descriptor.Glyph, Descriptor.Name(ctx.Lang), list, 300);
        }

        private async Task LoadAsync(DockWidgetContext ctx, StackPanel list)
        {
            List<RecentItem> items;
            if (ctx.IsAuditMode)
            {
                var now = ctx.Now;
                items =
                [
                    new("Q4 Bütçe.xlsx", "", now.AddMinutes(-4)),
                    new("Sunum Taslağı.pptx", "", now.AddMinutes(-22)),
                    new("Ekran görüntüsü 2026-11-18.png", "", now.AddHours(-1)),
                    new("Projeler", "", now.AddHours(-2)),
                    new("sözleşme.pdf", "", now.AddHours(-5)),
                    new("notlar.txt", "", now.AddDays(-1))
                ];
            }
            else
            {
                try
                {
                    items = await Task.Run(Enumerate);
                }
                catch (Exception ex)
                {
                    CrashLogger.Log("DockWidget", ex, "Reading recent items failed.");
                    items = [];
                }
            }
            if (!ReferenceEquals(list, _list)) return;
            Populate(ctx, list, items);
        }

        private static List<RecentItem> Enumerate()
        {
            string dir = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
            if (string.IsNullOrEmpty(dir) || !System.IO.Directory.Exists(dir)) return [];
            return new System.IO.DirectoryInfo(dir)
                .EnumerateFiles("*.lnk")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Take(10)
                .Select(f => new RecentItem(IOPath.GetFileNameWithoutExtension(f.Name), f.FullName, f.LastWriteTime))
                .ToList();
        }

        private static string GlyphFor(string name)
        {
            string ext = IOPath.GetExtension(name).ToLowerInvariant();
            return ext switch
            {
                "" => "\uE8B7",
                ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" or ".heic" or ".svg" => "\uE91B",
                ".mp4" or ".mkv" or ".mov" or ".avi" or ".webm" => "\uE714",
                ".mp3" or ".wav" or ".flac" or ".m4a" or ".ogg" => "\uE8D6",
                ".zip" or ".rar" or ".7z" or ".tar" or ".gz" => "\uE7B8",
                ".exe" or ".msi" or ".bat" or ".cmd" or ".ps1" => "\uE756",
                ".cs" or ".js" or ".ts" or ".py" or ".json" or ".xml" or ".html" or ".css" or ".cpp" or ".sln" => "\uE943",
                ".xlsx" or ".xls" or ".csv" => "\uE80A",
                _ => "\uE8A5"
            };
        }

        private void Populate(DockWidgetContext ctx, StackPanel list, List<RecentItem> items)
        {
            var p = ctx.Palette;
            list.Children.Clear();
            if (items.Count == 0)
            {
                list.Children.Add(W.Text(p, ctx.T("Son açılan öğe yok", "No recent items"), 12.5, p.SecondaryForeground, ui: false));
                return;
            }
            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                var dp = new DockPanel();
                var icon = W.Glyph(p, GlyphFor(it.Name), 13, p.AccentBrush);
                icon.Margin = new Thickness(0, 0, 10, 0);
                DockPanel.SetDock(icon, Dock.Left);
                dp.Children.Add(icon);
                string ago = DockWidgetMath.FormatDuration(ctx.Now - it.Opened, ctx.Lang);
                var when = W.Text(p, ctx.T(ago + " önce", ago + " ago"), 11, p.TertiaryForeground, ui: false);
                when.Margin = new Thickness(8, 0, 0, 0);
                DockPanel.SetDock(when, Dock.Right);
                dp.Children.Add(when);
                dp.Children.Add(W.Text(p, it.Name, 12.5, ui: false));
                string path = it.Path;
                var row = Fx.ListRow(p, dp, () =>
                {
                    ctx.CloseCard();
                    DockWidgetActions.Run(ctx, path);
                });
                row.ToolTip = it.Name;
                list.Children.Add(row);
                Fx.StaggerIn(row, i, 40);
            }
        }
    }

    private static class RecycleNative
    {
        public const uint SHERB_NOCONFIRMATION = 0x1;
        public const uint SHERB_NOPROGRESSUI = 0x2;
        public const uint SHERB_NOSOUND = 0x4;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern int SHEmptyRecycleBin(nint hwnd, string? pszRootPath, uint dwFlags);
    }

    /// <summary>recycle-bin: item count and size; the card opens or empties the bin (with confirmation).</summary>
    private sealed class RecycleBinTile : DockWidgetTileBase
    {
        private static readonly RecycleBinSummary Sample = new(12, 348L * 1024 * 1024);
        private TextBlock _glyph = null!;
        private TextBlock _count = null!;
        private TextBlock _size = null!;
        private DispatcherTimer? _timer;
        private RecycleBinSummary? _summary;
        private bool _busy;
        private TextBlock? _cardCount;
        private TextBlock? _cardSize;
        private FrameworkElement? _actions;
        private FrameworkElement? _confirm;

        public RecycleBinTile(DockWidgetDescriptor d, DockWidgetContext c) : base(d, c) { }

        protected override FrameworkElement BuildContent(DockWidgetContext ctx)
        {
            var p = ctx.Palette;
            double h = ctx.TileHeight;
            _glyph = W.Glyph(p, "\uE74D", Math.Max(12, h * 0.46));
            _count = W.Text(p, "–", Math.Max(9, h * 0.3), weight: FontWeights.SemiBold);
            _size = W.Text(p, "", Math.Max(7.5, h * 0.22), p.SecondaryForeground);
            var col = W.Column(_count, _size);
            col.Margin = new Thickness(Math.Max(4, h * 0.16), 0, 0, 0);
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.Children.Add(_glyph);
            Grid.SetColumn(col, 1);
            g.Children.Add(col);
            return g;
        }

        public override void OnAttached(DockWidgetContext ctx)
        {
            _timer ??= new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(10) };
            _timer.Tick -= OnTick;
            _timer.Tick += OnTick;
            _timer.Tag = ctx;
            _timer.Start();
            _ = PollAsync(ctx);
        }

        public override void OnDetached() => _timer?.Stop();

        private void OnTick(object? sender, EventArgs e)
        {
            if (_timer?.Tag is DockWidgetContext ctx) _ = PollAsync(ctx);
        }

        private async Task PollAsync(DockWidgetContext ctx)
        {
            if (_busy) return;
            _busy = true;
            try
            {
                var s = ctx.IsAuditMode ? Sample : await Task.Run(DockAppsService.QueryRecycleBin);
                bool changed = _summary is { } old && old.ItemCount != s.ItemCount;
                _summary = s;
                if (changed) Fx.Wobble(_glyph);
                Refresh(ctx);
                if (ctx.IsCardOpenFor(Chip)) RefreshCard(ctx);
            }
            catch (Exception ex)
            {
                CrashLogger.Log("DockWidget", ex, "Recycle bin query failed.");
            }
            finally
            {
                _busy = false;
            }
        }

        protected override void Refresh(DockWidgetContext ctx)
        {
            if (_summary is not { } s)
            {
                if (ctx.IsAuditMode) { _summary = Sample; s = Sample; }
                else return;
            }
            if (s.ItemCount <= 0)
            {
                W.SetTextAnimated(_count, ctx.T("Boş", "Empty"));
                _size.Text = string.Empty;
                _size.Visibility = Visibility.Collapsed;
            }
            else
            {
                W.SetTextAnimated(_count, s.ItemCount.ToString(CultureInfo.InvariantCulture));
                _size.Visibility = Visibility.Visible;
                W.SetTextAnimated(_size, Fx.FormatBytes(s.SizeBytes));
            }
        }

        protected override FrameworkElement? CreateCard(DockWidgetContext ctx)
        {
            var p = ctx.Palette;
            _cardCount = W.Text(p, "", 12.5, weight: FontWeights.SemiBold);
            _cardSize = W.Text(p, "", 12.5, weight: FontWeights.SemiBold);
            var rows = W.Column(LabeledRow(p, ctx.T("Öğe", "Items"), _cardCount), LabeledRow(p, ctx.T("Boyut", "Size"), _cardSize));
            rows.Margin = new Thickness(0, 0, 0, 10);

            var actions = W.Row(0,
                W.Button(p, ctx.T("Aç", "Open"), () => { ctx.CloseCard(); DockWidgetActions.Run(ctx, "shell:RecycleBinFolder"); }, "\uE838"),
                W.Button(p, ctx.T("Boşalt", "Empty"), () => ShowConfirm(true), "\uE74D"));
            _actions = actions;

            var question = W.Text(p, ctx.T("Emin misin? Bu geri alınamaz.", "Are you sure? This can't be undone."), 12.5, W.Brush(p.Negative), FontWeights.SemiBold);
            question.Margin = new Thickness(0, 0, 0, 8);
            var confirm = W.Column(question, W.Row(0,
                W.Button(p, ctx.T("Evet, boşalt", "Yes, empty"), () => _ = EmptyAsync(ctx), "\uE74D", primary: true),
                W.Button(p, ctx.T("Hayır", "No"), () => ShowConfirm(false))));
            confirm.Visibility = Visibility.Collapsed;
            _confirm = confirm;
            return W.Card(p, Descriptor.Glyph, Descriptor.Name(ctx.Lang), W.Column(rows, actions, confirm), 280);
        }

        private static FrameworkElement LabeledRow(DockWidgetPalette p, string label, TextBlock value)
        {
            var dp = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
            DockPanel.SetDock(value, Dock.Right);
            dp.Children.Add(value);
            dp.Children.Add(W.Text(p, label, 12.5, p.SecondaryForeground, ui: false));
            return dp;
        }

        private void ShowConfirm(bool show)
        {
            if (_actions is null || _confirm is null) return;
            _actions.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
            _confirm.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            Fx.StaggerIn(show ? _confirm : _actions, 0, 0, 5);
        }

        private async Task EmptyAsync(DockWidgetContext ctx)
        {
            ShowConfirm(false);
            if (ctx.IsAuditMode) return;
            try
            {
                await Task.Run(() => RecycleNative.SHEmptyRecycleBin(0, null,
                    RecycleNative.SHERB_NOCONFIRMATION | RecycleNative.SHERB_NOPROGRESSUI | RecycleNative.SHERB_NOSOUND));
            }
            catch (Exception ex)
            {
                CrashLogger.Log("DockWidget", ex, "Emptying the recycle bin failed.");
            }
            await PollAsync(ctx);
        }

        protected override void RefreshCard(DockWidgetContext ctx)
        {
            if (_cardCount is null || _cardSize is null) return;
            if (_summary is { } s)
            {
                _cardCount.Text = s.ItemCount.ToString(CultureInfo.InvariantCulture);
                _cardSize.Text = Fx.FormatBytes(s.SizeBytes);
            }
            else
            {
                _cardCount.Text = "–";
                _cardSize.Text = "–";
            }
        }
    }

    /// <summary>search: a pill that opens an input; Enter opens a path or searches the web.</summary>
    private sealed class SearchTile : DockWidgetTileBase
    {
        private DropShadowEffect _glow = null!;
        private SolidColorBrush _border = null!;
        private Color _accent;

        public SearchTile(DockWidgetDescriptor d, DockWidgetContext c) : base(d, c) { }

        protected override Thickness ContentPadding(double h) => new(0);

        protected override FrameworkElement BuildContent(DockWidgetContext ctx)
        {
            var p = ctx.Palette;
            double h = ctx.TileHeight;
            _accent = p.Accent;
            _glow = new DropShadowEffect { Color = p.Accent, BlurRadius = 12, ShadowDepth = 0, Opacity = 0 };
            _border = new SolidColorBrush(Color.FromArgb(0, p.Accent.R, p.Accent.G, p.Accent.B));
            var glyph = W.Glyph(p, "\uE721", Math.Max(10, h * 0.36), p.SecondaryForeground);
            var hint = W.Text(p, ctx.T("Ara veya yol yaz", "Search or type a path"), Math.Max(8.5, h * 0.27), p.TertiaryForeground, ui: false);
            hint.Margin = new Thickness(Math.Max(4, h * 0.18), 0, 0, 0);
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.Children.Add(glyph);
            Grid.SetColumn(hint, 1);
            g.Children.Add(hint);
            var pill = new Border
            {
                CornerRadius = new CornerRadius(Math.Max(8, h * 0.28)),
                BorderBrush = _border,
                BorderThickness = new Thickness(1),
                Background = Brushes.Transparent,
                Padding = new Thickness(Math.Max(6, h * 0.28), 0, Math.Max(6, h * 0.22), 0),
                Effect = _glow,
                Child = g
            };
            pill.MouseEnter += (_, _) => Glow(true);
            pill.MouseLeave += (_, _) => Glow(false);
            return pill;
        }

        private void Glow(bool on)
        {
            var dur = TimeSpan.FromMilliseconds(220);
            _glow.BeginAnimation(DropShadowEffect.OpacityProperty, new DoubleAnimation(on ? 0.6 : 0, dur));
            _border.BeginAnimation(SolidColorBrush.ColorProperty,
                new ColorAnimation(Color.FromArgb(on ? (byte)0xA0 : (byte)0, _accent.R, _accent.G, _accent.B), dur));
        }

        protected override void Refresh(DockWidgetContext ctx) { }

        protected override FrameworkElement? CreateCard(DockWidgetContext ctx)
        {
            var p = ctx.Palette;
            var input = W.Input(p, ctx.T("Web'de ara veya klasör yolu yaz…", "Search the web or type a folder path…"), 296);
            var hint = W.Text(p, ctx.T("Enter: yol varsa açar, yoksa Google'da arar", "Enter opens a path if it exists, otherwise searches Google"),
                11.5, p.TertiaryForeground, ui: false);
            hint.Margin = new Thickness(0, 8, 0, 0);
            input.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape)
                {
                    e.Handled = true;
                    ctx.CloseCard();
                    return;
                }
                if (e.Key != Key.Enter) return;
                e.Handled = true;
                string text = input.Text.Trim();
                if (text.Length == 0) return;
                ctx.CloseCard();
                Go(ctx, text);
            };
            Fx.AnimateInOnLoad(input);
            Fx.FocusInput(input, !ctx.IsAuditMode);
            return W.Card(p, Descriptor.Glyph, Descriptor.Name(ctx.Lang), W.Column(input, hint), 296);
        }

        private static void Go(DockWidgetContext ctx, string text)
        {
            string candidate = Environment.ExpandEnvironmentVariables(text.Trim('"'));
            bool isPath = false;
            try
            {
                isPath = candidate.IndexOfAny(IOPath.GetInvalidPathChars()) < 0 &&
                         (System.IO.Directory.Exists(candidate) || System.IO.File.Exists(candidate));
            }
            catch
            {
                isPath = false;
            }
            if (isPath) DockWidgetActions.Run(ctx, candidate);
            else DockWidgetActions.OpenUrl(ctx, "https://www.google.com/search?q=" + Uri.EscapeDataString(text));
        }
    }

    /// <summary>commands: a launcher input showing the top 6 command palette results.</summary>
    private sealed class CommandsTile : DockWidgetTileBase
    {
        private TextBox? _input;
        private StackPanel? _results;
        private DispatcherTimer? _debounce;
        private CancellationTokenSource? _cts;
        private IReadOnlyList<CommandEntry> _entries = [];
        private int _selected;
        private bool _executing;

        public CommandsTile(DockWidgetDescriptor d, DockWidgetContext c) : base(d, c) { }

        protected override FrameworkElement BuildContent(DockWidgetContext ctx) =>
            W.Glyph(ctx.Palette, "\uE756", Math.Max(12, ctx.TileHeight * 0.46));

        protected override void Refresh(DockWidgetContext ctx) { }

        protected override FrameworkElement? CreateCard(DockWidgetContext ctx)
        {
            var p = ctx.Palette;
            _cts?.Cancel();
            _entries = [];
            _selected = 0;
            var input = W.Input(p, ctx.T("Uygulama veya komut yaz…", "Type an app or a command…"), 300);
            var results = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            _input = input;
            _results = results;
            _debounce?.Stop();
            _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
            _debounce.Tick += (_, _) =>
            {
                _debounce?.Stop();
                _ = QueryAsync(ctx, input.Text);
            };
            input.TextChanged += (_, _) =>
            {
                _debounce?.Stop();
                _debounce?.Start();
            };
            input.PreviewKeyDown += (_, e) =>
            {
                switch (e.Key)
                {
                    case Key.Down:
                        e.Handled = true;
                        Select(ctx, Math.Min(_selected + 1, _entries.Count - 1));
                        break;
                    case Key.Up:
                        e.Handled = true;
                        Select(ctx, Math.Max(_selected - 1, 0));
                        break;
                    case Key.Enter:
                        e.Handled = true;
                        if (_entries.Count > 0) _ = ExecuteAsync(ctx, _entries[Math.Clamp(_selected, 0, _entries.Count - 1)]);
                        break;
                    case Key.Escape:
                        e.Handled = true;
                        ctx.CloseCard();
                        break;
                }
            };
            results.Children.Add(W.Text(p, ctx.T("Yazmaya başla", "Start typing"), 12, p.TertiaryForeground, ui: false));
            Fx.AnimateInOnLoad(input);
            Fx.FocusInput(input, !ctx.IsAuditMode);
            return W.Card(p, Descriptor.Glyph, Descriptor.Name(ctx.Lang), W.Column(input, results), 300);
        }

        private static IReadOnlyList<CommandEntry> AuditSamples(DockWidgetContext ctx)
        {
            static Task<CommandExecutionResult> Noop(CancellationToken _) => Task.FromResult(new CommandExecutionResult(true));
            return
            [
                new CommandEntry("audit:term", "Terminal", ctx.T("Uygulama", "App"), "app", null, 100, Noop, "\uE756"),
                new CommandEntry("audit:code", "Visual Studio Code", ctx.T("Uygulama", "App"), "app", null, 90, Noop, "\uE943"),
                new CommandEntry("audit:calc", ctx.T("Hesap Makinesi", "Calculator"), ctx.T("Uygulama", "App"), "app", null, 80, Noop, "\uE8EF"),
                new CommandEntry("audit:lock", ctx.T("Ekranı kilitle", "Lock screen"), ctx.T("Eylem", "Action"), "action", "Win+L", 70, Noop, "\uE72E")
            ];
        }

        private async Task QueryAsync(DockWidgetContext ctx, string text)
        {
            _cts?.Cancel();
            var cts = new CancellationTokenSource();
            _cts = cts;
            var results = _results;
            if (results is null) return;
            if (string.IsNullOrWhiteSpace(text))
            {
                _entries = [];
                Render(ctx, results);
                return;
            }
            try
            {
                IReadOnlyList<CommandEntry> found = ctx.IsAuditMode
                    ? AuditSamples(ctx).Where(c => c.Title.Contains(text.Trim(), StringComparison.CurrentCultureIgnoreCase)).ToList()
                    : await ctx.Orch.Launcher.QueryAsync(text, cts.Token);
                if (cts.IsCancellationRequested || !ReferenceEquals(results, _results)) return;
                _entries = found.Take(6).ToList();
                _selected = 0;
                Render(ctx, results);
            }
            catch (OperationCanceledException)
            {
                // A newer query replaced this one.
            }
            catch (Exception ex)
            {
                CrashLogger.Log("DockWidget", ex, "Command query failed.");
            }
        }

        private void Render(DockWidgetContext ctx, StackPanel results)
        {
            var p = ctx.Palette;
            results.Children.Clear();
            if (_entries.Count == 0)
            {
                string msg = string.IsNullOrWhiteSpace(_input?.Text) ? ctx.T("Yazmaya başla", "Start typing") : ctx.T("Sonuç yok", "No results");
                results.Children.Add(W.Text(p, msg, 12, p.TertiaryForeground, ui: false));
                return;
            }
            for (int i = 0; i < _entries.Count; i++)
            {
                var entry = _entries[i];
                var dp = new DockPanel();
                var icon = W.Glyph(p, string.IsNullOrEmpty(entry.IconGlyph) ? "\uE7C3" : entry.IconGlyph, 14, p.AccentBrush);
                icon.Margin = new Thickness(0, 0, 10, 0);
                DockPanel.SetDock(icon, Dock.Left);
                dp.Children.Add(icon);
                if (!string.IsNullOrEmpty(entry.ShortcutHint))
                {
                    var sc = W.Text(p, entry.ShortcutHint, 10.5, p.TertiaryForeground, mono: true);
                    sc.Margin = new Thickness(8, 0, 0, 0);
                    DockPanel.SetDock(sc, Dock.Right);
                    dp.Children.Add(sc);
                }
                var text = W.Column(W.Text(p, entry.Title, 12.5, weight: FontWeights.SemiBold),
                    W.Text(p, entry.Subtitle, 11, p.SecondaryForeground, ui: false));
                dp.Children.Add(text);
                var row = Fx.ListRow(p, dp, () => _ = ExecuteAsync(ctx, entry));
                results.Children.Add(row);
                Fx.StaggerIn(row, i, 30, 4);
            }
            Select(ctx, 0);
        }

        private void Select(DockWidgetContext ctx, int index)
        {
            if (_results is null || _entries.Count == 0 || index < 0) return;
            _selected = index;
            for (int i = 0; i < _results.Children.Count; i++)
            {
                if (_results.Children[i] is Border b)
                    b.Background = i == index ? ctx.Palette.CardRowBackground : Brushes.Transparent;
            }
        }

        private async Task ExecuteAsync(DockWidgetContext ctx, CommandEntry entry)
        {
            if (_executing) return;
            _executing = true;
            try
            {
                ctx.CloseCard();
                if (!ctx.IsAuditMode) await entry.ExecuteAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                CrashLogger.Log("DockWidget", ex, $"Command '{entry.Id}' failed.");
            }
            finally
            {
                _executing = false;
            }
        }

        public override void OnDetached()
        {
            _debounce?.Stop();
            _cts?.Cancel();
        }
    }

    private sealed record DriveRow(string Name, string Label, long Free, long Total)
    {
        public double UsedFraction => Total > 0 ? Math.Clamp(1 - (double)Free / Total, 0, 1) : 0;
    }

    /// <summary>storage: free space on the system drive with a usage bar; the card lists every fixed drive.</summary>
    private sealed class StorageTile : DockWidgetTileBase
    {
        private static readonly IReadOnlyList<DriveRow> Sample =
        [
            new("C:\\", "Windows", 128L << 30, 512L << 30),
            new("D:\\", "Veri", 412L << 30, 931L << 30)
        ];

        private TextBlock _text = null!;
        private BarGauge _bar = null!;
        private IReadOnlyList<DriveRow> _drives = [];
        private DateTime _nextQuery = DateTime.MinValue;
        private bool _busy;

        public StorageTile(DockWidgetDescriptor d, DockWidgetContext c) : base(d, c) { }

        protected override FrameworkElement BuildContent(DockWidgetContext ctx)
        {
            var p = ctx.Palette;
            double h = ctx.TileHeight;
            _text = W.Text(p, "–", Math.Max(8.5, h * 0.27), weight: FontWeights.SemiBold);
            _bar = W.Bar(p, 10, Math.Max(3, h * 0.12), p.Accent);
            _bar.Width = double.NaN;
            _bar.HorizontalAlignment = HorizontalAlignment.Stretch;
            _bar.Margin = new Thickness(0, Math.Max(2, h * 0.08), 0, 0);
            var col = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var glyph = W.Glyph(p, "\uEDA2", Math.Max(9, h * 0.3), p.SecondaryForeground);
            glyph.Margin = new Thickness(0, 0, 4, 0);
            var top = new DockPanel();
            DockPanel.SetDock(glyph, Dock.Left);
            top.Children.Add(glyph);
            top.Children.Add(_text);
            col.Children.Add(top);
            col.Children.Add(_bar);
            return col;
        }

        private static IReadOnlyList<DriveRow> Query()
        {
            string sysRoot = IOPath.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
            var rows = new List<DriveRow>();
            foreach (var d in System.IO.DriveInfo.GetDrives())
            {
                try
                {
                    if (d.DriveType != System.IO.DriveType.Fixed || !d.IsReady) continue;
                    rows.Add(new DriveRow(d.Name, d.VolumeLabel, d.AvailableFreeSpace, d.TotalSize));
                }
                catch
                {
                    // Drive went away or is locked; skip it.
                }
            }
            return rows.OrderBy(r => string.Equals(r.Name, sysRoot, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private async Task PollAsync(DockWidgetContext ctx)
        {
            if (_busy) return;
            _busy = true;
            try
            {
                _drives = ctx.IsAuditMode ? Sample : await Task.Run(Query);
                Apply(ctx);
            }
            catch (Exception ex)
            {
                CrashLogger.Log("DockWidget", ex, "Drive query failed.");
            }
            finally
            {
                _busy = false;
            }
        }

        private static Color BarColor(DockWidgetPalette p, double used) =>
            used >= 0.95 ? p.Negative : used > 0.85 ? p.Warning : p.Accent;

        private void Apply(DockWidgetContext ctx)
        {
            if (_drives.Count == 0)
            {
                W.SetTextAnimated(_text, "–");
                _bar.AnimateTo(0);
                return;
            }
            var d = _drives[0];
            W.SetTextAnimated(_text, ctx.T($"{Fx.FormatBytes(d.Free)} boş", $"{Fx.FormatBytes(d.Free)} free"));
            Fx.SetFill(_bar, BarColor(ctx.Palette, d.UsedFraction));
            _bar.AnimateTo(d.UsedFraction, 700);
        }

        protected override void Refresh(DockWidgetContext ctx)
        {
            var now = DateTime.Now;
            if (now < _nextQuery) return;
            _nextQuery = now.AddSeconds(30);
            _ = PollAsync(ctx);
        }

        protected override FrameworkElement? CreateCard(DockWidgetContext ctx)
        {
            var p = ctx.Palette;
            const double width = 290;
            var list = new StackPanel();
            if (_drives.Count == 0)
            {
                list.Children.Add(W.Text(p, ctx.T("Sürücü bulunamadı", "No drives found"), 12.5, p.SecondaryForeground, ui: false));
            }
            int i = 0;
            foreach (var d in _drives)
            {
                var dp = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
                var right = W.Text(p, ctx.T($"{Fx.FormatBytes(d.Free)} boş / {Fx.FormatBytes(d.Total)}", $"{Fx.FormatBytes(d.Free)} free / {Fx.FormatBytes(d.Total)}"),
                    11.5, p.SecondaryForeground, ui: false);
                DockPanel.SetDock(right, Dock.Right);
                dp.Children.Add(right);
                string name = d.Name.TrimEnd('\\') + (string.IsNullOrWhiteSpace(d.Label) ? "" : " · " + d.Label);
                dp.Children.Add(W.Text(p, name, 12.5, weight: FontWeights.SemiBold));
                var bar = W.Bar(p, width, 6, BarColor(p, d.UsedFraction));
                double used = d.UsedFraction;
                bar.Loaded += (_, _) => bar.AnimateTo(used, 800);
                var block = W.Column(dp, bar);
                block.Margin = new Thickness(0, 0, 0, 12);
                list.Children.Add(block);
                Fx.StaggerIn(block, i++, 60);
            }
            return W.Card(p, Descriptor.Glyph, Descriptor.Name(ctx.Lang), list, width,
                W.Button(p, "", () => { ctx.CloseCard(); DockWidgetActions.OpenSettings(ctx, "ms-settings:storagesense"); }, "\uE713"));
        }
    }

    /// <summary>screenshot: click starts a region snip with a shutter flash; the corner chevron opens more options.</summary>
    private sealed class ScreenshotTile : DockWidgetTileBase
    {
        private Border _flash = null!;
        private TextBlock _glyph = null!;

        public ScreenshotTile(DockWidgetDescriptor d, DockWidgetContext c) : base(d, c) { }

        protected override FrameworkElement BuildContent(DockWidgetContext ctx)
        {
            var p = ctx.Palette;
            double h = ctx.TileHeight;
            _glyph = W.Glyph(p, "\uE722", Math.Max(12, h * 0.46));
            _flash = new Border
            {
                Background = Brushes.White,
                CornerRadius = new CornerRadius(Math.Max(6, h * 0.24)),
                Opacity = 0,
                IsHitTestVisible = false
            };
            double cs = Math.Max(10, h * 0.36);
            var chevron = new Border
            {
                Width = cs,
                Height = cs,
                CornerRadius = new CornerRadius(cs / 2),
                Background = p.CardRowBackground,
                Child = W.Glyph(p, "\uE70E", cs * 0.55, p.SecondaryForeground),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, -1, -1, 0),
                Cursor = Cursors.Hand,
                ToolTip = ctx.T("Diğer seçenekler", "More options")
            };
            chevron.MouseLeftButtonDown += (_, e) => e.Handled = true;
            chevron.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                if (ctx.IsCardOpenFor(Chip)) ctx.CloseCard();
                else ctx.ShowCard(Chip, BuildOptions(ctx));
            };
            var g = new Grid();
            g.Children.Add(_glyph);
            g.Children.Add(_flash);
            g.Children.Add(chevron);
            return g;
        }

        protected override void Refresh(DockWidgetContext ctx) { }

        private void Shutter()
        {
            Fx.Flash(_flash, Colors.White, 0.95, 420);
            W.Pulse(_glyph, 0.8);
        }

        public override bool OnPrimaryClick(DockWidgetContext ctx)
        {
            Shutter();
            Fx.Safe(ctx, ctx.Orch.Connectivity.LaunchScreenSnip, "Screen snip failed.");
            return true;
        }

        private FrameworkElement BuildOptions(DockWidgetContext ctx)
        {
            var p = ctx.Palette;
            void Do(Action a)
            {
                ctx.CloseCard();
                Shutter();
                a();
            }
            FrameworkElement Btn(string tr, string en, string glyph, Action a)
            {
                var b = W.Button(p, ctx.T(tr, en), () => Do(a), glyph);
                b.HorizontalAlignment = HorizontalAlignment.Stretch;
                b.Margin = new Thickness(0, 0, 0, 6);
                ((StackPanel)b.Child).HorizontalAlignment = HorizontalAlignment.Left;
                return b;
            }
            var list = W.Column(
                Btn("Bölge", "Region", "\uF406", () => DockWidgetActions.Run(ctx, "ms-screenclip:")),
                Btn("Pencere", "Window", "\uE737", () => DockWidgetActions.Run(ctx, "ms-screenclip:?clippingMode=Window")),
                Btn("Tam ekran", "Full screen", "\uE740", () => DockWidgetActions.SendChord(ctx, 0x5B, 0x2C)),
                Btn("Ekran görüntüleri klasörü", "Screenshots folder", "\uE838", () =>
                {
                    string dir = IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");
                    DockWidgetActions.Run(ctx, System.IO.Directory.Exists(dir) ? dir : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
                }));
            for (int i = 0; i < list.Children.Count; i++) Fx.StaggerIn((FrameworkElement)list.Children[i], i, 40);
            return W.Card(p, Descriptor.Glyph, Descriptor.Name(ctx.Lang), list, 240);
        }
    }

    // =============================================================================================
    // Finance
    // =============================================================================================

    private static class FinanceSamples
    {
        public static ImmutableArray<double> Line(double price, double changePct, int count, int seed)
        {
            double start = price / (1 + changePct / 100.0);
            var b = ImmutableArray.CreateBuilder<double>(count);
            for (int i = 0; i < count; i++)
            {
                double t = i / (double)(count - 1);
                double wobble = Math.Sin(i * 0.37 + seed) * 0.006 + Math.Sin(i * 0.11 + seed * 2) * 0.009;
                b.Add((start + (price - start) * t) * (1 + wobble * (1 - t * 0.9)));
            }
            b[count - 1] = price;
            return b.MoveToImmutable();
        }

        public static CryptoSnapshot Crypto(string symbol)
        {
            string s = symbol.ToUpperInvariant();
            (double price, double chg) = s switch
            {
                "ETHUSDT" => (3412.50, 1.6),
                "SOLUSDT" => (148.32, -0.9),
                "BNBUSDT" => (588.10, 0.4),
                "XRPUSDT" => (0.5231, -1.2),
                _ => (64210.0, 2.4)
            };
            return new CryptoSnapshot(s, price, chg, Line(price, chg, 96, s.Length), DateTime.Now);
        }

        public static IReadOnlyList<StockQuote> Stocks(IReadOnlyList<string> symbols)
        {
            var known = new Dictionary<string, (double, double)>(StringComparer.OrdinalIgnoreCase)
            {
                ["AAPL"] = (228.40, 0.8),
                ["MSFT"] = (431.20, -0.3),
                ["NVDA"] = (138.70, 1.9),
                ["GOOGL"] = (176.50, 0.5),
                ["TSLA"] = (251.10, -2.1)
            };
            var list = new List<StockQuote>();
            int seed = 1;
            IReadOnlyList<string> source = symbols.Count > 0 ? symbols : new[] { "AAPL", "MSFT", "NVDA" };
            foreach (var sym in source)
            {
                var (price, chg) = known.TryGetValue(sym, out var v) ? v : (100.0 + seed * 17.3, seed % 2 == 0 ? -0.6 : 1.1);
                double prev = price / (1 + chg / 100.0);
                list.Add(new StockQuote(sym.ToUpperInvariant(), price, chg, prev, Line(price, chg, 40, seed++)));
            }
            return list;
        }

        public static string CryptoBase(string symbol) =>
            symbol.EndsWith("USDT", StringComparison.OrdinalIgnoreCase) && symbol.Length > 4 ? symbol[..^4].ToUpperInvariant() : symbol.ToUpperInvariant();
    }

    private sealed record QuoteView(string Symbol, double Price, double ChangePct, IReadOnlyList<double> Line, double? PreviousClose);

    /// <summary>crypto / stock-chart: symbol, ticking price, coloured change and a sparkline.</summary>
    private sealed class QuoteTile : DockWidgetTileBase
    {
        private readonly bool _isCrypto;
        private TextBlock _symbol = null!;
        private TextBlock _price = null!;
        private TextBlock _change = null!;
        private Sparkline _spark = null!;
        private Border _flash = null!;
        private double _lastPrice = double.NaN;
        private IReadOnlyList<double>? _lastLine;
        private Sparkline? _cardChart;
        private StackPanel? _cardRows;
        private TextBlock? _cardStatus;

        public QuoteTile(DockWidgetDescriptor d, DockWidgetContext c, bool isCrypto) : base(d, c)
        {
            _isCrypto = isCrypto;
            Refresh(c);
        }

        protected override FrameworkElement BuildContent(DockWidgetContext ctx)
        {
            var p = ctx.Palette;
            double h = ctx.TileHeight;
            // Counter layout: app square, big price over "BTC  +2.4%", and the day's line on the right.
            var square = Vivid.AppSquare(p, ServiceColors.For(Descriptor.Id == "crypto" ? "crypto" : "stock"), Descriptor.Glyph, Math.Round(h * 0.78));
            square.Margin = new Thickness(0, 0, Math.Round(h * 0.2), 0);
            _symbol = Vivid.Label(p, "", h);
            _price = Vivid.Value(p, "–", h);
            _price.TextTrimming = TextTrimming.None;
            _change = Vivid.Label(p, "", h);
            _change.FontWeight = FontWeights.Bold;
            _change.Margin = new Thickness(Math.Round(h * 0.12), 0, 0, 0);
            _spark = new Sparkline { Height = Math.Max(8, Math.Round(h * 0.5)), StrokeThickness = Math.Max(1.2, h * 0.045), Stroke = p.AccentBrush, ShowHead = true, VerticalAlignment = VerticalAlignment.Center };
            _spark.Margin = new Thickness(Math.Round(h * 0.16), 0, 0, 0);
            _flash = new Border { CornerRadius = new CornerRadius(Math.Max(6, h * 0.22)), Opacity = 0, IsHitTestVisible = false, Margin = new Thickness(-Math.Round(h * 0.1), -1, -Math.Round(h * 0.1), -1) };

            var labelRow = new StackPanel { Orientation = Orientation.Horizontal };
            labelRow.Children.Add(_symbol);
            labelRow.Children.Add(_change);
            var left = W.Column(_price, labelRow);
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumnSpan(_flash, 3);
            g.Children.Add(_flash);
            g.Children.Add(square);
            Grid.SetColumn(left, 1);
            g.Children.Add(left);
            Grid.SetColumn(_spark, 2);
            g.Children.Add(_spark);
            return g;
        }

        private (QuoteView? Quote, string? Status, string FallbackSymbol) Read(DockWidgetContext ctx)
        {
            if (_isCrypto)
            {
                var c = ctx.Data.Crypto;
                if (c is null && ctx.IsAuditMode) c = FinanceSamples.Crypto(ctx.Config.CryptoSymbol);
                string fb = FinanceSamples.CryptoBase(ctx.Config.CryptoSymbol);
                return c is null
                    ? (null, ctx.Data.CryptoStatus, fb)
                    : (new QuoteView(FinanceSamples.CryptoBase(c.Symbol), c.Price, c.ChangePercent, c.Line, null), ctx.Data.CryptoStatus, fb);
            }
            var stocks = ctx.Data.Stocks;
            if (stocks.Count == 0 && ctx.IsAuditMode) stocks = FinanceSamples.Stocks(ctx.Config.StockSymbols);
            string fallback = ctx.Config.StockSymbols.FirstOrDefault() ?? "AAPL";
            if (stocks.Count == 0) return (null, ctx.Data.StocksStatus, fallback);
            var s = stocks[0];
            return (new QuoteView(s.Symbol, s.Price, s.ChangePercent, s.Line, s.PreviousClose), ctx.Data.StocksStatus, fallback);
        }

        protected override void Refresh(DockWidgetContext ctx)
        {
            if (_price is null) return;
            var p = ctx.Palette;
            var (q, status, fb) = Read(ctx);
            if (q is null)
            {
                W.SetTextAnimated(_symbol, fb);
                W.SetTextAnimated(_price, "–");
                _change.Text = string.IsNullOrWhiteSpace(status) ? ctx.T("Bekleniyor", "Waiting") : status;
                _change.ToolTip = status;
                Fx.SetForeground(_change, (p.SecondaryForeground as SolidColorBrush)?.Color ?? p.Accent);
                _spark.Visibility = Visibility.Collapsed;
                return;
            }
            _spark.Visibility = Visibility.Visible;
            _change.ToolTip = null;
            var color = q.ChangePct >= 0 ? p.Positive : p.Negative;
            W.SetTextAnimated(_symbol, q.Symbol);
            W.SetTextAnimated(_price, DockWidgetMath.FormatPrice(q.Price));
            W.SetTextAnimated(_change, Fx.FormatPercent(q.ChangePct));
            Fx.SetForeground(_change, color);
            if (double.IsFinite(_lastPrice) && Math.Abs(q.Price - _lastPrice) > 1e-12)
            {
                Fx.Flash(_flash, q.Price > _lastPrice ? p.Positive : p.Negative, 0.28);
            }
            _lastPrice = q.Price;
            bool strokeChanged = _spark.Stroke is not SolidColorBrush sb || sb.Color != color;
            if (strokeChanged) _spark.Stroke = W.Brush(color);
            if (!ReferenceEquals(q.Line, _lastLine))
            {
                _lastLine = q.Line;
                _spark.SetValues(q.Line);
            }
            else if (strokeChanged)
            {
                _spark.InvalidateVisual();
            }
        }

        protected override FrameworkElement? CreateCard(DockWidgetContext ctx)
        {
            var p = ctx.Palette;
            _cardChart = new Sparkline { Height = 90, StrokeThickness = 1.8, Stroke = p.AccentBrush, Margin = new Thickness(0, 2, 0, 10) };
            _cardRows = new StackPanel();
            _cardStatus = W.Text(p, "", 11.5, p.TertiaryForeground, ui: false);
            _cardStatus.TextWrapping = TextWrapping.Wrap;
            _cardStatus.TextTrimming = TextTrimming.None;
            _cardStatus.Margin = new Thickness(0, 6, 0, 0);
            var body = W.Column(_cardChart, _cardRows, _cardStatus);

            if (_isCrypto)
            {
                var chips = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
                string current = ctx.Config.CryptoSymbol;
                foreach (var sym in new[] { "BTC", "ETH", "SOL", "BNB", "XRP" })
                {
                    string full = sym + "USDT";
                    bool active = string.Equals(full, current, StringComparison.OrdinalIgnoreCase);
                    chips.Children.Add(W.Button(p, sym, () =>
                    {
                        if (string.Equals(ctx.Config.CryptoSymbol, full, StringComparison.OrdinalIgnoreCase)) return;
                        ctx.EditConfig(c => c.CryptoSymbol = full);
                        ctx.Data.InvalidateNetworkData();
                        _lastPrice = double.NaN;
                        _lastLine = null;
                        Refresh(ctx);
                        ReopenCard(ctx);
                    }, primary: active));
                }
                body.Children.Add(chips);
            }
            return W.Card(p, Descriptor.Glyph, Descriptor.Name(ctx.Lang), body, 290);
        }

        protected override void RefreshCard(DockWidgetContext ctx)
        {
            if (_cardChart is null || _cardRows is null || _cardStatus is null) return;
            var p = ctx.Palette;
            var (q, status, fb) = Read(ctx);
            _cardRows.Children.Clear();
            if (q is null)
            {
                _cardChart.Visibility = Visibility.Collapsed;
                _cardRows.Children.Add(W.CardRow(p, fb, "–"));
            }
            else
            {
                var color = q.ChangePct >= 0 ? p.Positive : p.Negative;
                _cardChart.Visibility = Visibility.Visible;
                if (_cardChart.Stroke is not SolidColorBrush sb || sb.Color != color) _cardChart.Stroke = W.Brush(color);
                _cardChart.SetValues(q.Line);
                var brush = W.Brush(color);
                _cardRows.Children.Add(W.CardRow(p, q.Symbol, DockWidgetMath.FormatPrice(q.Price)));
                _cardRows.Children.Add(W.CardRow(p, _isCrypto ? ctx.T("24 sa değişim", "24h change") : ctx.T("Günlük değişim", "Day change"), Fx.FormatPercent(q.ChangePct), brush));
                if (q.Line.Count > 1)
                {
                    _cardRows.Children.Add(W.CardRow(p, ctx.T("En yüksek", "High"), DockWidgetMath.FormatPrice(q.Line.Max())));
                    _cardRows.Children.Add(W.CardRow(p, ctx.T("En düşük", "Low"), DockWidgetMath.FormatPrice(q.Line.Min())));
                }
                if (q.PreviousClose is double pc && pc > 0)
                    _cardRows.Children.Add(W.CardRow(p, ctx.T("Önceki kapanış", "Previous close"), DockWidgetMath.FormatPrice(pc)));
            }
            string note = _isCrypto
                ? ctx.T("Veriler Binance'ten, dakikada bir yenilenir.", "Data from Binance, refreshed every minute.")
                : ctx.T("Finnhub API anahtarı Ayarlar → Dock widget'ları bölümünden girilir.", "Set the Finnhub API key in Settings → Dock widgets.");
            _cardStatus.Text = string.IsNullOrWhiteSpace(status) ? note : status + "\n" + note;
        }
    }

    /// <summary>stock-watchlist: three tickers in columns with ticking values.</summary>
    private sealed class WatchlistTile : DockWidgetTileBase
    {
        private readonly TextBlock[] _sym = new TextBlock[3];
        private readonly TextBlock[] _price = new TextBlock[3];
        private readonly TextBlock[] _pct = new TextBlock[3];
        private readonly FrameworkElement[] _cols = new FrameworkElement[3];
        private TextBlock _status = null!;
        private Grid _grid = null!;
        private bool _hadQuotes;

        public WatchlistTile(DockWidgetDescriptor d, DockWidgetContext c) : base(d, c) { }

        protected override FrameworkElement BuildContent(DockWidgetContext ctx)
        {
            var p = ctx.Palette;
            double h = ctx.TileHeight;
            _grid = new Grid();
            for (int i = 0; i < 3; i++)
            {
                _grid.ColumnDefinitions.Add(new ColumnDefinition());
                _sym[i] = W.Text(p, "", Math.Max(7.5, h * 0.21), p.SecondaryForeground, FontWeights.SemiBold);
                _pct[i] = W.Text(p, "", Math.Max(7.5, h * 0.21), p.SecondaryForeground, FontWeights.SemiBold);
                _pct[i].Margin = new Thickness(3, 0, 0, 0);
                _price[i] = W.Text(p, "", Math.Max(8.5, h * 0.28), weight: FontWeights.SemiBold);
                var top = new StackPanel { Orientation = Orientation.Horizontal };
                top.Children.Add(_sym[i]);
                top.Children.Add(_pct[i]);
                var col = W.Column(top, _price[i]);
                col.Margin = new Thickness(i == 0 ? 0 : 3, 0, 0, 0);
                col.ClipToBounds = true;
                _cols[i] = col;
                Grid.SetColumn(col, i);
                _grid.Children.Add(col);
            }
            _status = W.Text(p, "", Math.Max(8, h * 0.24), p.SecondaryForeground, ui: false);
            _status.Visibility = Visibility.Collapsed;
            Grid.SetColumnSpan(_status, 3);
            _grid.Children.Add(_status);
            return _grid;
        }

        private static IReadOnlyList<StockQuote> Quotes(DockWidgetContext ctx)
        {
            var s = ctx.Data.Stocks;
            if (s.Count == 0 && ctx.IsAuditMode) s = FinanceSamples.Stocks(ctx.Config.StockSymbols);
            return s;
        }

        protected override void Refresh(DockWidgetContext ctx)
        {
            var p = ctx.Palette;
            var quotes = Quotes(ctx);
            bool has = quotes.Count > 0;
            if (!has)
            {
                foreach (var c in _cols) c.Visibility = Visibility.Collapsed;
                _status.Visibility = Visibility.Visible;
                _status.Text = string.IsNullOrWhiteSpace(ctx.Data.StocksStatus) ? ctx.T("Fiyatlar bekleniyor…", "Waiting for quotes…") : ctx.Data.StocksStatus;
                _status.ToolTip = _status.Text;
                _hadQuotes = false;
                return;
            }
            _status.Visibility = Visibility.Collapsed;
            for (int i = 0; i < 3; i++)
            {
                if (i >= quotes.Count)
                {
                    _cols[i].Visibility = Visibility.Collapsed;
                    continue;
                }
                var q = quotes[i];
                _cols[i].Visibility = Visibility.Visible;
                W.SetTextAnimated(_sym[i], q.Symbol);
                W.SetTextAnimated(_price[i], DockWidgetMath.FormatPrice(q.Price));
                W.SetTextAnimated(_pct[i], Fx.FormatPercent(q.ChangePercent));
                Fx.SetForeground(_pct[i], q.ChangePercent >= 0 ? p.Positive : p.Negative);
            }
            if (!_hadQuotes)
            {
                _hadQuotes = true;
                for (int i = 0; i < 3; i++) Fx.StaggerIn(_cols[i], i, 90);
            }
        }

        public override void OnAttached(DockWidgetContext ctx)
        {
            if (_hadQuotes)
                for (int i = 0; i < 3; i++) Fx.StaggerIn(_cols[i], i, 90);
        }

        protected override FrameworkElement? CreateCard(DockWidgetContext ctx)
        {
            var p = ctx.Palette;
            var list = new StackPanel();
            var quotes = Quotes(ctx);
            if (quotes.Count == 0)
            {
                list.Children.Add(W.Text(p, ctx.Data.StocksStatus ?? ctx.T("Fiyatlar bekleniyor…", "Waiting for quotes…"), 12.5, p.SecondaryForeground, ui: false));
            }
            int i = 0;
            foreach (var q in quotes)
            {
                var row = W.CardRow(p, q.Symbol + "  " + DockWidgetMath.FormatPrice(q.Price), Fx.FormatPercent(q.ChangePercent),
                    W.Brush(q.ChangePercent >= 0 ? p.Positive : p.Negative));
                list.Children.Add(row);
                Fx.StaggerIn(row, i++, 40);
            }
            var note = W.Text(p, ctx.T("Hisseler ve Finnhub API anahtarı Ayarlar → Dock widget'ları bölümünden ayarlanır.",
                "Tickers and the Finnhub API key are set in Settings → Dock widgets."), 11.5, p.TertiaryForeground, ui: false);
            note.TextWrapping = TextWrapping.Wrap;
            note.TextTrimming = TextTrimming.None;
            note.Margin = new Thickness(0, 8, 0, 0);
            list.Children.Add(note);
            return W.Card(p, Descriptor.Glyph, Descriptor.Name(ctx.Lang), list, 280);
        }
    }
}
