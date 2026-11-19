using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DinamikAda.App.Services.DockWidgets;
using DinamikAda.Core.Accounts;
using DinamikAda.Core.Widgets;

namespace DinamikAda.App.Views.DockWidgets.Accounts;

/// <summary>Finance account widgets: stripe-revenue, lemonsqueezy-revenue, gumroad-sales, polar-revenue, paddle-revenue.</summary>
public static class FinanceAccountTiles
{
    public static void RegisterFetchers(Action<string, string, AccountFetcher> add)
    {
        add("stripe-revenue", "stripe", FetchStripeAsync);
        add("lemonsqueezy-revenue", "lemonsqueezy", FetchLemonSqueezyAsync);
        add("gumroad-sales", "gumroad", FetchGumroadAsync);
        add("polar-revenue", "polar", FetchPolarAsync);
        add("paddle-revenue", "paddle", FetchPaddleAsync);
    }

    public static void Register(Dictionary<string, Func<DockWidgetDescriptor, DockWidgetContext, IDockWidgetTile>> map)
    {
        map["stripe-revenue"] = (d, c) => new StripeRevenueTile(d, c);
        map["lemonsqueezy-revenue"] = (d, c) => new LemonSqueezyTile(d, c);
        map["gumroad-sales"] = (d, c) => new GumroadSalesTile(d, c);
        map["polar-revenue"] = (d, c) => new PolarRevenueTile(d, c);
        map["paddle-revenue"] = (d, c) => new PaddleRevenueTile(d, c);
    }

    // ---- Fetch helpers --------------------------------------------------------------------------

    private static async Task<string> GetAsync(AccountFetchContext ctx, string url, string accept, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ctx.Require("key"));
        req.Headers.Accept.ParseAdd(accept);
        using var resp = await ctx.Http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct);
    }

    /// <summary>Runs a Core parser and turns a malformed response into a friendly error.</summary>
    private static T Parse<T>(AccountFetchContext ctx, string service, Func<T> parse)
    {
        try { return parse(); }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
        {
            throw new AccountFetchException(ctx.T(
                $"{service} beklenmeyen bir yanıt döndürdü. Biraz sonra tekrar denenecek.",
                $"{service} returned an unexpected response. It will be retried shortly."));
        }
    }

    private static async Task<object> FetchStripeAsync(AccountFetchContext ctx, CancellationToken ct)
    {
        long since = FinanceParsers.LocalMidnightUtc(DateTimeOffset.UtcNow, TimeZoneInfo.Local, FinanceParsers.Days - 1).ToUnixTimeSeconds();
        var all = new List<StripeTxn>();
        string? after = null;
        for (int page = 0; page < 5; page++)
        {
            string url = "https://api.stripe.com/v1/balance_transactions?limit=100&type=charge&created%5Bgte%5D="
                + since.ToString(CultureInfo.InvariantCulture)
                + (after is null ? string.Empty : "&starting_after=" + Uri.EscapeDataString(after));
            string json = await GetAsync(ctx, url, "application/json", ct);
            var result = Parse(ctx, "Stripe", () => FinanceParsers.ParseStripeBalanceTransactions(json));
            all.AddRange(result.Items);
            if (!result.HasMore || result.LastId is null) break;
            after = result.LastId;
        }
        return FinanceParsers.BuildStripeRevenue(all, DateTimeOffset.UtcNow, TimeZoneInfo.Local);
    }

    private static async Task<object> FetchLemonSqueezyAsync(AccountFetchContext ctx, CancellationToken ct)
    {
        string json = await GetAsync(ctx, "https://api.lemonsqueezy.com/v1/stores", "application/vnd.api+json", ct);
        return Parse(ctx, "Lemon Squeezy", () => FinanceParsers.ParseLemonSqueezyStores(json));
    }

    private static async Task<object> FetchGumroadAsync(AccountFetchContext ctx, CancellationToken ct)
    {
        string json = await GetAsync(ctx, "https://api.gumroad.com/v2/products", "application/json", ct);
        var sales = Parse(ctx, "Gumroad", () => FinanceParsers.ParseGumroadProducts(json));
        if (!sales.Success)
        {
            throw new AccountFetchException(ctx.T("Gumroad isteği reddetti", "Gumroad refused the request")
                + (sales.Message is { Length: > 0 } m ? ": " + m : "."));
        }
        return sales;
    }

    private static async Task<object> FetchPolarAsync(AccountFetchContext ctx, CancellationToken ct)
    {
        string json = await GetAsync(ctx, "https://api.polar.sh/v1/orders/?limit=100&sorting=-created_at", "application/json", ct);
        return Parse(ctx, "Polar", () => FinanceParsers.ParsePolarOrders(json, DateTimeOffset.UtcNow, TimeZoneInfo.Local));
    }

    private static async Task<object> FetchPaddleAsync(AccountFetchContext ctx, CancellationToken ct)
    {
        // Sandbox keys (pdl_sdbx_…) only work against the sandbox host.
        string host = ctx.Require("key").Contains("_sdbx_", StringComparison.Ordinal) ? "https://sandbox-api.paddle.com" : "https://api.paddle.com";
        string since = DateTimeOffset.UtcNow.AddDays(-FinanceParsers.Days).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        string? url = host + "/transactions?status=completed&per_page=200&created_at%5BGTE%5D=" + Uri.EscapeDataString(since);
        var all = new List<PaddleTxn>();
        for (int page = 0; page < 3 && url is not null; page++)
        {
            string json = await GetAsync(ctx, url, "application/json", ct);
            var result = Parse(ctx, "Paddle", () => FinanceParsers.ParsePaddleTransactions(json));
            all.AddRange(result.Items);
            url = result.Next;
        }
        return FinanceParsers.BuildPaddleRevenue(all, DateTimeOffset.UtcNow, TimeZoneInfo.Local);
    }

    // ---- Shared visuals -------------------------------------------------------------------------

    /// <summary>Reveals an element left to right with an animated opacity mask (charts "drawing in").</summary>
    private static void DrawIn(UIElement el, double ms = 900, double delayMs = 0)
    {
        var mid = new GradientStop(Colors.Black, 0);
        var edge = new GradientStop(Colors.Transparent, 0);
        var mask = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
        mask.GradientStops.Add(new GradientStop(Colors.Black, 0));
        mask.GradientStops.Add(mid);
        mask.GradientStops.Add(edge);
        el.OpacityMask = mask;
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        var begin = TimeSpan.FromMilliseconds(delayMs);
        var reveal = new DoubleAnimation(-0.08, 1.0, TimeSpan.FromMilliseconds(ms)) { BeginTime = begin, EasingFunction = ease };
        var lead = new DoubleAnimation(0.0, 1.08, TimeSpan.FromMilliseconds(ms)) { BeginTime = begin, EasingFunction = ease };
        lead.Completed += (_, _) =>
        {
            if (ReferenceEquals(el.OpacityMask, mask)) el.OpacityMask = null;
        };
        mid.BeginAnimation(GradientStop.OffsetProperty, reveal);
        edge.BeginAnimation(GradientStop.OffsetProperty, lead);
    }

    /// <summary>Two-column tile: big value + caption on the left, a chart on the right.</summary>
    private static Grid ValueChartGrid(UIElement left, UIElement right, double h, GridLength rightWidth)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = rightWidth });
        g.Children.Add(left);
        if (right is FrameworkElement fe) fe.Margin = new Thickness(Math.Round(h * 0.14), 0, 0, 0);
        Grid.SetColumn(right, 1);
        g.Children.Add(right);
        return g;
    }

    private static (TextBlock Amount, TextBlock Caption, StackPanel Column) ValueColumn(DockWidgetPalette p, double h)
    {
        var amount = W.Text(p, "—", Math.Max(9.5, Math.Round(h * 0.36)), p.Foreground, FontWeights.SemiBold);
        var caption = W.Text(p, string.Empty, Math.Max(7.5, Math.Round(h * 0.23)), p.SecondaryForeground);
        return (amount, caption, W.Column(amount, caption));
    }

    /// <summary>Big number with a caption under it, for the top of a card.</summary>
    private static StackPanel CardHeadline(DockWidgetPalette p, string value, string caption)
    {
        var big = W.Text(p, value, 26, p.Foreground, FontWeights.SemiBold);
        var cap = W.Text(p, caption, 11.5, p.TertiaryForeground, ui: false);
        var col = W.Column(big, cap);
        col.Margin = new Thickness(0, 0, 0, 8);
        W.PopIn(col);
        return col;
    }

    private static Sparkline CardChart(DockWidgetPalette p, IReadOnlyList<double> values)
    {
        var chart = new Sparkline { Height = 84, StrokeThickness = 1.8, Stroke = p.AccentBrush, Margin = new Thickness(0, 2, 0, 10) };
        chart.SetValues(values);
        DrawIn(chart, 1000, 120);
        return chart;
    }

    private static Border OpenButton(DockWidgetContext ctx, string label, string url)
    {
        var b = W.Button(ctx.Palette, label, () => DockWidgetActions.OpenUrl(ctx, url), "");
        b.Margin = new Thickness(0, 10, 0, 0);
        b.HorizontalAlignment = HorizontalAlignment.Left;
        return b;
    }

    private static TextBlock EmptyLine(DockWidgetContext ctx, string tr, string en)
    {
        var t = W.Text(ctx.Palette, ctx.T(tr, en), 12.5, ctx.Palette.SecondaryForeground, ui: false);
        t.TextWrapping = TextWrapping.Wrap;
        t.Margin = new Thickness(0, 4, 0, 4);
        return t;
    }

    private static TextBlock SectionTitle(DockWidgetPalette p, string text)
    {
        var t = W.Text(p, text, 11.5, p.TertiaryForeground, FontWeights.SemiBold, ui: false);
        t.Margin = new Thickness(0, 8, 0, 4);
        return t;
    }

    private static CultureInfo Culture(DockWidgetContext ctx) => CultureInfo.GetCultureInfo(ctx.En ? "en-US" : "tr-TR");

    private static string ChangeLabel(double pct) =>
        (pct >= 0 ? "▲ " : "▼ ") + Math.Abs(pct).ToString(Math.Abs(pct) >= 10 ? "0" : "0.#", CultureInfo.InvariantCulture) + "%";

    /// <summary>A row of tiny bars that grow from the bottom, staggered.</summary>
    private sealed class MiniBars : StackPanel
    {
        private readonly List<(Border Bar, ScaleTransform Scale)> _bars = [];

        public MiniBars(int count, double barWidth, double gap, double height, Brush fill)
        {
            Orientation = Orientation.Horizontal;
            VerticalAlignment = VerticalAlignment.Center;
            Height = height;
            for (int i = 0; i < count; i++)
            {
                var st = new ScaleTransform(1, 0.08);
                var bar = new Border
                {
                    Width = barWidth,
                    Height = height,
                    CornerRadius = new CornerRadius(barWidth / 2),
                    Background = fill,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    RenderTransformOrigin = new Point(0.5, 1),
                    RenderTransform = st,
                    Margin = new Thickness(i == 0 ? 0 : gap, 0, 0, 0)
                };
                _bars.Add((bar, st));
                Children.Add(bar);
            }
        }

        public void SetValues(IReadOnlyList<int> values)
        {
            int max = 1;
            foreach (var v in values) max = Math.Max(max, v);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            for (int i = 0; i < _bars.Count; i++)
            {
                int v = i < values.Count ? values[i] : 0;
                double target = v <= 0 ? 0.08 : 0.25 + 0.75 * v / max;
                _bars[i].Bar.Opacity = v <= 0 ? 0.35 : 1.0;
                _bars[i].Scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(target, TimeSpan.FromMilliseconds(460))
                {
                    BeginTime = TimeSpan.FromMilliseconds(45 * i),
                    EasingFunction = ease
                });
            }
        }
    }

    // ---- stripe-revenue -------------------------------------------------------------------------

    private sealed class StripeRevenueTile : AccountTileBase<StripeRevenue>
    {
        private TextBlock _amount = null!;
        private TextBlock _caption = null!;
        private Sparkline _spark = null!;
        private StripeRevenue? _sample;
        private IReadOnlyList<double>? _lastSeries;

        public StripeRevenueTile(DockWidgetDescriptor d, DockWidgetContext ctx) : base(d, ctx) { }

        protected override double CardWidth => 330;

        protected override StripeRevenue Sample(DockWidgetContext ctx)
        {
            if (_sample is not null) return _sample;
            double[] daily =
            [
                312.40, 287.10, 405.80, 366.25, 298.00, 198.50, 176.90, 344.60, 389.75, 421.30,
                402.10, 455.95, 233.40, 210.80, 398.20, 436.70, 467.15, 512.40, 489.90, 266.30,
                241.75, 452.60, 498.35, 530.10, 561.80, 546.25, 301.40, 288.95, 574.30, 318.60
            ];
            int days = Math.Clamp(ctx.Now.Day, 1, daily.Length);
            int best = Array.IndexOf(daily, daily.Max());
            return _sample = new StripeRevenue("USD", daily[^1], daily.Skip(daily.Length - days).Sum(), daily.Sum(), daily,
                12.4, daily[best], ctx.Now.Date.AddDays(best - (daily.Length - 1)));
        }

        protected override FrameworkElement BuildTile(DockWidgetContext ctx)
        {
            var p = ctx.Palette;
            double h = ctx.TileHeight;
            var (amount, caption, col) = ValueColumn(p, h);
            _amount = amount;
            _caption = caption;
            _spark = new Sparkline { Height = Math.Max(10, h * 0.56), StrokeThickness = Math.Max(1.2, h * 0.04), Stroke = p.AccentBrush, ShowHead = true };
            return ValueChartGrid(col, _spark, h, new GridLength(0.95, GridUnitType.Star));
        }

        protected override void ShowData(DockWidgetContext ctx, StripeRevenue data, bool isSample)
        {
            W.SetTextAnimated(_amount, Money(data.Today, data.Currency));
            string caption = ctx.T("Bugün", "Today") + (data.ChangePct is { } c ? "  " + ChangeLabel(c) : string.Empty);
            W.SetTextAnimated(_caption, caption);
            if (!ReferenceEquals(_lastSeries, data.Daily))
            {
                _lastSeries = data.Daily;
                _spark.SetValues(data.Daily);
                DrawIn(_spark);
            }
        }

        protected override FrameworkElement BuildCardBody(DockWidgetContext ctx, StripeRevenue data, bool isSample)
        {
            var p = ctx.Palette;
            var col = new StackPanel();
            col.Children.Add(CardHeadline(p, Money(data.Last30, data.Currency), ctx.T("Son 30 gün · ödemeler", "Last 30 days · charges")));
            if (data.Last30 <= 0)
                col.Children.Add(EmptyLine(ctx, "Son 30 günde ödeme yok.", "No charges in the last 30 days."));
            col.Children.Add(CardChart(p, data.Daily));

            var rows = new List<FrameworkElement>
            {
                W.CardRow(p, ctx.T("Bu ay", "This month"), Money(data.ThisMonth, data.Currency), glyph: ""),
                W.CardRow(p, ctx.T("Bugün", "Today"), Money(data.Today, data.Currency), glyph: ""),
                W.CardRow(p, ctx.T("En iyi gün", "Best day"),
                    data.BestDay > 0 ? $"{Money(data.BestDay, data.Currency)} · {data.BestDayDate.ToString("d MMM", Culture(ctx))}" : "—",
                    glyph: "")
            };
            if (data.ChangePct is { } c)
                rows.Add(W.CardRow(p, ctx.T("Son 7 gün / önceki 7 gün", "Last 7 days vs previous 7"), ChangeLabel(c),
                    W.Brush(c >= 0 ? p.Positive : p.Negative), ""));
            for (int i = 0; i < rows.Count; i++)
            {
                col.Children.Add(rows[i]);
                W.PopIn(rows[i], 60 + 45 * i);
            }
            col.Children.Add(OpenButton(ctx, ctx.T("Stripe'ı aç", "Open Stripe"), "https://dashboard.stripe.com"));
            return col;
        }
    }

    // ---- lemonsqueezy-revenue -------------------------------------------------------------------

    private sealed class LemonSqueezyTile : AccountTileBase<LemonSqueezyRevenue>
    {
        private TextBlock _amount = null!;
        private TextBlock _caption = null!;
        private static readonly LemonSqueezyRevenue SampleData = new(
        [
            new LemonStore("Pixel Forge", "USD", 48234.50, 3189.00, 1287, 96),
            new LemonStore("Side Project Lab", "USD", 6120.00, 412.00, 204, 14)
        ]);

        public LemonSqueezyTile(DockWidgetDescriptor d, DockWidgetContext ctx) : base(d, ctx) { }

        protected override LemonSqueezyRevenue Sample(DockWidgetContext ctx) => SampleData;

        protected override FrameworkElement BuildTile(DockWidgetContext ctx)
        {
            var (amount, caption, col) = ValueColumn(ctx.Palette, ctx.TileHeight);
            _amount = amount;
            _caption = caption;
            var g = new Grid();
            g.Children.Add(col);
            return g;
        }

        protected override void ShowData(DockWidgetContext ctx, LemonSqueezyRevenue data, bool isSample)
        {
            W.SetTextAnimated(_amount, Money(data.ThirtyDayRevenue, data.Currency));
            W.SetTextAnimated(_caption, ctx.T($"{Compact(data.ThirtyDaySales)} satış · 30 gün", $"{Compact(data.ThirtyDaySales)} sales · 30d"));
        }

        protected override FrameworkElement BuildCardBody(DockWidgetContext ctx, LemonSqueezyRevenue data, bool isSample)
        {
            var p = ctx.Palette;
            var col = new StackPanel();
            col.Children.Add(CardHeadline(p, Money(data.ThirtyDayRevenue, data.Currency),
                ctx.T($"Son 30 gün · {Compact(data.ThirtyDaySales)} satış", $"Last 30 days · {Compact(data.ThirtyDaySales)} sales")));
            var total = W.CardRow(p, ctx.T("Toplam gelir", "Total revenue"), Money(data.TotalRevenue, data.Currency), glyph: "");
            col.Children.Add(total);
            W.PopIn(total, 60);

            if (data.Stores.Count == 0)
            {
                col.Children.Add(EmptyLine(ctx, "Bu hesapta mağaza yok.", "There are no stores on this account."));
            }
            else
            {
                col.Children.Add(SectionTitle(p, ctx.T("Mağazalar", "Stores")));
                for (int i = 0; i < data.Stores.Count && i < 6; i++)
                {
                    var s = data.Stores[i];
                    var block = new StackPanel { Margin = new Thickness(0, 2, 0, 4) };
                    var head = new DockPanel();
                    var value = W.Text(p, Money(s.ThirtyDayRevenue, s.Currency), 12.5, p.Foreground, FontWeights.SemiBold);
                    DockPanel.SetDock(value, Dock.Right);
                    head.Children.Add(value);
                    head.Children.Add(W.Text(p, s.Name, 12.5, p.Foreground, FontWeights.SemiBold, ui: false));
                    block.Children.Add(head);
                    block.Children.Add(W.Text(p, ctx.T(
                            $"30 günde {Compact(s.ThirtyDaySales)} satış · toplam {Money(s.TotalRevenue, s.Currency)} ({Compact(s.TotalSales)} satış)",
                            $"{Compact(s.ThirtyDaySales)} sales in 30d · {Money(s.TotalRevenue, s.Currency)} total ({Compact(s.TotalSales)} sales)"),
                        11, p.TertiaryForeground, ui: false));
                    col.Children.Add(block);
                    W.PopIn(block, 100 + 50 * i);
                }
            }
            col.Children.Add(OpenButton(ctx, ctx.T("Lemon Squeezy'yi aç", "Open Lemon Squeezy"), "https://app.lemonsqueezy.com/dashboard"));
            return col;
        }
    }

    // ---- gumroad-sales --------------------------------------------------------------------------

    private sealed class GumroadSalesTile : AccountTileBase<GumroadSales>
    {
        private TextBlock _amount = null!;
        private TextBlock _caption = null!;
        private static readonly GumroadSales SampleData = new(true, null,
        [
            new GumroadProduct("Notion Finance Template", 1034, 29986.00, "$29", true),
            new GumroadProduct("Minimal Icon Pack", 412, 7828.00, "$19", true),
            new GumroadProduct("Figma UI Kit", 158, 6162.00, "$39", true),
            new GumroadProduct("Lightroom Presets", 287, 2583.00, "$9", true),
            new GumroadProduct("Freelance Contract Bundle", 64, 1216.00, "$19", false)
        ]);

        public GumroadSalesTile(DockWidgetDescriptor d, DockWidgetContext ctx) : base(d, ctx) { }

        protected override GumroadSales Sample(DockWidgetContext ctx) => SampleData;

        protected override FrameworkElement BuildTile(DockWidgetContext ctx)
        {
            var (amount, caption, col) = ValueColumn(ctx.Palette, ctx.TileHeight);
            _amount = amount;
            _caption = caption;
            var g = new Grid();
            g.Children.Add(col);
            return g;
        }

        protected override void ShowData(DockWidgetContext ctx, GumroadSales data, bool isSample)
        {
            W.SetTextAnimated(_amount, Money(data.TotalUsd, "USD"));
            W.SetTextAnimated(_caption, ctx.T($"{Compact(data.TotalSales)} satış", $"{Compact(data.TotalSales)} sales"));
        }

        protected override FrameworkElement BuildCardBody(DockWidgetContext ctx, GumroadSales data, bool isSample)
        {
            var p = ctx.Palette;
            var col = new StackPanel();
            col.Children.Add(CardHeadline(p, Money(data.TotalUsd, "USD"),
                ctx.T($"Toplam gelir · {Compact(data.TotalSales)} satış", $"Total revenue · {Compact(data.TotalSales)} sales")));

            var top = data.Products.Where(x => x.SalesUsd > 0 || x.SalesCount > 0).Take(5).ToList();
            if (top.Count == 0)
            {
                col.Children.Add(EmptyLine(ctx, "Henüz satış yok.", "No sales yet."));
            }
            else
            {
                col.Children.Add(SectionTitle(p, ctx.T("En çok kazandıran ürünler", "Top products by revenue")));
                double total = Math.Max(0.01, data.TotalUsd);
                var fill = p.Accent;
                for (int i = 0; i < top.Count; i++)
                {
                    var prod = top[i];
                    var block = new StackPanel { Margin = new Thickness(0, 3, 0, 5) };
                    var head = new DockPanel();
                    var value = W.Text(p, Money(prod.SalesUsd, "USD"), 12.5, p.Foreground, FontWeights.SemiBold);
                    DockPanel.SetDock(value, Dock.Right);
                    head.Children.Add(value);
                    head.Children.Add(W.Text(p, prod.Name, 12.5, p.Foreground, FontWeights.SemiBold, ui: false));
                    block.Children.Add(head);
                    string sub = ctx.T($"{Compact(prod.SalesCount)} satış", $"{Compact(prod.SalesCount)} sales")
                        + (prod.FormattedPrice.Length > 0 ? " · " + prod.FormattedPrice : string.Empty)
                        + (prod.Published ? string.Empty : ctx.T(" · yayında değil", " · unpublished"));
                    var subText = W.Text(p, sub, 11, p.TertiaryForeground, ui: false);
                    subText.Margin = new Thickness(0, 1, 0, 3);
                    block.Children.Add(subText);
                    var bar = W.Bar(p, 100, 4, fill);
                    bar.Width = double.NaN;
                    double share = prod.SalesUsd / total;
                    bar.Loaded += (_, _) => bar.AnimateTo(share, 650);
                    block.Children.Add(bar);
                    col.Children.Add(block);
                    W.PopIn(block, 80 + 55 * i);
                }
            }
            col.Children.Add(OpenButton(ctx, ctx.T("Gumroad'u aç", "Open Gumroad"), "https://app.gumroad.com/dashboard"));
            return col;
        }
    }

    // ---- polar-revenue --------------------------------------------------------------------------

    private sealed class PolarRevenueTile : AccountTileBase<PolarRevenue>
    {
        private TextBlock _amount = null!;
        private TextBlock _caption = null!;
        private MiniBars _bars = null!;
        private PolarRevenue? _sample;
        private IReadOnlyList<int>? _lastCounts;

        public PolarRevenueTile(DockWidgetDescriptor d, DockWidgetContext ctx) : base(d, ctx) { }

        protected override PolarRevenue Sample(DockWidgetContext ctx)
        {
            if (_sample is not null) return _sample;
            var now = DateTime.UtcNow;
            return _sample = new PolarRevenue("USD", 2846.00, 58,
            [
                new PolarOrder(49.00, "USD", now.AddMinutes(-18), "Pro License", "m***@g***.com"),
                new PolarOrder(9.00, "USD", now.AddHours(-3), "Starter Plan", "k***@s***.io"),
                new PolarOrder(199.00, "USD", now.AddHours(-9), "Team Plan", "o***@a***.com"),
                new PolarOrder(49.00, "USD", now.AddDays(-1).AddHours(-2), "Pro License", "z***@p***.dev"),
                new PolarOrder(9.00, "USD", now.AddDays(-2), "Starter Plan", "l***@e***.net")
            ], [6, 9, 4, 11, 8, 7, 3]);
        }

        protected override FrameworkElement BuildTile(DockWidgetContext ctx)
        {
            var p = ctx.Palette;
            double h = ctx.TileHeight;
            var (amount, caption, col) = ValueColumn(p, h);
            _amount = amount;
            _caption = caption;
            double bw = Math.Max(2, Math.Round(h * 0.065, 1));
            _bars = new MiniBars(7, bw, Math.Max(1, Math.Round(h * 0.03, 1)), Math.Max(10, h * 0.5), p.AccentBrush);
            return ValueChartGrid(col, _bars, h, GridLength.Auto);
        }

        protected override void ShowData(DockWidgetContext ctx, PolarRevenue data, bool isSample)
        {
            W.SetTextAnimated(_amount, Money(data.Last30, data.Currency));
            W.SetTextAnimated(_caption, ctx.T($"{Compact(data.Count30)} sipariş", $"{Compact(data.Count30)} orders"));
            if (!ReferenceEquals(_lastCounts, data.DailyCounts))
            {
                _lastCounts = data.DailyCounts;
                _bars.SetValues(data.DailyCounts);
            }
        }

        protected override FrameworkElement BuildCardBody(DockWidgetContext ctx, PolarRevenue data, bool isSample)
        {
            var p = ctx.Palette;
            var col = new StackPanel();
            col.Children.Add(CardHeadline(p, Money(data.Last30, data.Currency),
                ctx.T($"Son 30 gün · {Compact(data.Count30)} sipariş", $"Last 30 days · {Compact(data.Count30)} orders")));
            var avg = W.CardRow(p, ctx.T("Ortalama sipariş", "Average order"),
                data.Count30 > 0 ? Money(data.Last30 / data.Count30, data.Currency) : "—", glyph: "");
            col.Children.Add(avg);
            W.PopIn(avg, 60);

            col.Children.Add(SectionTitle(p, ctx.T("Son siparişler", "Latest orders")));
            if (data.Latest.Count == 0)
                col.Children.Add(EmptyLine(ctx, "Henüz sipariş yok.", "No orders yet."));
            for (int i = 0; i < data.Latest.Count; i++)
            {
                var o = data.Latest[i];
                var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
                var amount = W.Text(p, Money(o.Amount, o.Currency), 12.5, p.Foreground, FontWeights.SemiBold);
                amount.HorizontalAlignment = HorizontalAlignment.Right;
                string when = o.CreatedUtc == DateTime.MinValue ? string.Empty
                    : Ago(ctx, DateTime.SpecifyKind(o.CreatedUtc, DateTimeKind.Utc).ToLocalTime());
                var ago = W.Text(p, when, 11, p.TertiaryForeground, ui: false);
                ago.HorizontalAlignment = HorizontalAlignment.Right;
                var right = W.Column(amount, ago);
                right.Margin = new Thickness(8, 0, 0, 0);
                DockPanel.SetDock(right, Dock.Right);
                row.Children.Add(right);
                var name = W.Text(p, o.Product.Length > 0 ? o.Product : ctx.T("Sipariş", "Order"), 12.5, p.Foreground, ui: false);
                var who = W.Text(p, o.MaskedEmail, 11, p.TertiaryForeground, mono: true);
                row.Children.Add(W.Column(name, who));
                col.Children.Add(row);
                W.PopIn(row, 100 + 50 * i);
            }
            col.Children.Add(OpenButton(ctx, ctx.T("Polar'ı aç", "Open Polar"), "https://polar.sh/dashboard"));
            return col;
        }
    }

    // ---- paddle-revenue -------------------------------------------------------------------------

    private sealed class PaddleRevenueTile : AccountTileBase<PaddleRevenue>
    {
        private TextBlock _amount = null!;
        private TextBlock _caption = null!;
        private Sparkline _spark = null!;
        private IReadOnlyList<double>? _lastSeries;
        private static readonly PaddleRevenue SampleData = BuildSample();

        public PaddleRevenueTile(DockWidgetDescriptor d, DockWidgetContext ctx) : base(d, ctx) { }

        private static PaddleRevenue BuildSample()
        {
            double[] daily =
            [
                148.00, 97.00, 246.00, 198.00, 49.00, 0.00, 98.00, 197.00, 295.00, 148.00,
                246.00, 99.00, 49.00, 147.00, 344.00, 198.00, 296.00, 245.00, 98.00, 49.00,
                197.00, 392.00, 246.00, 295.00, 148.00, 99.00, 0.00, 247.00, 343.00, 196.00
            ];
            return new PaddleRevenue("USD", daily.Sum(), 112, daily);
        }

        protected override PaddleRevenue Sample(DockWidgetContext ctx) => SampleData;

        protected override FrameworkElement BuildTile(DockWidgetContext ctx)
        {
            var p = ctx.Palette;
            double h = ctx.TileHeight;
            var (amount, caption, col) = ValueColumn(p, h);
            _amount = amount;
            _caption = caption;
            _spark = new Sparkline { Height = Math.Max(10, h * 0.5), StrokeThickness = Math.Max(1.2, h * 0.04), Stroke = p.AccentBrush, ShowHead = true };
            return ValueChartGrid(col, _spark, h, new GridLength(0.8, GridUnitType.Star));
        }

        protected override void ShowData(DockWidgetContext ctx, PaddleRevenue data, bool isSample)
        {
            W.SetTextAnimated(_amount, Money(data.Total, data.Currency));
            W.SetTextAnimated(_caption, ctx.T($"{Compact(data.Count)} işlem", $"{Compact(data.Count)} sales"));
            if (!ReferenceEquals(_lastSeries, data.Daily))
            {
                _lastSeries = data.Daily;
                _spark.SetValues(data.Daily);
                DrawIn(_spark);
            }
        }

        protected override FrameworkElement BuildCardBody(DockWidgetContext ctx, PaddleRevenue data, bool isSample)
        {
            var p = ctx.Palette;
            var col = new StackPanel();
            col.Children.Add(CardHeadline(p, Money(data.Total, data.Currency),
                ctx.T("Son 30 gün · tamamlanan işlemler", "Last 30 days · completed transactions")));
            if (data.Count == 0)
                col.Children.Add(EmptyLine(ctx, "Son 30 günde tamamlanan işlem yok.", "No completed transactions in the last 30 days."));
            col.Children.Add(CardChart(p, data.Daily));
            var rows = new FrameworkElement[]
            {
                W.CardRow(p, ctx.T("İşlem sayısı", "Transactions"), data.Count.ToString("#,0", CultureInfo.InvariantCulture), glyph: ""),
                W.CardRow(p, ctx.T("Ortalama sipariş", "Average order"), data.Count > 0 ? Money(data.Average, data.Currency) : "—", glyph: ""),
                W.CardRow(p, ctx.T("Bugün", "Today"), Money(data.Daily.Count > 0 ? data.Daily[^1] : 0, data.Currency), glyph: "")
            };
            for (int i = 0; i < rows.Length; i++)
            {
                col.Children.Add(rows[i]);
                W.PopIn(rows[i], 60 + 45 * i);
            }
            col.Children.Add(OpenButton(ctx, ctx.T("Paddle'ı aç", "Open Paddle"), "https://vendors.paddle.com"));
            return col;
        }
    }
}
