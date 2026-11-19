using System.Net.Http;
using System.Net.Http.Headers;
using System.Windows;
using System.Windows.Controls;
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
    }

    public static void Register(Dictionary<string, Func<DockWidgetDescriptor, DockWidgetContext, IDockWidgetTile>> map)
    {
        map["deepseek-balance"] = (d, c) => new DeepSeekBalanceTile(d, c);
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
            _amount = W.Text(p, "—", Math.Round(h * 0.40), p.Foreground, FontWeights.SemiBold);
            _caption = W.Text(p, "DeepSeek", Math.Round(h * 0.26), p.SecondaryForeground);
            var texts = W.Column(_amount, _caption);
            texts.Margin = new Thickness(Math.Round(h * 0.18), 0, 0, 0);
            return W.Row(0, _ring, texts);
        }

        protected override void ShowData(DockWidgetContext ctx, DeepSeekBalance data, bool isSample)
        {
            var item = data.Primary;
            double total = item?.Total ?? 0;
            W.SetTextAnimated(_amount, Money(total, item?.Currency));
            // The ring shows how much of the balance is your own top-up (the rest is granted credit).
            double share = total <= 0 ? 0 : Math.Clamp((item!.ToppedUp / total) * 100, 0, 100);
            _ring.Fill = W.Brush(total <= 1 ? ctx.Palette.Negative : total <= 5 ? ctx.Palette.Warning : ctx.Palette.Accent);
            _ring.AnimateTo(total <= 0 ? 0 : Math.Max(share, 6));
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
}
