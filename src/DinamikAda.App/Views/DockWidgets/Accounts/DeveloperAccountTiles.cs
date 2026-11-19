using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using DinamikAda.App.Services.DockWidgets;
using DinamikAda.Core.Accounts;
using DinamikAda.Core.Widgets;

namespace DinamikAda.App.Views.DockWidgets.Accounts;

/// <summary>Developer and analytics account widgets: github-activity, github-notifications, linear-issues, resend-emails, plausible-visitors.</summary>
public static class DeveloperAccountTiles
{
    public static void RegisterFetchers(Action<string, string, AccountFetcher> add)
    {
        add("github-activity", "github", FetchGitHubActivityAsync);
        add("github-notifications", "github", FetchGitHubNotificationsAsync);
        add("linear-issues", "linear", FetchLinearAsync);
        add("resend-emails", "resend", FetchResendAsync);
        add("plausible-visitors", "plausible", FetchPlausibleAsync);
    }

    public static void Register(Dictionary<string, Func<DockWidgetDescriptor, DockWidgetContext, IDockWidgetTile>> map)
    {
        map["github-activity"] = (d, c) => new GitHubActivityTile(d, c);
        map["github-notifications"] = (d, c) => new GitHubNotificationsTile(d, c);
        map["linear-issues"] = (d, c) => new LinearIssuesTile(d, c);
        map["resend-emails"] = (d, c) => new ResendEmailsTile(d, c);
        map["plausible-visitors"] = (d, c) => new PlausibleVisitorsTile(d, c);
    }

    // ---- Fetchers -------------------------------------------------------------------------------

    /// <summary>Runs a Core parser and turns malformed payloads / service errors into friendly bilingual messages.</summary>
    private static T Parse<T>(AccountFetchContext ctx, string service, Func<T> parse)
    {
        try
        {
            return parse();
        }
        catch (DeveloperApiException ex)
        {
            throw new AccountFetchException(ctx.T($"{service} hatası: {ex.Message}", $"{service} error: {ex.Message}"));
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException or KeyNotFoundException or IndexOutOfRangeException)
        {
            throw new AccountFetchException(ctx.T($"{service} yanıtı okunamadı.", $"Could not read the {service} response."));
        }
    }

    /// <summary>For non-2xx responses: 401/403/404/429 go to the hub's friendly messages, others surface the service's own error text.</summary>
    private static void EnsureSuccess(AccountFetchContext ctx, HttpResponseMessage resp, string body, string service)
    {
        if (resp.IsSuccessStatusCode) return;
        if (resp.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound or (HttpStatusCode)429)
            && DeveloperParsers.TryReadErrorMessage(body) is { } msg)
        {
            if (msg.Length > 160) msg = msg[..160] + "…";
            throw new AccountFetchException(ctx.T($"{service} hatası: {msg}", $"{service} error: {msg}"));
        }
        resp.EnsureSuccessStatusCode();
    }

    private static string GitHubUser(AccountFetchContext ctx) => ctx.Require("user").Trim().TrimStart('@').Trim('/');

    private static async Task<object> FetchGitHubActivityAsync(AccountFetchContext ctx, CancellationToken ct)
    {
        string user = GitHubUser(ctx);
        var today = DateOnly.FromDateTime(DateTime.Now);
        string? token = ctx.Get("key");
        if (!string.IsNullOrWhiteSpace(token))
        {
            try
            {
                return await FetchGitHubGraphQlAsync(ctx, user, token.Trim(), today, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The token may lack access (it is only required for notifications): fall back to the public grid.
            }
        }

        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://github.com/users/{Uri.EscapeDataString(user)}/contributions");
        req.Headers.Accept.ParseAdd("text/html");
        using var resp = await ctx.Http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        string html = await resp.Content.ReadAsStringAsync(ct);
        return Parse(ctx, "GitHub", () => DeveloperParsers.ParseGitHubContributionsHtml(html, today));
    }

    private static async Task<GitHubContributions> FetchGitHubGraphQlAsync(AccountFetchContext ctx, string user, string token, DateOnly today, CancellationToken ct)
    {
        const string query = "query($login:String!){user(login:$login){contributionsCollection{contributionCalendar{totalContributions weeks{contributionDays{date contributionCount contributionLevel}}}}}}";
        string body = JsonSerializer.Serialize(new { query, variables = new { login = user } });
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.github.com/graphql")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Accept.ParseAdd("application/json");
        using var resp = await ctx.Http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        string json = await resp.Content.ReadAsStringAsync(ct);
        return Parse(ctx, "GitHub", () => DeveloperParsers.ParseGitHubContributionsGraphQl(json, today));
    }

    private static async Task<object> FetchGitHubNotificationsAsync(AccountFetchContext ctx, CancellationToken ct)
    {
        string? token = ctx.Get("key");
        if (string.IsNullOrWhiteSpace(token))
            throw new AccountFetchException(ctx.T(
                "Bildirimler için bir GitHub erişim jetonu gerekli. Kartta Düzenle'ye bas ve \"Notifications\" okuma izinli bir jeton ekle.",
                "Notifications need a GitHub access token. Press Edit on the card and add a token with read access to Notifications."));
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/notifications?per_page=30");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        req.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        using var resp = await ctx.Http.SendAsync(req, ct);
        string json = await resp.Content.ReadAsStringAsync(ct);
        EnsureSuccess(ctx, resp, json, "GitHub");
        return Parse(ctx, "GitHub", () => DeveloperParsers.ParseGitHubNotifications(json));
    }

    private static async Task<object> FetchLinearAsync(AccountFetchContext ctx, CancellationToken ct)
    {
        string body = JsonSerializer.Serialize(new { query = DeveloperParsers.LinearQuery });
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.linear.app/graphql")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        // Personal API keys go in the header as-is (no "Bearer" prefix).
        req.Headers.TryAddWithoutValidation("Authorization", ctx.Require("key").Trim());
        req.Headers.Accept.ParseAdd("application/json");
        using var resp = await ctx.Http.SendAsync(req, ct);
        string json = await resp.Content.ReadAsStringAsync(ct);
        EnsureSuccess(ctx, resp, json, "Linear");
        return Parse(ctx, "Linear", () => DeveloperParsers.ParseLinearIssues(json));
    }

    private static async Task<object> FetchResendAsync(AccountFetchContext ctx, CancellationToken ct)
    {
        // One page of up to 100 so the 7-day counts are meaningful; the card shows the latest 20.
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.resend.com/emails?limit=100");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ctx.Require("key").Trim());
        req.Headers.Accept.ParseAdd("application/json");
        using var resp = await ctx.Http.SendAsync(req, ct);
        string json = await resp.Content.ReadAsStringAsync(ct);
        EnsureSuccess(ctx, resp, json, "Resend");
        var now = DateTime.UtcNow;
        return Parse(ctx, "Resend", () => DeveloperParsers.ParseResendEmails(json, now));
    }

    private static async Task<object> FetchPlausibleAsync(AccountFetchContext ctx, CancellationToken ct)
    {
        string key = ctx.Require("key").Trim();
        string site = ctx.Require("site").Trim();
        if (site.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) site = site[8..];
        else if (site.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) site = site[7..];
        site = site.TrimEnd('/');
        string host = (ctx.Get("host") ?? string.Empty).Trim().TrimEnd('/');
        if (host.Length == 0) host = "https://plausible.io";
        if (!host.Contains("://", StringComparison.Ordinal)) host = "https://" + host;
        if (!Uri.TryCreate(host, UriKind.Absolute, out var hostUri) || (hostUri.Scheme != Uri.UriSchemeHttps && hostUri.Scheme != Uri.UriSchemeHttp))
            throw new AccountFetchException(ctx.T("Sunucu adresi geçersiz (ör. https://plausible.io).", "The host address is invalid (e.g. https://plausible.io)."));

        var today = DateOnly.FromDateTime(DateTime.Now);
        using var req = new HttpRequestMessage(HttpMethod.Post, host + "/api/v2/query")
        {
            Content = new StringContent(DeveloperParsers.PlausibleQueryBody(site, today), Encoding.UTF8, "application/json")
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        req.Headers.Accept.ParseAdd("application/json");
        using var resp = await ctx.Http.SendAsync(req, ct);
        string json = await resp.Content.ReadAsStringAsync(ct);
        EnsureSuccess(ctx, resp, json, "Plausible");

        int realtime = 0;
        try
        {
            using var rt = new HttpRequestMessage(HttpMethod.Get, host + "/api/v1/stats/realtime/visitors?site_id=" + Uri.EscapeDataString(site));
            rt.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var rtResp = await ctx.Http.SendAsync(rt, ct);
            if (rtResp.IsSuccessStatusCode) realtime = DeveloperParsers.ParsePlausibleRealtime(await rtResp.Content.ReadAsStringAsync(ct));
        }
        catch (HttpRequestException)
        {
            // Realtime is a bonus; the daily series is still useful without it.
        }
        return Parse(ctx, "Plausible", () => DeveloperParsers.ParsePlausibleStats(json, realtime, today));
    }

    // ---- Shared tile helpers --------------------------------------------------------------------

    /// <summary>Usable content width of a tile (chip width minus the base padding).</summary>
    private static double InnerWidth(DockWidgetContext ctx, double widthUnits)
    {
        double h = ctx.TileHeight;
        double chip = Math.Round(h * widthUnits + (widthUnits > 1 ? (widthUnits - 1) * 6 : 0));
        return Math.Max(10, chip - 2 * Math.Round(h * 0.22));
    }

    /// <summary>Reveals an element left to right (charts "drawing in").</summary>
    private static void DrawIn(FrameworkElement el, double width, double height, double delayMs = 0, double ms = 720)
    {
        var clip = new RectangleGeometry(new Rect(0, 0, 0, height + 4));
        el.Clip = clip;
        clip.BeginAnimation(RectangleGeometry.RectProperty, new RectAnimation(new Rect(0, -2, width + 4, height + 4), TimeSpan.FromMilliseconds(ms))
        {
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
            EasingFunction = Motion.Soft
        });
    }

    /// <summary>A bigger spring "pop" for a number that went up.</summary>
    private static void Pop(FrameworkElement el, double from = 1.45)
    {
        if (el.RenderTransform is not ScaleTransform st || st.IsFrozen)
        {
            st = new ScaleTransform(1, 1);
            el.RenderTransformOrigin = new Point(0.5, 0.5);
            el.RenderTransform = st;
        }
        var anim = new DoubleAnimation(from, 1, TimeSpan.FromMilliseconds(620))
        {
            EasingFunction = Motion.Soft
        };
        st.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        st.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
    }

    private static void FadeIn(UIElement el, double delayMs, double ms = 260)
    {
        el.Opacity = 0;
        el.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(ms))
        {
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
            EasingFunction = Motion.Soft
        });
    }

    /// <summary>A clickable card row; clicks don't reach the dock.</summary>
    private static Border ClickRow(DockWidgetPalette p, UIElement content, Action onClick, string? tooltip = null)
    {
        var b = new Border
        {
            Child = content,
            Background = p.CardRowBackground,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(0, 0, 0, 4),
            Cursor = Cursors.Hand,
            ToolTip = tooltip
        };
        b.MouseEnter += (_, _) => b.Opacity = 0.82;
        b.MouseLeave += (_, _) => b.Opacity = 1.0;
        b.MouseLeftButtonDown += (_, e) => e.Handled = true;
        b.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            W.Pulse(b, 0.97);
            onClick();
        };
        return b;
    }

    private static TextBlock Empty(DockWidgetContext ctx, string tr, string en)
    {
        var t = W.Text(ctx.Palette, ctx.T(tr, en), 12.5, ctx.Palette.SecondaryForeground, ui: false);
        t.TextWrapping = TextWrapping.Wrap;
        t.Margin = new Thickness(0, 2, 0, 6);
        return t;
    }

    private static Border LeftButton(Border b, double top = 8)
    {
        b.Margin = new Thickness(0, top, 6, 0);
        b.HorizontalAlignment = HorizontalAlignment.Left;
        return b;
    }

    private static string N(int v) => v.ToString("#,0", CultureInfo.InvariantCulture);

    private static string AgoUtc(DockWidgetContext ctx, DateTime utc) =>
        utc.Year < 2000 ? string.Empty : AgoText(ctx, utc.ToLocalTime());

    private static string AgoText(DockWidgetContext ctx, DateTime local)
    {
        var d = ctx.Now - local;
        if (d.TotalMinutes < 1) return ctx.T("az önce", "just now");
        if (d.TotalHours < 1) return ctx.T($"{(int)d.TotalMinutes} dk önce", $"{(int)d.TotalMinutes}m ago");
        if (d.TotalDays < 1) return ctx.T($"{(int)d.TotalHours} sa önce", $"{(int)d.TotalHours}h ago");
        return ctx.T($"{(int)d.TotalDays} gün önce", $"{(int)d.TotalDays}d ago");
    }

    // ---- github-activity ------------------------------------------------------------------------

    private sealed class GitHubActivityTile : AccountTileBase<GitHubContributions>
    {
        private const int TileWeeks = 16;
        private Border[,] _cells = null!;
        private TextBlock _streak = null!;
        private TextBlock _caption = null!;
        private Brush[] _levels = [];
        private Color _levelsFor;

        public GitHubActivityTile(DockWidgetDescriptor d, DockWidgetContext ctx) : base(d, ctx) { }

        protected override GitHubContributions Sample(DockWidgetContext ctx)
        {
            var today = DateOnly.FromDateTime(ctx.Now);
            var rng = new Random(20261119);
            var days = new List<GitHubContributionDay>();
            for (int i = 370; i >= 0; i--)
            {
                var d = today.AddDays(-i);
                bool weekend = d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
                double busy = 0.55 + 0.35 * Math.Sin(i / 23.0); // quieter and busier stretches
                int count = rng.NextDouble() < (weekend ? 0.28 : 0.72) * busy ? 1 + (int)(rng.NextDouble() * rng.NextDouble() * (weekend ? 6 : 14)) : 0;
                if (i < 9) count = Math.Max(count, 1 + (i * 7 + 3) % 6); // a live streak
                if (i == 9) count = 0;
                days.Add(new GitHubContributionDay(d, count, Level(count)));
            }
            var (cur, longest) = DeveloperParsers.ComputeStreaks(days, today);
            return new GitHubContributions(days, days.Sum(x => x.Count), cur, longest);
        }

        private static int Level(int c) => c <= 0 ? 0 : c <= 2 ? 1 : c <= 5 ? 2 : c <= 9 ? 3 : 4;

        private Brush[] Levels(DockWidgetPalette p)
        {
            if (_levels.Length == 5 && _levelsFor == p.Accent) return _levels;
            _levelsFor = p.Accent;
            _levels = [p.Track, W.Brush(p.Accent, 0x55), W.Brush(p.Accent, 0x8C), W.Brush(p.Accent, 0xC4), W.Brush(p.Accent)];
            return _levels;
        }

        private static Canvas BuildGrid(int weeks, double pitch, double cell, out Border[,] cells)
        {
            var canvas = new Canvas { Width = weeks * pitch, Height = 7 * pitch, VerticalAlignment = VerticalAlignment.Center, SnapsToDevicePixels = true };
            cells = new Border[weeks, 7];
            double radius = Math.Max(1, cell * 0.3);
            for (int c = 0; c < weeks; c++)
            {
                for (int r = 0; r < 7; r++)
                {
                    var b = new Border { Width = cell, Height = cell, CornerRadius = new CornerRadius(radius) };
                    Canvas.SetLeft(b, c * pitch);
                    Canvas.SetTop(b, r * pitch);
                    canvas.Children.Add(b);
                    cells[c, r] = b;
                }
            }
            return canvas;
        }

        /// <summary>Colours the last <c>weeks</c> columns (Sunday-first) and fades them in column by column.</summary>
        private static void Paint(Border[,] cells, GitHubContributions data, DateOnly today, Brush[] levels, double staggerMs)
        {
            int weeks = cells.GetLength(0);
            var map = new Dictionary<DateOnly, int>(data.Days.Count);
            foreach (var d in data.Days) map[d.Date] = d.Level;
            var firstSunday = today.AddDays(-(int)today.DayOfWeek - 7 * (weeks - 1));
            for (int c = 0; c < weeks; c++)
            {
                for (int r = 0; r < 7; r++)
                {
                    var date = firstSunday.AddDays(c * 7 + r);
                    var b = cells[c, r];
                    if (date > today)
                    {
                        b.BeginAnimation(UIElement.OpacityProperty, null);
                        b.Opacity = 0;
                        continue;
                    }
                    b.Background = levels[map.TryGetValue(date, out var l) ? Math.Clamp(l, 0, 4) : 0];
                    FadeIn(b, c * staggerMs, 240);
                }
            }
        }

        private static DateOnly LastDay(DockWidgetContext ctx, GitHubContributions data) =>
            data.Days.Count > 0 ? data.Days[^1].Date : DateOnly.FromDateTime(ctx.Now);

        protected override FrameworkElement BuildTile(DockWidgetContext ctx)
        {
            var p = ctx.Palette;
            double h = ctx.TileHeight;
            double pitch = Math.Max(2.6, Math.Floor((h - 5) / 7 * 10) / 10);
            var grid = BuildGrid(TileWeeks, pitch, pitch * 0.78, out _cells);
            _streak = W.Text(p, "0", Math.Round(h * 0.36), p.Foreground, FontWeights.SemiBold);
            _caption = W.Text(p, ctx.T("seri", "streak"), Math.Max(7, Math.Round(h * 0.21)), p.SecondaryForeground);
            _streak.HorizontalAlignment = HorizontalAlignment.Center;
            _caption.HorizontalAlignment = HorizontalAlignment.Center;
            var texts = W.Column(_streak, _caption);
            texts.Margin = new Thickness(Math.Round(h * 0.14), 0, 0, 0);
            var root = new Grid();
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(texts, 1);
            root.Children.Add(grid);
            root.Children.Add(texts);
            return root;
        }

        protected override void ShowData(DockWidgetContext ctx, GitHubContributions data, bool isSample)
        {
            Paint(_cells, data, LastDay(ctx, data), Levels(ctx.Palette), 32);
            W.SetTextAnimated(_streak, data.CurrentStreak.ToString(CultureInfo.InvariantCulture));
            _streak.Foreground = data.CurrentStreak > 0 ? ctx.Palette.Foreground : ctx.Palette.SecondaryForeground;
            _caption.ToolTip = ctx.T($"{data.CurrentStreak} günlük seri", $"{data.CurrentStreak}-day streak");
        }

        protected override FrameworkElement BuildCardBody(DockWidgetContext ctx, GitHubContributions data, bool isSample)
        {
            var p = ctx.Palette;
            var col = new StackPanel();
            if (data.Days.Count == 0)
            {
                col.Children.Add(Empty(ctx, "Katkı bilgisi bulunamadı.", "No contribution data found."));
                return col;
            }

            var today = LastDay(ctx, data);
            const int weeks = 53;
            const double pitch = 6.0;
            var grid = BuildGrid(weeks, pitch, 4.8, out var cells);
            grid.HorizontalAlignment = HorizontalAlignment.Left;
            Paint(cells, data, today, Levels(p), 9);

            // Month labels over the first week that contains the 1st of a month.
            var months = new Canvas { Height = 14, Width = weeks * pitch, HorizontalAlignment = HorizontalAlignment.Left };
            var culture = CultureInfo.GetCultureInfo(ctx.En ? "en-US" : "tr-TR");
            var firstSunday = today.AddDays(-(int)today.DayOfWeek - 7 * (weeks - 1));
            double lastX = -100;
            for (int c = 0; c < weeks; c++)
            {
                var weekStart = firstSunday.AddDays(c * 7);
                var firstOfMonth = Enumerable.Range(0, 7).Select(weekStart.AddDays).FirstOrDefault(d => d.Day == 1 && d <= today);
                if (firstOfMonth == default || c * pitch - lastX < 24) continue;
                var label = W.Text(p, culture.DateTimeFormat.GetAbbreviatedMonthName(firstOfMonth.Month), 9.5, p.TertiaryForeground, ui: false);
                Canvas.SetLeft(label, c * pitch);
                months.Children.Add(label);
                lastX = c * pitch;
            }
            col.Children.Add(months);
            col.Children.Add(grid);

            // Legend.
            var legend = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 8) };
            legend.Children.Add(W.Text(p, ctx.T("Az", "Less"), 9.5, p.TertiaryForeground, ui: false));
            foreach (var b in Levels(p))
                legend.Children.Add(new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(2), Background = b, Margin = new Thickness(3, 0, 0, 0) });
            var more = W.Text(p, ctx.T("Çok", "More"), 9.5, p.TertiaryForeground, ui: false);
            more.Margin = new Thickness(4, 0, 0, 0);
            legend.Children.Add(more);
            col.Children.Add(legend);

            var stats = new UniformGrid3();
            stats.Add(Stat(p, N(data.Total), ctx.T("son 1 yıl", "last year"), 0));
            stats.Add(Stat(p, N(data.CurrentStreak), ctx.T("güncel seri", "current streak"), 70));
            stats.Add(Stat(p, N(data.LongestStreak), ctx.T("en uzun seri", "longest streak"), 140));
            col.Children.Add(stats.Panel);

            string? user = ctx.IsAuditMode ? null : AccountStore.Get(ctx.Config, "github", "user")?.Trim().TrimStart('@');
            string url = string.IsNullOrEmpty(user) ? "https://github.com" : "https://github.com/" + Uri.EscapeDataString(user);
            col.Children.Add(LeftButton(W.Button(p, ctx.T("Profili aç", "Open profile"), () => DockWidgetActions.OpenUrl(ctx, url), "")));
            return col;
        }

        private static FrameworkElement Stat(DockWidgetPalette p, string value, string label, double delay)
        {
            var v = W.Text(p, value, 20, p.Foreground, FontWeights.SemiBold);
            var l = W.Text(p, label, 11, p.SecondaryForeground, ui: false);
            var box = new Border
            {
                Background = p.CardRowBackground,
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(0, 0, 6, 0),
                Child = W.Column(v, l)
            };
            W.PopIn(box, delay);
            return box;
        }

        /// <summary>Three equal columns.</summary>
        private sealed class UniformGrid3
        {
            public Grid Panel { get; } = new();
            public void Add(FrameworkElement el)
            {
                Panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                Grid.SetColumn(el, Panel.Children.Count);
                Panel.Children.Add(el);
            }
        }
    }

    // ---- github-notifications -------------------------------------------------------------------

    private sealed class GitHubNotificationsTile : AccountTileBase<GitHubNotifications>
    {
        private TextBlock _bell = null!;
        private TextBlock _count = null!;
        private Ellipse _halo = null!;
        private int _lastCount = -1;

        public GitHubNotificationsTile(DockWidgetDescriptor d, DockWidgetContext ctx) : base(d, ctx) { }

        protected override GitHubNotifications Sample(DockWidgetContext ctx)
        {
            var now = ctx.Now.ToUniversalTime();
            return new GitHubNotifications(
            [
                new("Add keyboard navigation to the dock", "PullRequest", "dinamikada/dinamikada", "review_requested", now.AddMinutes(-12), true, "https://github.com/notifications"),
                new("Crash when the display scale changes", "Issue", "dinamikada/dinamikada", "mention", now.AddHours(-2), true, "https://github.com/notifications"),
                new("v1.4.0", "Release", "dotnet/runtime", "subscribed", now.AddHours(-5), true, "https://github.com/notifications"),
                new("Bump xunit from 2.5.3 to 2.9.2", "PullRequest", "dinamikada/dinamikada", "author", now.AddHours(-9), true, "https://github.com/notifications"),
                new("CI run failed on main", "CheckSuite", "dinamikada/dinamikada", "ci_activity", now.AddDays(-1), false, "https://github.com/notifications")
            ]);
        }

        protected override FrameworkElement BuildTile(DockWidgetContext ctx)
        {
            var p = ctx.Palette;
            double h = ctx.TileHeight;
            double bell = Math.Round(h * 0.5);
            _halo = new Ellipse
            {
                Width = bell,
                Height = bell,
                Fill = p.AccentBrush,
                Opacity = 0,
                IsHitTestVisible = false,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new ScaleTransform(1, 1)
            };
            _bell = W.Glyph(p, "", bell, p.Foreground);
            _bell.RenderTransformOrigin = new Point(0.5, 0.15);
            _bell.RenderTransform = new RotateTransform(0);
            var bellHost = new Grid { Width = bell, Height = bell, VerticalAlignment = VerticalAlignment.Center };
            bellHost.Children.Add(_halo);
            bellHost.Children.Add(_bell);
            _count = W.Text(p, "0", Math.Round(h * 0.42), p.Foreground, FontWeights.SemiBold);
            return W.Row(Math.Round(h * 0.16), bellHost, _count);
        }

        protected override void ShowData(DockWidgetContext ctx, GitHubNotifications data, bool isSample)
        {
            var p = ctx.Palette;
            int n = data.UnreadCount;
            W.SetTextAnimated(_count, n > 99 ? "99+" : n.ToString(CultureInfo.InvariantCulture), animate: n <= _lastCount);
            _count.Foreground = n > 0 ? p.Foreground : p.SecondaryForeground;
            _bell.Foreground = n > 0 ? p.AccentBrush : p.SecondaryForeground;
            if (_lastCount >= 0 && n > _lastCount)
            {
                Pop(_count);
                PulseHalo();
                Ring();
            }
            _lastCount = n;
        }

        private void PulseHalo()
        {
            var st = (ScaleTransform)_halo.RenderTransform;
            var dur = TimeSpan.FromMilliseconds(760);
            var grow = new DoubleAnimation(1, 2.3, dur) { RepeatBehavior = new RepeatBehavior(2), EasingFunction = Motion.Soft };
            st.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            _halo.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.55, 0, dur) { RepeatBehavior = new RepeatBehavior(2) });
        }

        private void Ring()
        {
            var rt = (RotateTransform)_bell.RenderTransform;
            var shake = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(640) };
            double[] angles = [0, 16, -14, 11, -8, 5, -2, 0];
            for (int i = 0; i < angles.Length; i++)
                shake.KeyFrames.Add(new EasingDoubleKeyFrame(angles[i], KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(i * 80))));
            rt.BeginAnimation(RotateTransform.AngleProperty, shake);
        }

        private static string TypeGlyph(string type) => type switch
        {
            "PullRequest" => "",
            "Issue" => "",
            "Release" => "",
            "Discussion" => "",
            "CheckSuite" or "WorkflowRun" => "",
            "Commit" => "",
            "RepositoryVulnerabilityAlert" or "RepositoryDependabotAlertsThread" => "",
            _ => ""
        };

        protected override FrameworkElement BuildCardBody(DockWidgetContext ctx, GitHubNotifications data, bool isSample)
        {
            var p = ctx.Palette;
            var col = new StackPanel();
            var summary = W.Text(p, data.UnreadCount > 0
                ? ctx.T($"{data.UnreadCount} okunmamış bildirim", $"{data.UnreadCount} unread notifications")
                : ctx.T("Okunmamış bildirim yok", "No unread notifications"), 12, p.SecondaryForeground, ui: false);
            summary.Margin = new Thickness(0, 0, 0, 8);
            col.Children.Add(summary);
            if (data.Items.Count == 0) col.Children.Add(Empty(ctx, "Hepsi okundu. Yeni bildirim gelince burada görünecek.", "All caught up. New notifications will show up here."));

            const int max = 9;
            int i = 0;
            foreach (var n in data.Items.Take(max))
            {
                var glyph = W.Glyph(p, TypeGlyph(n.Type), 14, n.Unread ? p.AccentBrush : p.SecondaryForeground);
                glyph.Width = 20;
                glyph.VerticalAlignment = VerticalAlignment.Top;
                glyph.Margin = new Thickness(0, 2, 8, 0);
                var title = W.Text(p, n.Title.Length > 0 ? n.Title : n.Type, 12.5, p.Foreground, n.Unread ? FontWeights.SemiBold : FontWeights.Normal, ui: false);
                string ago = AgoUtc(ctx, n.UpdatedUtc);
                var meta = W.Text(p, n.Repo + (ago.Length > 0 ? " · " + ago : string.Empty), 11, p.TertiaryForeground, ui: false);
                var dp = new DockPanel();
                DockPanel.SetDock(glyph, Dock.Left);
                dp.Children.Add(glyph);
                dp.Children.Add(W.Column(title, meta));
                string url = n.HtmlUrl;
                var row = ClickRow(p, dp, () => DockWidgetActions.OpenUrl(ctx, url), n.Title);
                col.Children.Add(row);
                W.PopIn(row, 35 * i++);
            }
            if (data.Items.Count > max)
                col.Children.Add(W.Text(p, ctx.T($"+{data.Items.Count - max} bildirim daha", $"+{data.Items.Count - max} more"), 11.5, p.TertiaryForeground, ui: false));
            col.Children.Add(LeftButton(W.Button(p, ctx.T("Tümünü aç", "Open all"), () => DockWidgetActions.OpenUrl(ctx, "https://github.com/notifications"), "")));
            return col;
        }
    }

    // ---- linear-issues --------------------------------------------------------------------------

    private sealed class LinearIssuesTile : AccountTileBase<LinearIssues>
    {
        private TextBlock _count = null!;
        private TextBlock _caption = null!;
        private TextBlock _top = null!;

        public LinearIssuesTile(DockWidgetDescriptor d, DockWidgetContext ctx) : base(d, ctx) { }

        protected override LinearIssues Sample(DockWidgetContext ctx)
        {
            var today = DateOnly.FromDateTime(ctx.Now);
            return new LinearIssues("Ada", DeveloperParsers.SortLinear(
            [
                new("ENG-214", "Checkout fails for saved cards", 1, "Urgent", "https://linear.app", today, "In Progress", "#f2c94c", "started"),
                new("ENG-198", "Migrate billing webhooks to v2", 2, "High", "https://linear.app", today.AddDays(3), "In Review", "#0f783c", "started"),
                new("ENG-203", "Dark mode for the settings page", 3, "Medium", "https://linear.app", null, "Todo", "#e2e2e2", "unstarted"),
                new("DES-41", "Onboarding illustrations", 2, "High", "https://linear.app", today.AddDays(9), "Todo", "#e2e2e2", "unstarted"),
                new("ENG-176", "Remove legacy feature flags", 4, "Low", "https://linear.app", null, "Backlog", "#bec2c8", "backlog"),
                new("OPS-12", "Rotate staging certificates", 0, "No priority", "https://linear.app", today.AddDays(14), "Todo", "#e2e2e2", "unstarted")
            ]));
        }

        protected override FrameworkElement BuildTile(DockWidgetContext ctx)
        {
            var p = ctx.Palette;
            double h = ctx.TileHeight;
            _count = W.Text(p, "0", Math.Round(h * 0.5), p.Foreground, FontWeights.SemiBold);
            _caption = W.Text(p, ctx.T("açık", "open"), Math.Max(7, Math.Round(h * 0.22)), p.SecondaryForeground);
            _top = W.Text(p, "—", Math.Max(7, Math.Round(h * 0.24)), p.Foreground, FontWeights.SemiBold, mono: true);
            var texts = W.Column(_caption, _top);
            texts.Margin = new Thickness(Math.Round(h * 0.16), 0, 0, 0);
            var root = new Grid();
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(texts, 1);
            root.Children.Add(_count);
            root.Children.Add(texts);
            return root;
        }

        protected override void ShowData(DockWidgetContext ctx, LinearIssues data, bool isSample)
        {
            var p = ctx.Palette;
            W.SetTextAnimated(_count, data.Issues.Count.ToString(CultureInfo.InvariantCulture));
            var top = data.Top;
            W.SetTextAnimated(_top, top is null ? ctx.T("temiz", "clear") : top.Identifier);
            _top.Foreground = top?.Priority switch { 1 => W.Brush(p.Negative), 2 => W.Brush(p.Warning), null => p.SecondaryForeground, _ => p.Foreground };
            _top.ToolTip = top is null ? null : $"{top.Identifier} · {top.Title}";
            _caption.Text = top is { Priority: 1 } ? ctx.T("açık · acil", "open · urgent") : ctx.T("açık", "open");
        }

        /// <summary>Our own priority mark: a red "!" badge for urgent, 3/2/1 filled bars for high/medium/low, dim bars for none.</summary>
        private static FrameworkElement PriorityMark(DockWidgetPalette p, int priority, double size = 14)
        {
            if (priority == 1)
            {
                var t = W.Text(p, "!", size * 0.78, Brushes.White, FontWeights.Bold);
                t.HorizontalAlignment = HorizontalAlignment.Center;
                return new Border { Width = size, Height = size, CornerRadius = new CornerRadius(size * 0.28), Background = W.Brush(p.Negative), Child = t, VerticalAlignment = VerticalAlignment.Center };
            }
            int filled = priority switch { 2 => 3, 3 => 2, 4 => 1, _ => 0 };
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };
            double bw = size / 4;
            for (int i = 0; i < 3; i++)
            {
                double bh = priority == 0 ? bw : size * (0.42 + 0.29 * i);
                sp.Children.Add(new Border
                {
                    Width = bw,
                    Height = bh,
                    CornerRadius = new CornerRadius(bw * 0.4),
                    Background = i < filled ? p.SecondaryForeground : p.Track,
                    VerticalAlignment = priority == 0 ? VerticalAlignment.Center : VerticalAlignment.Bottom,
                    Margin = new Thickness(i == 0 ? 0 : bw * 0.5, 0, 0, 0)
                });
            }
            return sp;
        }

        protected override FrameworkElement BuildCardBody(DockWidgetContext ctx, LinearIssues data, bool isSample)
        {
            var p = ctx.Palette;
            var col = new StackPanel();
            var summary = W.Text(p, ctx.T($"{data.Issues.Count} açık görev", $"{data.Issues.Count} open issues")
                + (data.ViewerName.Length > 0 ? " · " + data.ViewerName : string.Empty), 12, p.SecondaryForeground, ui: false);
            summary.Margin = new Thickness(0, 0, 0, 8);
            col.Children.Add(summary);
            if (data.Issues.Count == 0) col.Children.Add(Empty(ctx, "Sana atanmış açık görev yok.", "No open issues assigned to you."));

            var today = DateOnly.FromDateTime(ctx.Now);
            var culture = CultureInfo.GetCultureInfo(ctx.En ? "en-US" : "tr-TR");
            const int max = 10;
            int i = 0;
            foreach (var issue in data.Issues.Take(max))
            {
                var mark = PriorityMark(p, issue.Priority);
                mark.Margin = new Thickness(0, 0, 8, 0);
                var id = W.Text(p, issue.Identifier, 11.5, p.SecondaryForeground, mono: true);
                id.Margin = new Thickness(0, 0, 8, 0);
                var stateColor = DeveloperParsers.TryParseHexColor(issue.StateColor, out var r, out var g, out var b)
                    ? W.Brush(Color.FromRgb(r, g, b)) : p.SecondaryForeground;
                var dot = new Ellipse { Width = 8, Height = 8, Fill = stateColor, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), ToolTip = issue.StateName };
                var dp = new DockPanel();
                DockPanel.SetDock(mark, Dock.Left);
                DockPanel.SetDock(id, Dock.Left);
                DockPanel.SetDock(dot, Dock.Right);
                dp.Children.Add(mark);
                dp.Children.Add(id);
                dp.Children.Add(dot);
                if (issue.DueDate is { } due)
                {
                    bool overdue = due < today;
                    var dueText = W.Text(p, due == today ? ctx.T("bugün", "today") : due.ToString("d MMM", culture), 10.5,
                        overdue || due == today ? W.Brush(p.Negative) : p.TertiaryForeground, ui: false);
                    dueText.Margin = new Thickness(8, 0, 0, 0);
                    DockPanel.SetDock(dueText, Dock.Right);
                    dp.Children.Add(dueText);
                }
                dp.Children.Add(W.Text(p, issue.Title, 12.5, p.Foreground, ui: false));
                string url = issue.Url.Length > 0 ? issue.Url : "https://linear.app";
                var row = ClickRow(p, dp, () => DockWidgetActions.OpenUrl(ctx, url),
                    $"{issue.Identifier} · {issue.PriorityLabel} · {issue.StateName}");
                col.Children.Add(row);
                W.PopIn(row, 35 * i++);
            }
            if (data.Issues.Count > max)
                col.Children.Add(W.Text(p, ctx.T($"+{data.Issues.Count - max} görev daha", $"+{data.Issues.Count - max} more"), 11.5, p.TertiaryForeground, ui: false));
            col.Children.Add(LeftButton(W.Button(p, ctx.T("Linear'ı aç", "Open Linear"), () => DockWidgetActions.OpenUrl(ctx, "https://linear.app"), "")));
            return col;
        }
    }

    // ---- resend-emails --------------------------------------------------------------------------

    private sealed class ResendEmailsTile : AccountTileBase<ResendSummary>
    {
        private RingGauge _ring = null!;
        private TextBlock _sent = null!;
        private TextBlock _caption = null!;

        public ResendEmailsTile(DockWidgetDescriptor d, DockWidgetContext ctx) : base(d, ctx) { }

        protected override ResendSummary Sample(DockWidgetContext ctx)
        {
            var now = ctx.Now.ToUniversalTime();
            var latest = new List<ResendEmail>
            {
                new("s1", ["melis.kaya@example.com"], "Siparişin yola çıktı", now.AddMinutes(-4), "delivered"),
                new("s2", ["j.smith@example.org"], "Reset your password", now.AddMinutes(-31), "opened"),
                new("s3", ["team@example.net"], "Weekly digest", now.AddHours(-2), "clicked"),
                new("s4", ["old-address@example.com"], "Your invoice #1042", now.AddHours(-6), "bounced"),
                new("s5", ["can@example.com"], "Hoş geldin!", now.AddHours(-9), "delivered"),
                new("s6", ["ops@example.io"], "Daily report", now.AddHours(-20), "delivered")
            };
            var counts = new Dictionary<string, int> { ["delivered"] = 118, ["opened"] = 64, ["clicked"] = 21, ["bounced"] = 4, ["complained"] = 1, ["sent"] = 3 };
            return new ResendSummary(latest, counts, counts.Values.Sum(), 203.0 / 208.0);
        }

        protected override FrameworkElement BuildTile(DockWidgetContext ctx)
        {
            var p = ctx.Palette;
            double h = ctx.TileHeight;
            _ring = W.Ring(p, Math.Round(h * 0.74), Math.Max(2.5, h * 0.11), p.Positive);
            _sent = W.Text(p, "0", Math.Round(h * 0.4), p.Foreground, FontWeights.SemiBold);
            _caption = W.Text(p, ctx.T("7 günde", "in 7 days"), Math.Max(7, Math.Round(h * 0.22)), p.SecondaryForeground);
            // W.Row owns the left margin of later children, so the gap goes in as its spacing.
            return W.Row(Math.Round(h * 0.18), _ring, W.Column(_sent, _caption));
        }

        private static Color RateColor(DockWidgetPalette p, double? rate) =>
            rate is null ? p.Accent : rate >= 0.95 ? p.Positive : rate >= 0.85 ? p.Warning : p.Negative;

        protected override void ShowData(DockWidgetContext ctx, ResendSummary data, bool isSample)
        {
            W.SetTextAnimated(_sent, Compact(data.Sent7Days));
            _ring.Fill = W.Brush(RateColor(ctx.Palette, data.DeliveryRate));
            _ring.AnimateTo(data.DeliveryRate ?? 0);
            _ring.ToolTip = data.DeliveryRate is { } r ? ctx.T("Teslim oranı ", "Delivery rate ") + W.Pct(r * 100) : null;
        }

        private static string StatusName(DockWidgetContext ctx, string s) => s switch
        {
            "delivered" => ctx.T("Teslim edildi", "Delivered"),
            "opened" => ctx.T("Açıldı", "Opened"),
            "clicked" => ctx.T("Tıklandı", "Clicked"),
            "bounced" => ctx.T("Geri döndü", "Bounced"),
            "complained" => ctx.T("Şikâyet", "Complained"),
            "failed" => ctx.T("Başarısız", "Failed"),
            "sent" => ctx.T("Gönderildi", "Sent"),
            "queued" => ctx.T("Sırada", "Queued"),
            "scheduled" => ctx.T("Zamanlandı", "Scheduled"),
            "delivery_delayed" => ctx.T("Gecikti", "Delayed"),
            "canceled" => ctx.T("İptal", "Canceled"),
            _ => s.Length == 0 ? ctx.T("Bilinmiyor", "Unknown") : char.ToUpperInvariant(s[0]) + s[1..].Replace('_', ' ')
        };

        private static Color StatusColor(DockWidgetPalette p, string s) => s switch
        {
            "delivered" or "opened" or "clicked" => p.Positive,
            "bounced" or "complained" or "failed" => p.Negative,
            "delivery_delayed" => p.Warning,
            _ => p.Accent
        };

        private static readonly string[] Order = ["delivered", "opened", "clicked", "sent", "queued", "scheduled", "delivery_delayed", "bounced", "complained", "failed"];

        protected override FrameworkElement BuildCardBody(DockWidgetContext ctx, ResendSummary data, bool isSample)
        {
            var p = ctx.Palette;
            var col = new StackPanel();

            var big = W.Text(p, N(data.Sent7Days), 26, p.Foreground, FontWeights.SemiBold);
            var bigCaption = W.Text(p, ctx.T("son 7 günde gönderilen", "sent in the last 7 days"), 11.5, p.SecondaryForeground, ui: false);
            var rate = W.Text(p, data.DeliveryRate is { } r ? W.Pct(r * 100) : "—", 20, W.Brush(RateColor(p, data.DeliveryRate)), FontWeights.SemiBold);
            rate.HorizontalAlignment = HorizontalAlignment.Right;
            var rateCaption = W.Text(p, ctx.T("teslim oranı", "delivery rate"), 11.5, p.SecondaryForeground, ui: false);
            rateCaption.HorizontalAlignment = HorizontalAlignment.Right;
            var right = W.Column(rate, rateCaption);
            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
            DockPanel.SetDock(right, Dock.Right);
            head.Children.Add(right);
            head.Children.Add(W.Column(big, bigCaption));
            col.Children.Add(head);

            // Status breakdown with animated bars.
            var statuses = data.Last7Days.Where(kv => kv.Value > 0)
                .OrderBy(kv => { int ix = Array.IndexOf(Order, kv.Key); return ix < 0 ? 99 : ix; }).ToList();
            int total = Math.Max(1, data.Last7Days.Values.Sum());
            int i = 0;
            foreach (var (status, count) in statuses)
            {
                var label = W.Text(p, StatusName(ctx, status), 12, p.SecondaryForeground, ui: false);
                label.Width = 98;
                var value = W.Text(p, N(count), 12, p.Foreground, FontWeights.SemiBold);
                value.Width = 40;
                value.TextAlignment = TextAlignment.Right;
                var bar = W.Bar(p, CardWidth - 98 - 40 - 12, 6, StatusColor(p, status));
                bar.Margin = new Thickness(0, 0, 6, 0);
                var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
                DockPanel.SetDock(label, Dock.Left);
                DockPanel.SetDock(value, Dock.Right);
                row.Children.Add(label);
                row.Children.Add(value);
                row.Children.Add(bar);
                col.Children.Add(row);
                double frac = count / (double)total;
                int delay = 60 * i++;
                bar.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () => bar.AnimateTo(frac, 560 + delay));
            }
            if (statuses.Count == 0) col.Children.Add(Empty(ctx, "Son 7 günde gönderim yok.", "Nothing sent in the last 7 days."));

            var latestTitle = W.Text(p, ctx.T("Son e-postalar", "Latest emails"), 12, p.Foreground, FontWeights.SemiBold, ui: false);
            latestTitle.Margin = new Thickness(0, 10, 0, 6);
            col.Children.Add(latestTitle);
            if (data.Latest.Count == 0) col.Children.Add(Empty(ctx, "Henüz e-posta gönderilmemiş.", "No emails sent yet."));
            int j = 0;
            foreach (var e in data.Latest.Take(7))
            {
                string to = e.To.Count == 0 ? "—" : DeveloperParsers.MaskEmail(e.To[0]) + (e.To.Count > 1 ? $" +{e.To.Count - 1}" : string.Empty);
                var subject = W.Text(p, e.Subject.Length > 0 ? e.Subject : ctx.T("(konu yok)", "(no subject)"), 12.5, p.Foreground, ui: false);
                string ago = AgoUtc(ctx, e.CreatedUtc);
                var meta = W.Text(p, to + (ago.Length > 0 ? " · " + ago : string.Empty), 11, p.TertiaryForeground, ui: false);
                var c = StatusColor(p, e.LastEvent);
                var chip = new Border
                {
                    Background = W.Brush(c, 0x2E),
                    CornerRadius = new CornerRadius(7),
                    Padding = new Thickness(7, 2, 7, 2),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(8, 0, 0, 0),
                    Child = W.Text(p, StatusName(ctx, e.LastEvent), 10.5, W.Brush(c), FontWeights.SemiBold)
                };
                var dp = new DockPanel();
                DockPanel.SetDock(chip, Dock.Right);
                dp.Children.Add(chip);
                dp.Children.Add(W.Column(subject, meta));
                var row = new Border { Child = dp, Background = p.CardRowBackground, CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 5, 8, 5), Margin = new Thickness(0, 0, 0, 4) };
                col.Children.Add(row);
                W.PopIn(row, 120 + 35 * j++);
            }
            col.Children.Add(LeftButton(W.Button(p, ctx.T("Resend'i aç", "Open Resend"), () => DockWidgetActions.OpenUrl(ctx, "https://resend.com/emails"), "")));
            return col;
        }
    }

    // ---- plausible-visitors ---------------------------------------------------------------------

    private sealed class PlausibleVisitorsTile : AccountTileBase<PlausibleStats>
    {
        private Ellipse _dot = null!;
        private Ellipse _dotHalo = null!;
        private TextBlock _realtime = null!;
        private TextBlock _today = null!;
        private Sparkline _spark = null!;
        private double _sparkWidth;
        private double _sparkHeight;
        private bool _attached;

        public PlausibleVisitorsTile(DockWidgetDescriptor d, DockWidgetContext ctx) : base(d, ctx) { }

        protected override PlausibleStats Sample(DockWidgetContext ctx)
        {
            var today = DateOnly.FromDateTime(ctx.Now);
            int[] visitors = [312, 298, 341, 365, 290, 188, 172, 334, 356, 389, 402, 377, 241, 128];
            var days = new List<PlausibleDay>();
            for (int i = 0; i < 14; i++) days.Add(new PlausibleDay(today.AddDays(i - 13), visitors[i], (int)(visitors[i] * (2.4 + (i % 3) * 0.2))));
            int last7 = days.Skip(7).Sum(d => d.Visitors), prev7 = days.Take(7).Sum(d => d.Visitors);
            return new PlausibleStats(days, 7, days[^1].Visitors, last7, prev7, days.Skip(7).Sum(d => d.Pageviews), (last7 - prev7) * 100.0 / prev7);
        }

        protected override FrameworkElement BuildTile(DockWidgetContext ctx)
        {
            var p = ctx.Palette;
            double h = ctx.TileHeight;
            double dot = Math.Max(5, Math.Round(h * 0.2));
            _dot = new Ellipse { Width = dot, Height = dot, Fill = W.Brush(p.Positive), IsHitTestVisible = false };
            _dotHalo = new Ellipse
            {
                Width = dot,
                Height = dot,
                Fill = W.Brush(p.Positive),
                Opacity = 0,
                IsHitTestVisible = false,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new ScaleTransform(1, 1)
            };
            var dotHost = new Grid { Width = dot, Height = dot, VerticalAlignment = VerticalAlignment.Center };
            dotHost.Children.Add(_dotHalo);
            dotHost.Children.Add(_dot);
            _realtime = W.Text(p, "0", Math.Round(h * 0.36), p.Foreground, FontWeights.SemiBold);
            _today = W.Text(p, string.Empty, Math.Max(7, Math.Round(h * 0.22)), p.SecondaryForeground);
            var left = W.Column(W.Row(Math.Round(h * 0.12), dotHost, _realtime), _today);

            double inner = InnerWidth(ctx, Descriptor.WidthUnits);
            _sparkWidth = Math.Round(inner * 0.42);
            _sparkHeight = Math.Max(8, Math.Round(h * 0.5));
            _spark = new Sparkline { Width = _sparkWidth, Height = _sparkHeight, StrokeThickness = Math.Max(1.1, h * 0.04), Stroke = p.AccentBrush, ShowHead = true, VerticalAlignment = VerticalAlignment.Center };
            _spark.Margin = new Thickness(Math.Round(h * 0.12), 0, 0, 0);

            var root = new Grid();
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(_spark, 1);
            root.Children.Add(left);
            root.Children.Add(_spark);
            return root;
        }

        public override void OnAttached(DockWidgetContext ctx)
        {
            base.OnAttached(ctx);
            _attached = true;
            StartPulse();
        }

        public override void OnDetached()
        {
            _attached = false;
            StopPulse();
            base.OnDetached();
        }

        private void StartPulse()
        {
            if (!_attached || _dotHalo is null) return;
            var st = (ScaleTransform)_dotHalo.RenderTransform;
            var dur = TimeSpan.FromSeconds(1.8);
            var grow = new DoubleAnimation(1, 2.6, dur) { RepeatBehavior = RepeatBehavior.Forever, EasingFunction = Motion.Soft };
            st.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            _dotHalo.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.5, 0, dur) { RepeatBehavior = RepeatBehavior.Forever });
        }

        private void StopPulse()
        {
            if (_dotHalo is null) return;
            var st = (ScaleTransform)_dotHalo.RenderTransform;
            st.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            _dotHalo.BeginAnimation(UIElement.OpacityProperty, null);
            _dotHalo.Opacity = 0;
        }

        protected override void ShowData(DockWidgetContext ctx, PlausibleStats data, bool isSample)
        {
            var p = ctx.Palette;
            W.SetTextAnimated(_realtime, Compact(data.Realtime));
            W.SetTextAnimated(_today, ctx.T("Bugün ", "Today ") + Compact(data.Today));
            _dot.Fill = data.Realtime > 0 ? W.Brush(p.Positive) : p.Track;
            _realtime.ToolTip = ctx.T($"{data.Realtime} kişi şu an sitede", $"{data.Realtime} people on the site now");
            _spark.SetValues(data.Days.Select(d => (double)d.Visitors).ToList());
            DrawIn(_spark, _sparkWidth, _sparkHeight);
        }

        protected override FrameworkElement BuildCardBody(DockWidgetContext ctx, PlausibleStats data, bool isSample)
        {
            var p = ctx.Palette;
            var col = new StackPanel();

            var big = W.Text(p, N(data.Last7), 26, p.Foreground, FontWeights.SemiBold);
            var caption = W.Text(p, ctx.T("ziyaretçi · son 7 gün", "visitors · last 7 days"), 11.5, p.SecondaryForeground, ui: false);
            string change = data.ChangePct is { } c ? (c >= 0 ? "▲ " : "▼ ") + Math.Abs(c).ToString("0.#", CultureInfo.InvariantCulture) + "%" : "—";
            var changeText = W.Text(p, change, 15, data.ChangePct is null ? p.SecondaryForeground : W.Brush(data.ChangePct >= 0 ? p.Positive : p.Negative), FontWeights.SemiBold);
            changeText.HorizontalAlignment = HorizontalAlignment.Right;
            var changeCaption = W.Text(p, ctx.T("önceki 7 güne göre", "vs previous 7 days"), 11, p.TertiaryForeground, ui: false);
            changeCaption.HorizontalAlignment = HorizontalAlignment.Right;
            var right = W.Column(changeText, changeCaption);
            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
            DockPanel.SetDock(right, Dock.Right);
            head.Children.Add(right);
            head.Children.Add(W.Column(big, caption));
            col.Children.Add(head);

            // 14-day bar chart: bars grow from the baseline one after another; the last 7 days are brighter.
            const double chartH = 84;
            int max = Math.Max(1, data.Days.Count == 0 ? 1 : data.Days.Max(d => d.Visitors));
            var chart = new Grid { Height = chartH, Margin = new Thickness(0, 0, 0, 4) };
            var culture = CultureInfo.GetCultureInfo(ctx.En ? "en-US" : "tr-TR");
            for (int i = 0; i < data.Days.Count; i++)
            {
                var d = data.Days[i];
                chart.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                double target = Math.Max(2, d.Visitors / (double)max * chartH);
                var bar = new Border
                {
                    Height = 0,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    CornerRadius = new CornerRadius(3, 3, 1, 1),
                    Margin = new Thickness(1.5, 0, 1.5, 0),
                    Background = i >= data.Days.Count - 7 ? p.AccentBrush : W.Brush(p.Accent, 0x66),
                    ToolTip = $"{d.Date.ToString("d MMM", culture)}: {N(d.Visitors)} {ctx.T("ziyaretçi", "visitors")} · {N(d.Pageviews)} {ctx.T("görüntüleme", "pageviews")}"
                };
                Grid.SetColumn(bar, i);
                chart.Children.Add(bar);
                bar.BeginAnimation(FrameworkElement.HeightProperty, new DoubleAnimation(0, target, TimeSpan.FromMilliseconds(520))
                {
                    BeginTime = TimeSpan.FromMilliseconds(30 * i),
                    EasingFunction = Motion.Soft
                });
            }
            col.Children.Add(chart);
            if (data.Days.Count > 0)
            {
                var axis = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
                var end = W.Text(p, ctx.T("bugün", "today"), 10, p.TertiaryForeground, ui: false);
                DockPanel.SetDock(end, Dock.Right);
                axis.Children.Add(end);
                axis.Children.Add(W.Text(p, data.Days[0].Date.ToString("d MMM", culture), 10, p.TertiaryForeground, ui: false));
                col.Children.Add(axis);
            }

            var rows = new FrameworkElement[]
            {
                W.CardRow(p, ctx.T("Şu an sitede", "On the site now"), N(data.Realtime), data.Realtime > 0 ? W.Brush(p.Positive) : null, ""),
                W.CardRow(p, ctx.T("Bugün", "Today"), N(data.Today), null, ""),
                W.CardRow(p, ctx.T("Görüntüleme (7 gün)", "Pageviews (7 days)"), N(data.Pageviews7), null, ""),
                W.CardRow(p, ctx.T("Önceki 7 gün", "Previous 7 days"), N(data.Prev7), null, "")
            };
            for (int i = 0; i < rows.Length; i++)
            {
                col.Children.Add(rows[i]);
                W.PopIn(rows[i], 200 + 40 * i);
            }

            string? site = ctx.IsAuditMode ? null : AccountStore.Get(ctx.Config, "plausible", "site")?.Trim();
            string? host = ctx.IsAuditMode ? null : AccountStore.Get(ctx.Config, "plausible", "host")?.Trim().TrimEnd('/');
            if (string.IsNullOrEmpty(host)) host = "https://plausible.io";
            if (!host.Contains("://", StringComparison.Ordinal)) host = "https://" + host;
            string url = string.IsNullOrEmpty(site) ? host : host + "/" + Uri.EscapeDataString(site);
            col.Children.Add(LeftButton(W.Button(p, ctx.T("Panoyu aç", "Open dashboard"), () => DockWidgetActions.OpenUrl(ctx, url), "")));
            return col;
        }
    }
}
