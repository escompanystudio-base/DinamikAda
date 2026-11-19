using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using DinamikAda.App.Services.DockWidgets;
using DinamikAda.Core.Accounts;
using DinamikAda.Core.Widgets;

namespace DinamikAda.App.Views.DockWidgets.Accounts;

/// <summary>
/// Social, mail, calendar, AI and app account widgets: youtube-channel, leetcode-stats, gmail-unread, gcal-next,
/// deepseek-balance (reference implementation) and unsplash-wallpaper.
/// </summary>
public static class SocialMailAccountTiles
{
    public static void RegisterFetchers(Action<string, string, AccountFetcher> add)
    {
        add("deepseek-balance", "deepseek", FetchDeepSeekAsync);
        add("youtube-channel", "youtube", FetchYouTubeAsync);
        add("leetcode-stats", "leetcode", FetchLeetCodeAsync);
        add("gmail-unread", "gmail", FetchGmailAsync);
        add("gcal-next", "gcal", FetchCalendarAsync);
        add("unsplash-wallpaper", "unsplash", FetchUnsplashAsync);
    }

    public static void Register(Dictionary<string, Func<DockWidgetDescriptor, DockWidgetContext, IDockWidgetTile>> map)
    {
        map["deepseek-balance"] = (d, c) => new DeepSeekBalanceTile(d, c);
        map["youtube-channel"] = (d, c) => new YouTubeChannelTile(d, c);
        map["leetcode-stats"] = (d, c) => new LeetCodeTile(d, c);
        map["gmail-unread"] = (d, c) => new GmailTile(d, c);
        map["gcal-next"] = (d, c) => new CalendarNextTile(d, c);
        map["unsplash-wallpaper"] = (d, c) => new UnsplashTile(d, c);
    }

    // ---- deepseek-balance (reference account widget) --------------------------------------------

    private static async Task<object> FetchDeepSeekAsync(AccountFetchContext ctx, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.deepseek.com/user/balance");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ctx.Require("key"));
        req.Headers.Accept.ParseAdd("application/json");
        using var resp = await ctx.Http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        return SocialMailParsers.ParseDeepSeekBalance(await resp.Content.ReadAsStringAsync(ct));
    }

    private sealed class DeepSeekBalanceTile : AccountTileBase<DeepSeekBalance>
    {
        private RingGauge _ring = null!;
        private TextBlock _amount = null!;
        private TextBlock _caption = null!;

        public DeepSeekBalanceTile(DockWidgetDescriptor d, DockWidgetContext ctx) : base(d, ctx) { }

        protected override DeepSeekBalance Sample(DockWidgetContext ctx) =>
            new(true, [new DeepSeekBalanceItem("USD", 18.42, 2.00, 16.42)]);

        protected override FrameworkElement BuildTile(DockWidgetContext ctx)
        {
            var p = ctx.Palette;
            double h = ctx.TileHeight;
            _ring = W.Ring(p, h * 0.78, Math.Max(2.5, h * 0.11), p.Accent);
            _amount = Tight(W.Text(p, "—", Math.Round(h * 0.40), p.Foreground, FontWeights.SemiBold));
            _caption = Tight(W.Text(p, "DeepSeek", Math.Round(h * 0.26), p.SecondaryForeground));
            var texts = W.Column(_amount, _caption);
            // W.Row applies the spacing as each later child's left margin.
            return W.Row(Math.Round(h * 0.18), _ring, texts);
        }

        protected override void ShowData(DockWidgetContext ctx, DeepSeekBalance data, bool isSample)
        {
            var item = data.Primary;
            double total = item?.Total ?? 0;
            W.SetTextAnimated(_amount, Money(total, item?.Currency));
            // The ring shows how much of the balance is your own top-up (the rest is granted credit).
            double share = total <= 0 ? 0 : Math.Clamp((item!.ToppedUp / total) * 100, 0, 100);
            _ring.Fill = W.Brush(total <= 1 ? ctx.Palette.Negative : total <= 5 ? ctx.Palette.Warning : ctx.Palette.Accent);
            _ring.AnimateTo(total <= 0 ? 0 : Math.Max(share, 6) / 100.0); // RingGauge takes 0..1
            _caption.Text = data.IsAvailable ? "DeepSeek" : ctx.T("Bakiye yok", "No balance");
        }

        protected override FrameworkElement BuildCardBody(DockWidgetContext ctx, DeepSeekBalance data, bool isSample)
        {
            var p = ctx.Palette;
            var col = new StackPanel();
            if (data.Items.Count == 0)
                col.Children.Add(W.Text(p, ctx.T("Bakiye bilgisi yok.", "No balance information."), 12.5, p.SecondaryForeground));
            foreach (var i in data.Items)
            {
                var big = W.Text(p, Money(i.Total, i.Currency), 26, p.Foreground, FontWeights.SemiBold);
                big.Margin = new Thickness(0, 0, 0, 6);
                col.Children.Add(big);
                col.Children.Add(W.CardRow(p, ctx.T("Yüklenen", "Topped up"), Money(i.ToppedUp, i.Currency)));
                col.Children.Add(W.CardRow(p, ctx.T("Hediye kredi", "Granted credit"), Money(i.Granted, i.Currency)));
            }
            col.Children.Add(W.CardRow(p, ctx.T("Durum", "Status"),
                data.IsAvailable ? ctx.T("Kullanılabilir", "Available") : ctx.T("Yetersiz bakiye", "Insufficient balance"),
                W.Brush(data.IsAvailable ? p.Positive : p.Negative)));
            var open = W.Button(p, ctx.T("DeepSeek'i aç", "Open DeepSeek"),
                () => DockWidgetActions.OpenUrl(ctx, "https://platform.deepseek.com/usage"), "");
            open.Margin = new Thickness(0, 8, 0, 0);
            open.HorizontalAlignment = HorizontalAlignment.Left;
            col.Children.Add(open);
            return col;
        }
    }

    // =================================================================================================
    // Shared helpers
    // =================================================================================================

    private const string GlyphPlay = "";
    private const string GlyphMail = "";
    private const string GlyphCalendar = "";
    private const string GlyphOpen = "";
    private const string GlyphRefresh = "";
    private const string GlyphPicture = "";
    private const string GlyphDownload = "";
    private const string GlyphLocation = "";

    /// <summary>Runs a parser and turns malformed payloads into a friendly, bilingual fetch error.</summary>
    private static T ParseOrThrow<T>(AccountFetchContext ctx, Func<T> parse)
    {
        try
        {
            return parse();
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
        {
            throw new AccountFetchException(ctx.T("Servisten beklenmeyen bir yanıt geldi.", "The service sent an unexpected response."));
        }
    }

    private static double Fs(DockWidgetContext ctx, double factor, double min = 8) => Math.Max(min, Math.Round(ctx.TileHeight * factor));

    /// <summary>Compact line height so two text lines fit in a 26 DIP tile.</summary>
    private static TextBlock Tight(TextBlock tb)
    {
        tb.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        tb.LineHeight = Math.Ceiling(tb.FontSize * 1.16);
        return tb;
    }

    private static CultureInfo Culture(DockWidgetContext ctx) => CultureInfo.GetCultureInfo(ctx.En ? "en-US" : "tr-TR");

    private static string Full(long v, DockWidgetContext ctx) => v.ToString("#,0", Culture(ctx));

    private static void FadeTo(UIElement el, double to, double ms, double beginMs = 0)
    {
        if (ms <= 0)
        {
            el.BeginAnimation(UIElement.OpacityProperty, null);
            el.Opacity = to;
            return;
        }
        el.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms))
        {
            BeginTime = TimeSpan.FromMilliseconds(beginMs),
            EasingFunction = Motion.Soft
        }, HandoffBehavior.SnapshotAndReplace);
    }

    /// <summary>Sets the text of a <see cref="W.Button"/> (its last TextBlock).</summary>
    private static void SetButtonText(Border button, string text)
    {
        if (button.Child is StackPanel sp && sp.Children.OfType<TextBlock>().LastOrDefault() is { } tb) tb.Text = text;
    }

    private static Border Chip(DockWidgetPalette p, string text, Color color, double size)
    {
        return new Border
        {
            Background = W.Brush(color, 0x2E),
            BorderBrush = W.Brush(color, 0x70),
            BorderThickness = new Thickness(0.7),
            CornerRadius = new CornerRadius(size),
            Padding = new Thickness(Math.Round(size * 0.45), 0, Math.Round(size * 0.45), Math.Round(size * 0.05)),
            Child = W.Text(p, text, size, W.Brush(color), FontWeights.SemiBold),
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    private static BitmapImage? Decode(byte[]? bytes, int decodeWidth)
    {
        if (bytes is null || bytes.Length == 0) return null;
        try
        {
            using var ms = new MemoryStream(bytes, writable: false);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            if (decodeWidth > 0) bmp.DecodePixelWidth = decodeWidth;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception ex) when (ex is NotSupportedException or IOException or InvalidOperationException or ArgumentException or FileFormatException)
        {
            return null;
        }
    }

    /// <summary>A text block whose number counts up/down with a WPF animation (no timers).</summary>
    private sealed class CountText : TextBlock
    {
        public static readonly DependencyProperty NumberProperty = DependencyProperty.Register(
            nameof(Number), typeof(double), typeof(CountText), new PropertyMetadata(0.0, (d, _) => ((CountText)d).Render()));

        private readonly Func<double, string> _format;
        private double _target = double.NaN;

        public CountText(DockWidgetPalette p, double size, Brush fg, Func<double, string> format)
        {
            _format = format;
            FontSize = size;
            FontFamily = p.UiFont;
            FontWeight = FontWeights.SemiBold;
            Foreground = fg;
            TextTrimming = TextTrimming.CharacterEllipsis;
            VerticalAlignment = VerticalAlignment.Center;
            Text = format(0);
        }

        public double Number
        {
            get => (double)GetValue(NumberProperty);
            set => SetValue(NumberProperty, value);
        }

        private void Render()
        {
            string s = _format(Number);
            if (!string.Equals(Text, s, StringComparison.Ordinal)) Text = s;
        }

        public void CountTo(double value, double ms = 900, double delayMs = 0)
        {
            if (!double.IsNaN(_target) && Math.Abs(_target - value) < 0.5) return;
            _target = value;
            BeginAnimation(NumberProperty, new DoubleAnimation(value, TimeSpan.FromMilliseconds(ms))
            {
                BeginTime = TimeSpan.FromMilliseconds(delayMs),
                EasingFunction = Motion.Soft
            }, HandoffBehavior.SnapshotAndReplace);
        }
    }

    /// <summary>Two stacked layers that cross-fade between image brushes.</summary>
    private sealed class CrossfadeImage : Grid
    {
        private readonly Border _a;
        private readonly Border _b;
        private bool _aFront = true;
        private object? _key;

        public CrossfadeImage(CornerRadius radius)
        {
            _a = new Border { CornerRadius = radius, Opacity = 0 };
            _b = new Border { CornerRadius = radius, Opacity = 0 };
            Children.Add(_a);
            Children.Add(_b);
        }

        /// <summary>Shows <paramref name="brush"/>; fades from the current one unless <paramref name="instant"/>.</summary>
        public void Show(object key, Brush brush, bool instant = false)
        {
            if (Equals(key, _key)) return;
            bool first = _key is null;
            _key = key;
            var front = _aFront ? _a : _b;
            var back = _aFront ? _b : _a;
            back.Background = brush;
            Panel.SetZIndex(back, 1);
            Panel.SetZIndex(front, 0);
            double ms = first || instant ? 0 : 520;
            back.Opacity = ms <= 0 ? 1 : 0;
            FadeTo(back, 1, ms);
            if (ms > 0) FadeTo(front, 0, ms, 380); else FadeTo(front, 0, 0);
            _aFront = !_aFront;
        }
    }

    private static readonly Lazy<HttpClient> CardHttp = new(() =>
    {
        var http = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10)
        })
        { Timeout = TimeSpan.FromSeconds(90) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("DinamikAda/1.0 (+desktop widgets)");
        return http;
    });

    // =================================================================================================
    // youtube-channel
    // =================================================================================================

    private static async Task<object> FetchYouTubeAsync(AccountFetchContext ctx, CancellationToken ct)
    {
        string key = ctx.Require("key");
        string selector = SocialMailParsers.YouTubeChannelSelector(ctx.Require("channel"));
        // The YouTube Data API takes the key as a query parameter.
        string url = "https://www.googleapis.com/youtube/v3/channels?part=snippet,statistics&" + selector +
                     "&key=" + Uri.EscapeDataString(key);
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.ParseAdd("application/json");
        using var resp = await ctx.Http.SendAsync(req, ct);
        string body = await resp.Content.ReadAsStringAsync(ct);
        if ((int)resp.StatusCode is 400 or 403 && SocialMailParsers.ParseGoogleApiError(body) is { } msg)
        {
            throw new AccountFetchException(ctx.T(
                "YouTube isteği reddetti. Anahtarı ve YouTube Data API v3'ün etkin olduğunu kontrol et. (" + msg + ")",
                "YouTube rejected the request. Check the key and that YouTube Data API v3 is enabled. (" + msg + ")"));
        }
        resp.EnsureSuccessStatusCode();
        var channel = ParseOrThrow(ctx, () => SocialMailParsers.ParseYouTubeChannel(body));
        return channel ?? throw new AccountFetchException(ctx.T(
            "Kanal bulunamadı. @kullanıcı adını veya UC… kimliğini kontrol et.",
            "Channel not found. Check the @handle or UC… ID."));
    }

    private sealed class YouTubeChannelTile : AccountTileBase<YouTubeChannel>
    {
        private YouTubeChannel? _sample;
        private TextBlock _subs = null!;
        private TextBlock _caption = null!;
        private Border _delta = null!;
        private long? _prevSubs;

        public YouTubeChannelTile(DockWidgetDescriptor d, DockWidgetContext ctx) : base(d, ctx) { }

        protected override double CardWidth => 300;

        protected override YouTubeChannel Sample(DockWidgetContext ctx) =>
            _sample ??= new YouTubeChannel("UCq7xN2bT5cR8wLzA4kY9mPe", "Kod ve Kahve", "@kodvekahve",
                48_260, 3_912_480, 214, false, null);

        protected override FrameworkElement BuildTile(DockWidgetContext ctx)
        {
            var p = ctx.Palette;
            double h = ctx.TileHeight;
            double badge = Math.Round(h * 0.56);
            var play = new Border
            {
                Width = badge,
                Height = Math.Round(badge * 0.74),
                CornerRadius = new CornerRadius(badge * 0.24),
                Background = W.Brush(p.Negative),
                Child = W.Glyph(p, GlyphPlay, badge * 0.38, Brushes.White),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, Math.Round(h * 0.16), 0)
            };
            _subs = W.Text(p, string.Empty, Fs(ctx, 0.40), p.Foreground, FontWeights.SemiBold);
            _caption = W.Text(p, ctx.T("abone", "subscribers"), Fs(ctx, 0.24), p.SecondaryForeground);
            var texts = W.Column(Tight(_subs), Tight(_caption));

            // The "+N" chip sits in the row (right side) so it never covers the caption.
            _delta = Chip(p, "+0", p.Positive, Math.Max(7.5, Math.Round(h * 0.22)));
            _delta.VerticalAlignment = VerticalAlignment.Center;
            _delta.Margin = new Thickness(Math.Round(h * 0.12), 0, 0, 0);
            _delta.Visibility = Visibility.Collapsed;
            _delta.IsHitTestVisible = false;

            var dock = new DockPanel { LastChildFill = true, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(play, Dock.Left);
            dock.Children.Add(play);
            DockPanel.SetDock(_delta, Dock.Right);
            dock.Children.Add(_delta);
            dock.Children.Add(texts);
            return dock;
        }

        protected override void ShowData(DockWidgetContext ctx, YouTubeChannel data, bool isSample)
        {
            if (data.HiddenSubscribers)
            {
                W.SetTextAnimated(_subs, Compact(data.Views));
                _caption.Text = ctx.T("izlenme", "views");
            }
            else
            {
                W.SetTextAnimated(_subs, Compact(data.Subscribers));
                _caption.Text = ctx.T("abone", "subscribers");
            }

            if (isSample)
            {
                _prevSubs = null;
                ShowDelta(37);
                return;
            }
            if (_prevSubs is { } prev && !data.HiddenSubscribers)
            {
                if (data.Subscribers > prev) ShowDelta(data.Subscribers - prev);
                else if (data.Subscribers < prev) _delta.Visibility = Visibility.Collapsed;
            }
            else
            {
                _delta.Visibility = Visibility.Collapsed;
            }
            _prevSubs = data.Subscribers;
        }

        private void ShowDelta(long diff)
        {
            string text = "+" + Compact(diff);
            if (_delta.Child is TextBlock tb && tb.Text == text && _delta.Visibility == Visibility.Visible) return;
            if (_delta.Child is TextBlock t) t.Text = text;
            _delta.Visibility = Visibility.Visible;
            W.PopIn(_delta, 120);
        }

        protected override FrameworkElement BuildCardBody(DockWidgetContext ctx, YouTubeChannel data, bool isSample)
        {
            var p = ctx.Palette;
            var col = new StackPanel();

            // Avatar + title.
            const double av = 40;
            FrameworkElement avatar;
            if (!isSample && !ctx.IsAuditMode && Uri.TryCreate(data.ThumbnailUrl, UriKind.Absolute, out var thumb) && thumb.Scheme == Uri.UriSchemeHttps)
            {
                var img = new BitmapImage();
                try
                {
                    img.BeginInit();
                    img.UriSource = thumb;
                    img.DecodePixelWidth = 96;
                    img.EndInit();
                    avatar = new Ellipse { Width = av, Height = av, Fill = new ImageBrush(img) { Stretch = Stretch.UniformToFill } };
                }
                catch (Exception ex) when (ex is NotSupportedException or IOException or InvalidOperationException or ArgumentException)
                {
                    avatar = Initials(p, data.Title, av);
                }
            }
            else avatar = Initials(p, data.Title, av);
            avatar.Margin = new Thickness(0, 0, 10, 0);
            var title = W.Text(p, data.Title, 15, p.Foreground, FontWeights.SemiBold);
            var handle = W.Text(p, data.Handle ?? data.Id, 12, p.SecondaryForeground, ui: false);
            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
            DockPanel.SetDock(avatar, Dock.Left);
            head.Children.Add(avatar);
            head.Children.Add(W.Column(title, handle));
            col.Children.Add(head);
            W.PopIn(head);

            // Three counting stats.
            var stats = new UniformGrid { Columns = 3, Margin = new Thickness(0, 0, 0, 8) };
            void Stat(string label, long value, bool hidden, int i)
            {
                var n = new CountText(p, 21, p.Foreground, v => Compact(Math.Round(v)));
                var box = new StackPanel { Margin = new Thickness(i == 0 ? 0 : 4, 0, i == 2 ? 0 : 4, 0) };
                var bg = new Border
                {
                    Background = p.CardRowBackground,
                    CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(10, 8, 10, 8),
                    Child = box
                };
                box.Children.Add(n);
                box.Children.Add(W.Text(p, label, 11.5, p.SecondaryForeground, ui: false));
                stats.Children.Add(bg);
                W.PopIn(bg, 60 + 70 * i);
                if (hidden) n.Text = ctx.T("Gizli", "Hidden");
                else n.CountTo(value, 1100, 120 + 90 * i);
            }
            Stat(ctx.T("abone", "subscribers"), data.Subscribers, data.HiddenSubscribers, 0);
            Stat(ctx.T("izlenme", "views"), data.Views, false, 1);
            Stat(ctx.T("video", "videos"), data.Videos, false, 2);
            col.Children.Add(stats);

            col.Children.Add(W.CardRow(p, ctx.T("Toplam izlenme", "Total views"), Full(data.Views, ctx)));
            if (!data.HiddenSubscribers)
                col.Children.Add(W.CardRow(p, ctx.T("Abone", "Subscribers"), Full(data.Subscribers, ctx)));
            if (data.Videos > 0)
                col.Children.Add(W.CardRow(p, ctx.T("Video başına izlenme", "Views per video"), Compact(data.Views / (double)data.Videos)));

            string url = data.Handle is { Length: > 1 } h && h.StartsWith('@')
                ? "https://www.youtube.com/" + Uri.EscapeDataString(h).Replace("%40", "@")
                : "https://www.youtube.com/channel/" + Uri.EscapeDataString(data.Id);
            var open = W.Button(p, ctx.T("Kanalı aç", "Open channel"), () => DockWidgetActions.OpenUrl(ctx, url), GlyphOpen);
            open.Margin = new Thickness(0, 8, 0, 0);
            open.HorizontalAlignment = HorizontalAlignment.Left;
            col.Children.Add(open);
            return col;
        }

        private static FrameworkElement Initials(DockWidgetPalette p, string title, double size)
        {
            string letter = title.Trim().Length > 0 ? title.Trim()[..1].ToUpper(CultureInfo.CurrentCulture) : "Y";
            return new Border
            {
                Width = size,
                Height = size,
                CornerRadius = new CornerRadius(size / 2),
                Background = W.Brush(p.Negative),
                Child = W.Text(p, letter, size * 0.45, Brushes.White, FontWeights.Bold)
            }.WithCenteredChild();
        }
    }

    // =================================================================================================
    // leetcode-stats
    // =================================================================================================

    private static async Task<object> FetchLeetCodeAsync(AccountFetchContext ctx, CancellationToken ct)
    {
        string user = ctx.Require("user").Trim().TrimStart('@');
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://leetcode.com/graphql")
        {
            Content = new StringContent(SocialMailParsers.LeetCodeRequestBody(user), Encoding.UTF8, "application/json")
        };
        req.Headers.Referrer = new Uri("https://leetcode.com");
        req.Headers.Accept.ParseAdd("application/json");
        using var resp = await ctx.Http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        string body = await resp.Content.ReadAsStringAsync(ct);
        var stats = ParseOrThrow(ctx, () => SocialMailParsers.ParseLeetCode(body));
        return stats ?? throw new AccountFetchException(ctx.T(
            $"LeetCode kullanıcısı bulunamadı: {user}. Profil sayfandaki kullanıcı adını yaz.",
            $"LeetCode user not found: {user}. Use the username from your profile page."));
    }

    private sealed class LeetCodeTile : AccountTileBase<LeetCodeStats>
    {
        private static readonly LeetCodeStats SampleStats = new("emre_dev", 184_233,
            new LeetCodeDifficulty(186, 837), new LeetCodeDifficulty(192, 1748), new LeetCodeDifficulty(34, 757));

        private RingGauge _easy = null!;
        private RingGauge _medium = null!;
        private RingGauge _hard = null!;
        private TextBlock _total = null!;
        private TextBlock _caption = null!;

        public LeetCodeTile(DockWidgetDescriptor d, DockWidgetContext ctx) : base(d, ctx) { }

        protected override double CardWidth => 300;

        protected override LeetCodeStats Sample(DockWidgetContext ctx) => SampleStats;

        protected override FrameworkElement BuildTile(DockWidgetContext ctx)
        {
            var p = ctx.Palette;
            double h = ctx.TileHeight;
            double size = Math.Round(h * 0.86);
            double t = Math.Max(2, Math.Round(h * 0.075, 1));
            double step = t + Math.Max(1.2, h * 0.035);
            _easy = W.Ring(p, size, t, p.Positive);
            _medium = W.Ring(p, size - 2 * step, t, p.Warning);
            _hard = W.Ring(p, size - 4 * step, t, p.Negative);
            var rings = new Grid { Width = size, Height = size, Margin = new Thickness(0, 0, Math.Round(h * 0.16), 0) };
            rings.Children.Add(_easy);
            rings.Children.Add(_medium);
            rings.Children.Add(_hard);

            _total = W.Text(p, string.Empty, Fs(ctx, 0.40), p.Foreground, FontWeights.SemiBold);
            _caption = W.Text(p, ctx.T("çözüldü", "solved"), Fs(ctx, 0.24), p.SecondaryForeground);
            var dock = new DockPanel { LastChildFill = true, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(rings, Dock.Left);
            dock.Children.Add(rings);
            dock.Children.Add(W.Column(Tight(_total), Tight(_caption)));
            return dock;
        }

        protected override void ShowData(DockWidgetContext ctx, LeetCodeStats data, bool isSample)
        {
            W.SetTextAnimated(_total, data.TotalSolved.ToString("#,0", Culture(ctx)));
            _caption.Text = ctx.T("çözüldü", "solved");
            _easy.AnimateTo(data.Easy.Fraction, 700);
            _medium.AnimateTo(data.Medium.Fraction, 850);
            _hard.AnimateTo(data.Hard.Fraction, 1000);
        }

        protected override FrameworkElement BuildCardBody(DockWidgetContext ctx, LeetCodeStats data, bool isSample)
        {
            var p = ctx.Palette;
            var col = new StackPanel();

            var big = new CountText(p, 28, p.Foreground, v => Math.Round(v).ToString("#,0", Culture(ctx)));
            big.CountTo(data.TotalSolved, 1000, 80);
            var of = W.Text(p, " / " + data.TotalQuestions.ToString("#,0", Culture(ctx)), 15, p.SecondaryForeground);
            of.VerticalAlignment = VerticalAlignment.Bottom;
            of.Margin = new Thickness(2, 0, 0, 4);
            var headline = new StackPanel { Orientation = Orientation.Horizontal };
            headline.Children.Add(big);
            headline.Children.Add(of);
            col.Children.Add(headline);
            col.Children.Add(W.Text(p, ctx.T("çözülen problem", "problems solved") + " · " + data.Username, 12, p.SecondaryForeground, ui: false));

            var bars = new StackPanel { Margin = new Thickness(0, 12, 0, 6) };
            void Bar(string label, LeetCodeDifficulty d, Color c, int i)
            {
                var row = new StackPanel { Margin = new Thickness(0, 0, 0, 9) };
                var top = new DockPanel();
                var val = W.Text(p, d.Solved.ToString("#,0", Culture(ctx)) + " / " + d.Total.ToString("#,0", Culture(ctx)), 12, p.SecondaryForeground, FontWeights.SemiBold);
                DockPanel.SetDock(val, Dock.Right);
                top.Children.Add(val);
                top.Children.Add(W.Text(p, label, 12.5, W.Brush(c), FontWeights.SemiBold, ui: false));
                var bar = W.Bar(p, CardWidth, 7, c);
                bar.Width = double.NaN;
                bar.HorizontalAlignment = HorizontalAlignment.Stretch;
                bar.Margin = new Thickness(0, 4, 0, 0);
                row.Children.Add(top);
                row.Children.Add(bar);
                bars.Children.Add(row);
                W.PopIn(row, 70 * i);
                bar.BeginAnimation(BarGauge.ValueProperty, new DoubleAnimation(0, d.Fraction, TimeSpan.FromMilliseconds(760))
                {
                    BeginTime = TimeSpan.FromMilliseconds(160 + 110 * i),
                    EasingFunction = Motion.Soft
                });
            }
            Bar(ctx.T("Kolay", "Easy"), data.Easy, p.Positive, 0);
            Bar(ctx.T("Orta", "Medium"), data.Medium, p.Warning, 1);
            Bar(ctx.T("Zor", "Hard"), data.Hard, p.Negative, 2);
            col.Children.Add(bars);

            col.Children.Add(W.CardRow(p, ctx.T("Sıralama", "Ranking"),
                data.Ranking > 0 ? "#" + data.Ranking.ToString("#,0", Culture(ctx)) : "—"));

            var open = W.Button(p, ctx.T("Profili aç", "Open profile"),
                () => DockWidgetActions.OpenUrl(ctx, "https://leetcode.com/u/" + Uri.EscapeDataString(data.Username) + "/"), GlyphOpen);
            open.Margin = new Thickness(0, 8, 0, 0);
            open.HorizontalAlignment = HorizontalAlignment.Left;
            col.Children.Add(open);
            return col;
        }
    }

    // =================================================================================================
    // gmail-unread (IMAP, read-only)
    // =================================================================================================

    private sealed record GmailInbox(int Unread, int Total, IReadOnlyList<ImapMessageHeader> Newest);

    private static async Task<object> FetchGmailAsync(AccountFetchContext ctx, CancellationToken ct)
    {
        string user = ctx.Require("user").Trim();
        string pass = ctx.Require("key").Replace(" ", string.Empty, StringComparison.Ordinal);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));
        var token = cts.Token;
        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync("imap.gmail.com", 993, token);
            await using var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "imap.gmail.com" }, token);
            var imap = new ImapConnection(ssl);
            await imap.ReadGreetingAsync(token);

            var login = await imap.CommandAsync("a1", "LOGIN " + ImapParsers.Quote(user) + " " + ImapParsers.Quote(pass), token);
            var (loginStatus, _) = ImapParsers.TaggedResult(Encoding.UTF8.GetString(login), "a1");
            if (loginStatus != "OK")
            {
                throw new AccountFetchException(ctx.T(
                    "Gmail girişi reddetti. Gerçek şifreni değil, 2 adımlı doğrulama açıkken oluşturduğun 16 haneli uygulama şifresini kullan.",
                    "Gmail rejected the sign-in. Use the 16-character app password created with 2-Step Verification on, not your real password."));
            }

            string status = Encoding.UTF8.GetString(await imap.CommandAsync("a2", "STATUS INBOX (UNSEEN MESSAGES)", token));
            var counts = ImapParsers.ParseStatus(status);
            await imap.CommandAsync("a3", "EXAMINE INBOX", token);
            string search = Encoding.UTF8.GetString(await imap.CommandAsync("a4", "SEARCH UNSEEN", token));
            var unseen = ImapParsers.ParseSearch(search);
            IReadOnlyList<ImapMessageHeader> newest = [];
            var ids = ImapParsers.Newest(unseen, 5);
            if (ids.Count > 0)
            {
                byte[] fetch = await imap.CommandAsync("a5",
                    "FETCH " + string.Join(",", ids.Select(i => i.ToString(CultureInfo.InvariantCulture))) +
                    " (BODY.PEEK[HEADER.FIELDS (FROM SUBJECT DATE)])", token);
                newest = ImapParsers.ParseFetchHeaders(fetch);
            }
            try { await imap.CommandAsync("a6", "LOGOUT", token); }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidDataException) { /* the server may close first */ }

            int unread = counts.Unseen > 0 || unseen.Count == 0 ? counts.Unseen : unseen.Count;
            return new GmailInbox(unread, counts.Messages, newest);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new AccountFetchException(ctx.T("Gmail zamanında yanıt vermedi.", "Gmail did not answer in time."));
        }
        catch (Exception ex) when (ex is SocketException or IOException or AuthenticationException or InvalidDataException)
        {
            throw new AccountFetchException(ctx.T("Gmail'e (IMAP) bağlanılamadı.", "Could not connect to Gmail (IMAP)."));
        }
    }

    /// <summary>Minimal IMAP command/response loop over a TLS stream; parsing lives in <see cref="ImapParsers"/>.</summary>
    private sealed class ImapConnection(Stream stream)
    {
        private const int MaxResponse = 2 * 1024 * 1024;
        private readonly byte[] _chunk = new byte[16 * 1024];
        private byte[] _buf = new byte[32 * 1024];
        private int _len;

        public async Task ReadGreetingAsync(CancellationToken ct)
        {
            while (true)
            {
                int crlf = IndexOfCrlf();
                if (crlf >= 0)
                {
                    Consume(crlf + 2);
                    return;
                }
                await FillAsync(ct);
            }
        }

        public async Task<byte[]> CommandAsync(string tag, string command, CancellationToken ct)
        {
            byte[] line = Encoding.UTF8.GetBytes(tag + " " + command + "\r\n");
            await stream.WriteAsync(line, ct);
            await stream.FlushAsync(ct);
            while (true)
            {
                int end = ImapParsers.FindTaggedEnd(_buf.AsSpan(0, _len), tag);
                if (end >= 0)
                {
                    byte[] resp = _buf.AsSpan(0, end).ToArray();
                    Consume(end);
                    return resp;
                }
                await FillAsync(ct);
            }
        }

        private async Task FillAsync(CancellationToken ct)
        {
            int n = await stream.ReadAsync(_chunk, ct);
            if (n <= 0) throw new IOException("IMAP connection closed.");
            if (_len + n > MaxResponse) throw new InvalidDataException("IMAP response too large.");
            if (_len + n > _buf.Length) Array.Resize(ref _buf, Math.Max(_buf.Length * 2, _len + n));
            Buffer.BlockCopy(_chunk, 0, _buf, _len, n);
            _len += n;
        }

        private int IndexOfCrlf()
        {
            for (int i = 0; i + 1 < _len; i++)
                if (_buf[i] == '\r' && _buf[i + 1] == '\n') return i;
            return -1;
        }

        private void Consume(int count)
        {
            Buffer.BlockCopy(_buf, count, _buf, 0, _len - count);
            _len -= count;
        }
    }

    private sealed class GmailTile : AccountTileBase<GmailInbox>
    {
        private GmailInbox? _sample;
        private string? _sampleLang;
        private FrameworkElement _group = null!;
        private TextBlock _glyph = null!;
        private TextBlock _count = null!;
        private int? _lastCount;

        public GmailTile(DockWidgetDescriptor d, DockWidgetContext ctx) : base(d, ctx) { }

        protected override double CardWidth => 320;

        protected override GmailInbox Sample(DockWidgetContext ctx)
        {
            if (_sample is not null && _sampleLang == ctx.Lang) return _sample;
            _sampleLang = ctx.Lang;
            var now = new DateTimeOffset(ctx.Now);
            ImapMessageHeader M(int seq, string name, string addr, string tr, string en, double minutesAgo) =>
                new(seq, name, addr, ctx.T(tr, en), now.AddMinutes(-minutesAgo));
            return _sample = new GmailInbox(12, 4821,
            [
                M(4821, "GitHub", "notifications@github.com", "[dinamikada] PR #128 birleştirildi: dock widget'ları", "[dinamikada] PR #128 merged: dock widgets", 6),
                M(4819, "Ayşe Yılmaz", "ayse.yilmaz@example.com", "Toplantı notları ve sonraki adımlar", "Meeting notes and next steps", 42),
                M(4815, "Trendyol", "bilgi@trendyol.com", "Siparişin kargoya verildi", "Your order has shipped", 128),
                M(4810, "Google", "no-reply@accounts.google.com", "Güvenlik uyarısı: yeni oturum açma", "Security alert: new sign-in", 305),
                M(4802, "Medium Daily Digest", "noreply@medium.com", ".NET 8'de dayanıklı servisler kurmak", "Building resilient services in .NET 8", 540),
            ]);
        }

        protected override FrameworkElement BuildTile(DockWidgetContext ctx)
        {
            var p = ctx.Palette;
            double h = ctx.TileHeight;
            _glyph = W.Glyph(p, GlyphMail, Math.Round(h * 0.46), p.SecondaryForeground);
            _glyph.Margin = new Thickness(0, 0, Math.Round(h * 0.12), 0);
            _count = W.Text(p, string.Empty, Fs(ctx, 0.42), p.Foreground, FontWeights.SemiBold);
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            row.Children.Add(_glyph);
            row.Children.Add(_count);
            _group = row;
            return new Grid { Children = { row } };
        }

        protected override void ShowData(DockWidgetContext ctx, GmailInbox data, bool isSample)
        {
            var p = ctx.Palette;
            string text = data.Unread > 99 ? "99+" : data.Unread.ToString(CultureInfo.InvariantCulture);
            W.SetTextAnimated(_count, text);
            var glyphBrush = data.Unread > 0 ? p.AccentBrush : p.SecondaryForeground;
            if (!Equals((_glyph.Foreground as SolidColorBrush)?.Color, (glyphBrush as SolidColorBrush)?.Color)) _glyph.Foreground = glyphBrush;
            if (_lastCount is { } last && last != data.Unread) W.Pulse(_group, data.Unread > last ? 1.22 : 0.86);
            _lastCount = data.Unread;
        }

        protected override FrameworkElement BuildCardBody(DockWidgetContext ctx, GmailInbox data, bool isSample)
        {
            var p = ctx.Palette;
            var col = new StackPanel();
            var big = new CountText(p, 28, p.Foreground, v => Math.Round(v).ToString("#,0", Culture(ctx)));
            big.CountTo(data.Unread, 800, 60);
            var label = W.Text(p, ctx.T(" okunmamış", " unread"), 15, p.SecondaryForeground);
            label.VerticalAlignment = VerticalAlignment.Bottom;
            label.Margin = new Thickness(2, 0, 0, 4);
            var headline = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 2) };
            headline.Children.Add(big);
            headline.Children.Add(label);
            col.Children.Add(headline);
            if (data.Total > 0)
                col.Children.Add(W.Text(p, ctx.T($"Gelen kutusunda {Full(data.Total, ctx)} e-posta", $"{Full(data.Total, ctx)} emails in the inbox"),
                    12, p.TertiaryForeground, ui: false));

            var list = new StackPanel { Margin = new Thickness(0, 10, 0, 4) };
            if (data.Newest.Count == 0)
            {
                list.Children.Add(W.Text(p, ctx.T("Okunmamış e-posta yok. Gelen kutun tertemiz.", "No unread email. Inbox zero."), 12.5, p.SecondaryForeground, ui: false));
            }
            for (int i = 0; i < data.Newest.Count; i++)
            {
                var m = data.Newest[i];
                var top = new DockPanel();
                if (m.Date is { } d)
                {
                    var ago = W.Text(p, Ago(ctx, d.LocalDateTime), 11.5, p.TertiaryForeground, ui: false);
                    ago.Margin = new Thickness(8, 0, 0, 0);
                    DockPanel.SetDock(ago, Dock.Right);
                    top.Children.Add(ago);
                }
                top.Children.Add(W.Text(p, m.FromName, 12.5, p.Foreground, FontWeights.SemiBold, ui: false));
                var subject = W.Text(p, string.IsNullOrWhiteSpace(m.Subject) ? ctx.T("(konu yok)", "(no subject)") : m.Subject,
                    12, p.SecondaryForeground, ui: false);
                var row = new Border
                {
                    Background = p.CardRowBackground,
                    CornerRadius = new CornerRadius(9),
                    Padding = new Thickness(10, 6, 10, 7),
                    Margin = new Thickness(0, 0, 0, 5),
                    Child = W.Column(top, subject)
                };
                list.Children.Add(row);
                W.PopIn(row, 50 + 60 * i);
            }
            col.Children.Add(list);

            var open = W.Button(p, ctx.T("Gmail'i aç", "Open Gmail"), () => DockWidgetActions.OpenUrl(ctx, "https://mail.google.com"), GlyphOpen);
            open.Margin = new Thickness(0, 4, 0, 0);
            open.HorizontalAlignment = HorizontalAlignment.Left;
            col.Children.Add(open);
            return col;
        }
    }

    // =================================================================================================
    // gcal-next (secret iCal address)
    // =================================================================================================

    private sealed record CalendarAgenda(IReadOnlyList<CalendarEvent> Events);

    private static async Task<object> FetchCalendarAsync(AccountFetchContext ctx, CancellationToken ct)
    {
        string raw = ctx.Require("url").Trim();
        if (raw.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase)) raw = "https://" + raw["webcal://".Length..];
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new AccountFetchException(ctx.T(
                "Takvim adresi https:// ile başlamalı. Google Takvim ayarlarındaki \"iCal biçimindeki gizli adres\"i yapıştır.",
                "The calendar address must start with https://. Paste the \"Secret address in iCal format\" from Google Calendar settings."));
        }
        using var req = new HttpRequestMessage(HttpMethod.Get, uri);
        req.Headers.Accept.ParseAdd("text/calendar");
        using var resp = await ctx.Http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        string ics = await resp.Content.ReadAsStringAsync(ct);
        if (!ics.Contains("BEGIN:VCALENDAR", StringComparison.OrdinalIgnoreCase))
            throw new AccountFetchException(ctx.T("Bu adres bir iCal takvimi döndürmedi.", "This address did not return an iCal calendar."));
        var now = DateTime.Now;
        var events = ICalParser.Parse(ics, now.AddDays(-1), now.AddDays(14), TimeZoneInfo.Local);
        return new CalendarAgenda(ICalParser.Upcoming(events, now, 8));
    }

    private sealed class CalendarNextTile : AccountTileBase<CalendarAgenda>
    {
        private CalendarAgenda? _sample;
        private string? _sampleLang;
        private Border _stripe = null!;
        private TextBlock _title = null!;
        private TextBlock _when = null!;
        private BarGauge _bar = null!;
        private CalendarEvent? _shown;
        private bool _shownEmpty;
        private Color _barColor;

        public CalendarNextTile(DockWidgetDescriptor d, DockWidgetContext ctx) : base(d, ctx) { }

        protected override double CardWidth => 320;

        protected override CalendarAgenda Sample(DockWidgetContext ctx)
        {
            if (_sample is not null && _sampleLang == ctx.Lang) return _sample;
            _sampleLang = ctx.Lang;
            var n = ctx.Now;
            var baseTime = new DateTime(n.Year, n.Month, n.Day, n.Hour, n.Minute / 5 * 5, 0);
            var today = n.Date;
            CalendarEvent E(string tr, string en, string? loc, DateTime s, double minutes, bool allDay = false) =>
                new(ctx.T(tr, en), loc, s, s.AddMinutes(minutes), allDay);
            return _sample = new CalendarAgenda(
            [
                E("Ürün ekibi haftalık", "Product team weekly", ctx.T("Toplantı Odası 2", "Meeting Room 2"), baseTime.AddMinutes(25), 45),
                E("Öğle yemeği · Elif", "Lunch · Elif", "Karaköy Lokantası", baseTime.AddHours(2).AddMinutes(10), 60),
                E("Tasarım incelemesi", "Design review", "Google Meet", baseTime.AddHours(4).AddMinutes(30), 30),
                E("Annemin doğum günü", "Mom's birthday", null, today.AddDays(1), 24 * 60, allDay: true),
                E("Diş hekimi", "Dentist", "Nişantaşı", today.AddDays(1).AddHours(10), 40),
                E("Koşu kulübü", "Running club", ctx.T("Maçka Parkı", "Maçka Park"), today.AddDays(2).AddHours(8), 60),
            ]);
        }

        protected override FrameworkElement BuildTile(DockWidgetContext ctx)
        {
            var p = ctx.Palette;
            double h = ctx.TileHeight;
            _stripe = new Border
            {
                Width = Math.Max(2.5, Math.Round(h * 0.09)),
                CornerRadius = new CornerRadius(2),
                Background = p.AccentBrush,
                Margin = new Thickness(0, Math.Round(h * 0.12), Math.Round(h * 0.16), Math.Round(h * 0.12))
            };
            _title = W.Text(p, string.Empty, Fs(ctx, 0.30), p.Foreground, FontWeights.SemiBold);
            _when = W.Text(p, string.Empty, Fs(ctx, 0.24), p.AccentBrush, FontWeights.SemiBold);
            _bar = W.Bar(p, 10, Math.Max(2, Math.Round(h * 0.07)), p.Accent);
            _barColor = p.Accent;
            _bar.Width = double.NaN;
            _bar.HorizontalAlignment = HorizontalAlignment.Stretch;
            _bar.Margin = new Thickness(0, Math.Max(1, Math.Round(h * 0.04)), 0, 0);
            var col = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            col.Children.Add(Tight(_title));
            col.Children.Add(Tight(_when));
            col.Children.Add(_bar);
            var dock = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(_stripe, Dock.Left);
            dock.Children.Add(_stripe);
            dock.Children.Add(col);
            return dock;
        }

        protected override void ShowData(DockWidgetContext ctx, CalendarAgenda data, bool isSample)
        {
            _shown = null;
            _shownEmpty = false;
            Render(ctx, data);
        }

        protected override void Tick(DockWidgetContext ctx, CalendarAgenda data, bool isSample) => Render(ctx, data);

        private void Render(DockWidgetContext ctx, CalendarAgenda data)
        {
            if (_title is null) return;
            var p = ctx.Palette;
            var now = ctx.Now;
            var next = data.Events.FirstOrDefault(e => e.End > now && !e.AllDay)
                       ?? data.Events.FirstOrDefault(e => e.End > now);
            if (next is null)
            {
                if (!_shownEmpty)
                {
                    _shownEmpty = true;
                    _shown = null;
                    W.SetTextAnimated(_title, ctx.T("Etkinlik yok", "No events"));
                }
                SetText(_when, ctx.T("Önümüzdeki 2 hafta boş", "Free for 2 weeks"));
                SetBar(p, 0, p.Accent);
                return;
            }
            _shownEmpty = false;
            if (!ReferenceEquals(next, _shown))
            {
                _shown = next;
                W.SetTextAnimated(_title, Title(ctx, next));
            }

            bool live = next.Start <= now;
            W.SetTextAnimated(_when, Countdown(ctx, next, now));
            if (live)
            {
                double total = (next.End - next.Start).TotalSeconds;
                SetBar(p, total <= 0 ? 1 : (now - next.Start).TotalSeconds / total, p.Positive);
            }
            else
            {
                double mins = (next.Start - now).TotalMinutes;
                SetBar(p, next.AllDay || mins > 60 ? 0 : 1 - mins / 60, mins <= 10 ? p.Warning : p.Accent);
            }
        }

        private static void SetText(TextBlock tb, string text) => W.SetTextAnimated(tb, text);

        private void SetBar(DockWidgetPalette p, double value, Color color)
        {
            if (_barColor != color)
            {
                _barColor = color;
                _bar.Fill = W.Brush(color);
                _bar.InvalidateVisual();
                _stripe.Background = W.Brush(color);
                _when.Foreground = W.Brush(color);
            }
            _bar.AnimateTo(value, 600);
        }

        private static string Title(DockWidgetContext ctx, CalendarEvent e) =>
            string.IsNullOrWhiteSpace(e.Title) ? ctx.T("(başlıksız)", "(no title)") : e.Title;

        private static string Countdown(DockWidgetContext ctx, CalendarEvent e, DateTime now)
        {
            if (e.Start <= now)
            {
                if (e.AllDay) return ctx.T("bugün", "today");
                int left = (int)Math.Ceiling((e.End - now).TotalMinutes);
                return left >= 60
                    ? ctx.T($"şimdi · {left / 60} sa {left % 60} dk kaldı", $"now · {left / 60}h {left % 60}m left")
                    : ctx.T($"şimdi · {left} dk kaldı", $"now · {left}m left");
            }
            if (e.AllDay)
            {
                int days = (e.Start.Date - now.Date).Days;
                return days <= 0 ? ctx.T("bugün", "today") : days == 1 ? ctx.T("yarın", "tomorrow") : ctx.T($"{days} gün sonra", $"in {days}d");
            }
            var d = e.Start - now;
            int mins = (int)Math.Ceiling(d.TotalMinutes);
            if (mins < 60) return ctx.T($"{mins} dk sonra", $"in {mins}m");
            if (d.TotalHours < 12 || e.Start.Date == now.Date)
            {
                int hh = mins / 60, mm = mins % 60;
                return mm == 0 ? ctx.T($"{hh} sa sonra", $"in {hh}h") : ctx.T($"{hh} sa {mm} dk sonra", $"in {hh}h {mm}m");
            }
            string time = e.Start.ToString("HH:mm", CultureInfo.InvariantCulture);
            if (e.Start.Date == now.Date.AddDays(1)) return ctx.T($"yarın {time}", $"tomorrow {time}");
            return e.Start.ToString("ddd HH:mm", Culture(ctx));
        }

        protected override FrameworkElement BuildCardBody(DockWidgetContext ctx, CalendarAgenda data, bool isSample)
        {
            var p = ctx.Palette;
            var now = ctx.Now;
            var col = new StackPanel();
            var events = data.Events.Where(e => e.End > now).ToList();
            if (events.Count == 0)
            {
                col.Children.Add(W.Text(p, ctx.T("Önümüzdeki 14 günde etkinlik yok.", "No events in the next 14 days."), 12.5, p.SecondaryForeground, ui: false));
            }

            int index = 0;
            foreach (var group in events.GroupBy(e => e.Start.Date < now.Date ? now.Date : e.Start.Date))
            {
                string label = group.Key == now.Date ? ctx.T("Bugün", "Today")
                    : group.Key == now.Date.AddDays(1) ? ctx.T("Yarın", "Tomorrow")
                    : group.Key.ToString(ctx.En ? "dddd, MMM d" : "d MMMM dddd", Culture(ctx));
                var header = W.Text(p, label, 11.5, p.TertiaryForeground, FontWeights.SemiBold, ui: false);
                header.Margin = new Thickness(0, index == 0 ? 0 : 8, 0, 4);
                col.Children.Add(header);
                W.PopIn(header, 50 * index);

                foreach (var e in group)
                {
                    bool live = e.Start <= now && e.End > now;
                    var timeCol = new StackPanel { Width = 66, VerticalAlignment = VerticalAlignment.Top };
                    if (e.AllDay)
                    {
                        timeCol.Children.Add(W.Text(p, ctx.T("Tüm gün", "All day"), 12.5, p.Foreground, FontWeights.SemiBold));
                    }
                    else
                    {
                        timeCol.Children.Add(W.Text(p, e.Start.ToString("HH:mm", CultureInfo.InvariantCulture), 13, p.Foreground, FontWeights.SemiBold, mono: false));
                        timeCol.Children.Add(W.Text(p, e.End.ToString("HH:mm", CultureInfo.InvariantCulture), 11.5, p.TertiaryForeground));
                    }
                    var info = new StackPanel();
                    var titleRow = new DockPanel();
                    if (live)
                    {
                        var chip = Chip(p, ctx.T("şimdi", "now"), p.Positive, 10.5);
                        chip.Margin = new Thickness(6, 0, 0, 0);
                        DockPanel.SetDock(chip, Dock.Right);
                        titleRow.Children.Add(chip);
                    }
                    titleRow.Children.Add(W.Text(p, Title(ctx, e), 12.5, p.Foreground, FontWeights.SemiBold, ui: false));
                    info.Children.Add(titleRow);
                    if (e.Location is { } loc)
                    {
                        var locRow = new DockPanel { Margin = new Thickness(0, 2, 0, 0) };
                        var g = W.Glyph(p, GlyphLocation, 10.5, p.TertiaryForeground);
                        g.Margin = new Thickness(0, 0, 4, 0);
                        DockPanel.SetDock(g, Dock.Left);
                        locRow.Children.Add(g);
                        locRow.Children.Add(W.Text(p, loc.Replace('\n', ' '), 11.5, p.SecondaryForeground, ui: false));
                        info.Children.Add(locRow);
                    }
                    var rowDock = new DockPanel();
                    DockPanel.SetDock(timeCol, Dock.Left);
                    rowDock.Children.Add(timeCol);
                    rowDock.Children.Add(info);
                    var row = new Border
                    {
                        Background = p.CardRowBackground,
                        BorderBrush = live ? W.Brush(p.Positive) : Brushes.Transparent,
                        BorderThickness = new Thickness(live ? 2.5 : 0, 0, 0, 0),
                        CornerRadius = new CornerRadius(9),
                        Padding = new Thickness(10, 6, 10, 7),
                        Margin = new Thickness(0, 0, 0, 5),
                        Child = rowDock
                    };
                    col.Children.Add(row);
                    W.PopIn(row, 50 * ++index);
                }
            }

            var open = W.Button(p, ctx.T("Takvimi aç", "Open calendar"), () => DockWidgetActions.OpenUrl(ctx, "https://calendar.google.com"), GlyphCalendar);
            open.Margin = new Thickness(0, 6, 0, 0);
            open.HorizontalAlignment = HorizontalAlignment.Left;
            col.Children.Add(open);
            return col;
        }
    }

    // =================================================================================================
    // unsplash-wallpaper
    // =================================================================================================

    private sealed record UnsplashData(UnsplashPhoto Photo, byte[]? SmallBytes);

    private static async Task<object> FetchUnsplashAsync(AccountFetchContext ctx, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.unsplash.com/photos/random?orientation=landscape&content_filter=high");
        req.Headers.Authorization = new AuthenticationHeaderValue("Client-ID", ctx.Require("key"));
        req.Headers.Add("Accept-Version", "v1");
        req.Headers.Accept.ParseAdd("application/json");
        using var resp = await ctx.Http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        string body = await resp.Content.ReadAsStringAsync(ct);
        var photo = ParseOrThrow(ctx, () => SocialMailParsers.ParseUnsplashPhoto(body))
                    ?? throw new AccountFetchException(ctx.T("Unsplash fotoğraf bilgisi eksik geldi.", "Unsplash returned incomplete photo data."));

        byte[]? small = null;
        if (Uri.TryCreate(photo.SmallUrl, UriKind.Absolute, out var smallUri) && smallUri.Scheme == Uri.UriSchemeHttps)
        {
            using var img = new HttpRequestMessage(HttpMethod.Get, smallUri);
            using var imgResp = await ctx.Http.SendAsync(img, ct);
            imgResp.EnsureSuccessStatusCode();
            small = await imgResp.Content.ReadAsByteArrayAsync(ct);
        }
        return new UnsplashData(photo, small);
    }

    private const uint SPI_SETDESKWALLPAPER = 0x0014;
    private const uint SPIF_UPDATEINIFILE = 0x01;
    private const uint SPIF_SENDCHANGE = 0x02;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, string pvParam, uint fWinIni);

    private sealed class UnsplashTile : AccountTileBase<UnsplashData>
    {
        private static readonly UnsplashData SampleData = new(new UnsplashPhoto("sample-bosphorus",
            "", "", "", "Deniz Kaya", "https://unsplash.com", "https://unsplash.com", "", "#2B3A67",
            "Boğaz'da gün batımı"), null);

        private CrossfadeImage _image = null!;
        private Brush? _sampleBrush;
        private string? _regularId;
        private BitmapImage? _regular;
        private bool _settingWallpaper;

        public UnsplashTile(DockWidgetDescriptor d, DockWidgetContext ctx) : base(d, ctx) { }

        protected override bool ShowChrome => false;
        protected override Thickness ContentPadding(double h) => new(0);
        protected override double CardWidth => 320;

        protected override UnsplashData Sample(DockWidgetContext ctx) => SampleData;

        private Brush SampleBrush()
        {
            if (_sampleBrush is not null) return _sampleBrush;
            // A generated dusk-over-water gradient stands in for the photo (no network in sample/audit mode).
            var sky = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0.35, 1),
                GradientStops =
                {
                    new GradientStop(Color.FromRgb(0x1F, 0x2A, 0x5A), 0.0),
                    new GradientStop(Color.FromRgb(0x6A, 0x3F, 0x8E), 0.38),
                    new GradientStop(Color.FromRgb(0xF0, 0x7C, 0x6C), 0.66),
                    new GradientStop(Color.FromRgb(0xFC, 0xC4, 0x7A), 0.80),
                    new GradientStop(Color.FromRgb(0x24, 0x3B, 0x5E), 0.81),
                    new GradientStop(Color.FromRgb(0x10, 0x1C, 0x33), 1.0),
                }
            };
            sky.Freeze();
            return _sampleBrush = sky;
        }

        protected override FrameworkElement BuildTile(DockWidgetContext ctx)
        {
            var p = ctx.Palette;
            double h = ctx.TileHeight;
            var radius = new CornerRadius(Math.Max(8, h * 0.28));
            _image = new CrossfadeImage(radius);
            var frame = new Border
            {
                CornerRadius = radius,
                BorderBrush = p.TileBorder,
                BorderThickness = new Thickness(0.8),
                IsHitTestVisible = false
            };
            var grid = new Grid { Background = Brushes.Transparent };
            grid.Children.Add(new Border { CornerRadius = radius, Background = p.TileBackground });
            grid.Children.Add(_image);
            grid.Children.Add(frame);
            return grid;
        }

        protected override void ShowData(DockWidgetContext ctx, UnsplashData data, bool isSample)
        {
            if (isSample || data.SmallBytes is null)
            {
                _image.Show("sample", SampleBrush());
                return;
            }
            var bmp = Decode(data.SmallBytes, (int)Math.Round(Root.Width * 2));
            if (bmp is null)
            {
                _image.Show("sample", SampleBrush());
                return;
            }
            var brush = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill };
            brush.Freeze();
            _image.Show(data.Photo.Id, brush);
        }

        protected override FrameworkElement BuildCardBody(DockWidgetContext ctx, UnsplashData data, bool isSample)
        {
            var p = ctx.Palette;
            var photo = data.Photo;
            var col = new StackPanel();
            bool live = !isSample && !ctx.IsAuditMode && data.SmallBytes is not null;

            // Preview: the small image at once, the regular one cross-fades in when it arrives.
            var radius = new CornerRadius(12);
            var preview = new CrossfadeImage(radius) { Height = Math.Round(CardWidth * 0.6) };
            var previewHost = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            Color tint = p.Accent;
            if (photo.Color is { } hex)
            {
                try { tint = (Color)ColorConverter.ConvertFromString(hex); }
                catch (FormatException) { }
            }
            previewHost.Children.Add(new Border { CornerRadius = radius, Background = W.Brush(tint, 0xFF) });
            previewHost.Children.Add(preview);
            col.Children.Add(previewHost);
            W.PopIn(previewHost);

            if (!live)
            {
                preview.Show("sample", SampleBrush(), instant: true);
            }
            else
            {
                var small = Decode(data.SmallBytes, 640);
                if (small is not null)
                {
                    var sb = new ImageBrush(small) { Stretch = Stretch.UniformToFill };
                    sb.Freeze();
                    preview.Show(photo.Id + ":s", sb, instant: true);
                }
                if (_regularId == photo.Id && _regular is not null) ShowRegular(preview, photo.Id, _regular);
                else _ = LoadRegularAsync(preview, photo);
            }

            if (!string.IsNullOrWhiteSpace(photo.Description))
            {
                var desc = W.Text(p, photo.Description!, 12.5, p.Foreground, ui: false);
                desc.TextWrapping = TextWrapping.Wrap;
                desc.MaxHeight = 36;
                desc.Margin = new Thickness(0, 0, 0, 4);
                col.Children.Add(desc);
            }

            // Attribution required by the Unsplash API guidelines, with referral parameters.
            var credit = new TextBlock
            {
                FontFamily = p.TextFont,
                FontSize = 12,
                Foreground = p.SecondaryForeground,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            };
            Hyperlink Link(string text, string url)
            {
                var link = new Hyperlink(new Run(text)) { Foreground = p.AccentBrush, TextDecorations = null, Cursor = System.Windows.Input.Cursors.Hand };
                link.Click += (_, _) => DockWidgetActions.OpenUrl(ctx, SocialMailParsers.WithUnsplashReferral(url));
                return link;
            }
            if (ctx.En)
            {
                credit.Inlines.Add(new Run("Photo by "));
                credit.Inlines.Add(Link(photo.UserName, photo.UserLink));
                credit.Inlines.Add(new Run(" on "));
                credit.Inlines.Add(Link("Unsplash", "https://unsplash.com"));
            }
            else
            {
                credit.Inlines.Add(new Run("Fotoğraf: "));
                credit.Inlines.Add(Link(photo.UserName, photo.UserLink));
                credit.Inlines.Add(new Run(" / "));
                credit.Inlines.Add(Link("Unsplash", "https://unsplash.com"));
            }
            col.Children.Add(credit);

            var buttons = new WrapPanel();
            buttons.Children.Add(W.Button(p, ctx.T("Yeni fotoğraf", "New photo"), () =>
            {
                if (ctx.IsAuditMode || isSample) return;
                ctx.Data.Accounts.Refresh(Id);
            }, GlyphRefresh));
            Border? setButton = null;
            setButton = W.Button(p, ctx.T("Duvar kağıdı yap", "Set as wallpaper"), () =>
            {
                if (ctx.IsAuditMode || !live || setButton is null) return;
                _ = SetWallpaperAsync(ctx, photo, setButton);
            }, GlyphPicture, primary: true);
            buttons.Children.Add(setButton);
            buttons.Children.Add(W.Button(p, string.Empty, () => DockWidgetActions.OpenUrl(ctx, SocialMailParsers.WithUnsplashReferral(photo.PhotoLink)), GlyphOpen));
            col.Children.Add(buttons);
            return col;
        }

        private static void ShowRegular(CrossfadeImage preview, string id, BitmapImage bmp)
        {
            var brush = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill };
            brush.Freeze();
            preview.Show(id + ":r", brush);
        }

        private async Task LoadRegularAsync(CrossfadeImage preview, UnsplashPhoto photo)
        {
            try
            {
                if (!Uri.TryCreate(photo.RegularUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return;
                byte[] bytes = await CardHttp.Value.GetByteArrayAsync(uri);
                var bmp = await Task.Run(() => Decode(bytes, 960));
                if (bmp is null) return;
                _regularId = photo.Id;
                _regular = bmp;
                ShowRegular(preview, photo.Id, bmp);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
                // The small preview stays.
            }
        }

        private async Task SetWallpaperAsync(DockWidgetContext ctx, UnsplashPhoto photo, Border button)
        {
            if (_settingWallpaper || ctx.IsAuditMode) return;
            _settingWallpaper = true;
            try
            {
                SetButtonText(button, ctx.T("İndiriliyor…", "Downloading…"));
                string? key = AccountStore.Get(ctx.Config, "unsplash", "key");
                var http = CardHttp.Value;

                // The Unsplash API requires triggering download_location whenever a photo is downloaded.
                if (key is not null && Uri.TryCreate(photo.DownloadLocation, UriKind.Absolute, out var track) && track.Scheme == Uri.UriSchemeHttps)
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, track);
                    req.Headers.Authorization = new AuthenticationHeaderValue("Client-ID", key);
                    req.Headers.Add("Accept-Version", "v1");
                    try { using var _ = await http.SendAsync(req); }
                    catch (HttpRequestException) { /* tracking is best-effort; the download still proceeds */ }
                }

                if (!Uri.TryCreate(photo.FullUrl, UriKind.Absolute, out var full) || full.Scheme != Uri.UriSchemeHttps)
                    throw new InvalidOperationException("Bad photo URL.");
                string dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DinamikAda", "Wallpapers");
                Directory.CreateDirectory(dir);
                string path = System.IO.Path.Combine(dir, SocialMailParsers.UnsplashWallpaperFileName(photo.Id));
                string temp = path + ".part";
                using (var resp = await http.GetAsync(full, HttpCompletionOption.ResponseHeadersRead))
                {
                    resp.EnsureSuccessStatusCode();
                    long? total = resp.Content.Headers.ContentLength;
                    await using var src = await resp.Content.ReadAsStreamAsync();
                    await using (var dst = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                    {
                        var buffer = new byte[81920];
                        long read = 0;
                        int lastPct = -1;
                        int n;
                        while ((n = await src.ReadAsync(buffer)) > 0)
                        {
                            await dst.WriteAsync(buffer.AsMemory(0, n));
                            read += n;
                            if (total is > 0)
                            {
                                int pct = (int)(read * 100 / total.Value);
                                if (pct != lastPct && pct % 5 == 0)
                                {
                                    lastPct = pct;
                                    SetButtonText(button, ctx.T($"İndiriliyor… %{pct}", $"Downloading… {pct}%"));
                                }
                            }
                        }
                    }
                }
                File.Move(temp, path, overwrite: true);

                SetButtonText(button, ctx.T("Ayarlanıyor…", "Applying…"));
                bool ok = await Task.Run(() => SystemParametersInfo(SPI_SETDESKWALLPAPER, 0, path, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE));
                SetButtonText(button, ok ? ctx.T("Duvar kağıdı ayarlandı", "Wallpaper set") : ctx.T("Ayarlanamadı", "Could not set it"));
                W.Pulse(button, ok ? 1.06 : 0.94);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                SetButtonText(button, ctx.T("İndirilemedi, tekrar dene", "Download failed, try again"));
                Services.CrashLogger.Log("AccountWidget", ex, "Unsplash wallpaper could not be set.");
            }
            finally
            {
                _settingWallpaper = false;
            }
        }
    }
}

internal static class SocialMailTileExtensions
{
    /// <summary>Centers a Border's TextBlock child (initials avatars).</summary>
    public static Border WithCenteredChild(this Border b)
    {
        if (b.Child is FrameworkElement fe)
        {
            fe.HorizontalAlignment = HorizontalAlignment.Center;
            fe.VerticalAlignment = VerticalAlignment.Center;
        }
        return b;
    }
}
