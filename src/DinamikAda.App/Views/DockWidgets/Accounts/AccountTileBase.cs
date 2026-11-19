using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DinamikAda.App.Services.DockWidgets;
using DinamikAda.Core.Accounts;
using DinamikAda.Core.Widgets;

namespace DinamikAda.App.Views.DockWidgets.Accounts;

/// <summary>Render-audit switch: build the connect form instead of the sample card.</summary>
public static class AccountAudit
{
    public static bool ShowConnectForm { get; set; }
}

/// <summary>
/// Base for widgets that read an account (Stripe, GitHub, Gmail…).
/// <list type="bullet">
/// <item>Until the service is connected the tile shows <see cref="Sample"/> data, dimmed, with a link badge; its card
/// is a step-by-step connect form whose fields come from <see cref="AccountCatalog"/>.</item>
/// <item>Once connected it shows live data from <see cref="AccountHub"/>, and the card shows the subclass body plus a
/// status footer (last update or error, refresh, disconnect).</item>
/// </list>
/// Subclasses implement the four abstract members and never deal with credentials or fetch state.
/// </summary>
public abstract class AccountTileBase<T> : DockWidgetTileBase where T : class
{
    private Grid _host = null!;
    private Border _badge = null!;
    private TextBlock _badgeGlyph = null!;
    private FrameworkElement _content = null!;
    private TextBlock? _cardStatus;
    private bool _shownOnce;
    private AccountState _lastState;
    private object? _lastData;
    private bool _confirmRemove;

    protected AccountTileBase(DockWidgetDescriptor descriptor, DockWidgetContext ctx) : base(descriptor, ctx) { }

    /// <summary>The service this widget reads (from the catalog descriptor).</summary>
    protected AccountServiceDescriptor Service => AccountCatalog.Find(Descriptor.Account)!;

    // ---- What subclasses implement --------------------------------------------------------------

    /// <summary>Plausible data shown before connecting, in audit mode and while the first fetch runs.</summary>
    protected abstract T Sample(DockWidgetContext ctx);
    /// <summary>Builds the tile content once (store elements in fields, like any tile).</summary>
    protected abstract FrameworkElement BuildTile(DockWidgetContext ctx);
    /// <summary>Shows <paramref name="data"/> on the tile; called when the data or state changes. Animate here.</summary>
    protected abstract void ShowData(DockWidgetContext ctx, T data, bool isSample);
    /// <summary>The card body for live (or sample) data; the base adds the header and status footer.</summary>
    protected abstract FrameworkElement BuildCardBody(DockWidgetContext ctx, T data, bool isSample);
    /// <summary>Called every second with the current data (for clocks/countdowns); keep it cheap.</summary>
    protected virtual void Tick(DockWidgetContext ctx, T data, bool isSample) { }
    /// <summary>Card width.</summary>
    protected virtual double CardWidth => 320;

    // ---- State ----------------------------------------------------------------------------------

    protected AccountResult Result(DockWidgetContext ctx) =>
        ctx.IsAuditMode ? AccountResult.NotConnected : ctx.Data.Accounts.Get(Id);

    /// <summary>Live data when there is any, else the sample.</summary>
    protected (T Data, bool IsSample) Current(DockWidgetContext ctx)
    {
        var r = Result(ctx);
        return r.Data is T live ? (live, false) : (Sample(ctx), true);
    }

    protected sealed override FrameworkElement BuildContent(DockWidgetContext ctx)
    {
        var p = ctx.Palette;
        _content = BuildTile(ctx);
        double b = Math.Max(11, ctx.TileHeight * 0.36);
        _badgeGlyph = W.Glyph(p, "", b * 0.58, p.Foreground);
        _badge = new Border
        {
            Width = b,
            Height = b,
            CornerRadius = new CornerRadius(b / 2),
            Background = p.CardBackground,
            BorderBrush = p.TileBorder,
            BorderThickness = new Thickness(0.8),
            Child = _badgeGlyph,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, -b * 0.35, -b * 0.35, 0),
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed
        };
        _host = new Grid();
        _host.Children.Add(_content);
        _host.Children.Add(_badge);
        return _host;
    }

    protected sealed override void Refresh(DockWidgetContext ctx)
    {
        var r = Result(ctx);
        var (data, isSample) = Current(ctx);
        try { Tick(ctx, data, isSample); }
        catch (Exception ex) { Services.CrashLogger.Log("AccountWidget", ex, $"Widget '{Id}' tick failed."); }
        object? dataRef = isSample ? null : data;
        if (_shownOnce && r.State == _lastState && ReferenceEquals(dataRef, _lastData)) return;
        _shownOnce = true;
        _lastState = r.State;
        _lastData = dataRef;

        var p = ctx.Palette;
        bool dim = isSample && !ctx.IsAuditMode;
        AnimateOpacity(_content, dim ? 0.55 : 1.0);
        switch (r.State)
        {
            case AccountState.NotConnected when !ctx.IsAuditMode:
                SetBadge("", p.Foreground, p.CardBackground);
                break;
            case AccountState.Error:
                SetBadge("", Brushes.White, W.Brush(p.Negative));
                break;
            case AccountState.Loading:
                SetBadge("", p.Foreground, p.CardBackground);
                break;
            default:
                if (_badge.Visibility == Visibility.Visible) _badge.Visibility = Visibility.Collapsed;
                break;
        }
        try { ShowData(ctx, data, isSample); }
        catch (Exception ex) { Services.CrashLogger.Log("AccountWidget", ex, $"Widget '{Id}' could not show data."); }
    }

    private void SetBadge(string glyph, Brush fg, Brush bg)
    {
        _badgeGlyph.Text = glyph;
        _badgeGlyph.Foreground = fg;
        _badge.Background = bg;
        if (_badge.Visibility != Visibility.Visible)
        {
            _badge.Visibility = Visibility.Visible;
            W.PopIn(_badge);
        }
    }

    private static void AnimateOpacity(UIElement el, double to)
    {
        if (Math.Abs(el.Opacity - to) < 0.01) return;
        el.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(260))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
    }

    // ---- Cards ----------------------------------------------------------------------------------

    protected sealed override FrameworkElement? CreateCard(DockWidgetContext ctx)
    {
        _confirmRemove = false;
        _cardStatus = null;
        var r = Result(ctx);
        if ((r.State == AccountState.NotConnected && !ctx.IsAuditMode) || (ctx.IsAuditMode && AccountAudit.ShowConnectForm))
            return BuildConnectCard(ctx);

        var (data, isSample) = Current(ctx);
        FrameworkElement body;
        try { body = BuildCardBody(ctx, data, isSample); }
        catch (Exception ex)
        {
            Services.CrashLogger.Log("AccountWidget", ex, $"Widget '{Id}' card failed.");
            body = W.Text(ctx.Palette, ctx.T("Kart gösterilemedi.", "The card could not be shown."), 12.5, ctx.Palette.SecondaryForeground);
        }
        return W.Card(ctx.Palette, Descriptor.Glyph, Descriptor.Name(ctx.Lang), W.Column(body, BuildFooter(ctx, r)), CardWidth);
    }

    protected override void RefreshCard(DockWidgetContext ctx)
    {
        if (_cardStatus is not null) _cardStatus.Text = StatusText(ctx, Result(ctx));
    }

    private static string StatusText(DockWidgetContext ctx, AccountResult r) => r.State switch
    {
        _ when ctx.IsAuditMode => ctx.T("Örnek veri", "Sample data"),
        AccountState.Loading => ctx.T("Yükleniyor…", "Loading…"),
        AccountState.Error => r.Error ?? ctx.T("Hata", "Error"),
        AccountState.Ready when r.UpdatedLocal is { } t =>
            ctx.T("Güncellendi ", "Updated ") + t.ToString("HH:mm", CultureInfo.InvariantCulture),
        _ => string.Empty
    };

    private FrameworkElement BuildFooter(DockWidgetContext ctx, AccountResult r)
    {
        var p = ctx.Palette;
        var footer = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        footer.Children.Add(new Border { Height = 1, Background = p.Track, Margin = new Thickness(0, 0, 0, 8) });
        _cardStatus = W.Text(p, StatusText(ctx, r), 11.5,
            r.State == AccountState.Error ? W.Brush(p.Negative) : p.TertiaryForeground, ui: false);
        _cardStatus.TextWrapping = TextWrapping.Wrap;
        _cardStatus.Margin = new Thickness(0, 0, 0, 8);
        footer.Children.Add(_cardStatus);

        var buttons = new WrapPanel();
        buttons.Children.Add(W.Button(p, ctx.T("Yenile", "Refresh"), () =>
        {
            if (ctx.IsAuditMode) return;
            ctx.Data.Accounts.Refresh(Id);
            RefreshCard(ctx);
        }, ""));
        buttons.Children.Add(W.Button(p, ctx.T("Düzenle", "Edit"), () =>
        {
            if (ctx.IsAuditMode) return;
            ctx.ShowCard(Root, BuildConnectCard(ctx));
        }, ""));
        Border? remove = null;
        remove = W.Button(p, ctx.T("Bağlantıyı kaldır", "Disconnect"), () =>
        {
            if (ctx.IsAuditMode) return;
            if (!_confirmRemove)
            {
                _confirmRemove = true;
                if (remove?.Child is StackPanel sp && sp.Children.OfType<TextBlock>().LastOrDefault() is { } tb)
                    tb.Text = ctx.T("Emin misin? Tekrar bas", "Sure? Press again");
                return;
            }
            AccountStore.Remove(ctx.Orch.ConfigStore, ctx.Config, Service.Id);
            ctx.Data.Accounts.Refresh(Id);
            ReopenCard(ctx);
        }, "");
        buttons.Children.Add(remove);
        footer.Children.Add(buttons);
        return footer;
    }

    /// <summary>Step-by-step connect form generated from the service's catalog entry.</summary>
    private FrameworkElement BuildConnectCard(DockWidgetContext ctx)
    {
        var p = ctx.Palette;
        var svc = Service;
        var root = new StackPanel();

        var intro = W.Text(p, ctx.T(
                $"Bu widget {svc.Name} hesabını kendi salt-okunur anahtarınla okur. Anahtar yalnızca bu bilgisayarda şifreli saklanır ve sadece {svc.Name}'e gönderilir.",
                $"This widget reads your {svc.Name} account with your own read-only key. The key is stored encrypted on this PC only and is sent only to {svc.Name}."),
            12, p.SecondaryForeground, ui: false);
        intro.TextWrapping = TextWrapping.Wrap;
        intro.Margin = new Thickness(0, 0, 0, 10);
        root.Children.Add(intro);

        var steps = svc.Steps(ctx.Lang);
        for (int i = 0; i < steps.Count; i++)
        {
            var num = new Border
            {
                Width = 18,
                Height = 18,
                CornerRadius = new CornerRadius(9),
                Background = W.Brush(p.Accent, 0x30),
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Top,
                Child = W.Text(p, (i + 1).ToString(CultureInfo.InvariantCulture), 10.5, p.AccentBrush, FontWeights.Bold)
            };
            ((TextBlock)num.Child).HorizontalAlignment = HorizontalAlignment.Center;
            var text = W.Text(p, steps[i], 12, p.Foreground, ui: false);
            text.TextWrapping = TextWrapping.Wrap;
            text.TextTrimming = TextTrimming.None;
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            DockPanel.SetDock(num, Dock.Left);
            row.Children.Add(num);
            row.Children.Add(text);
            root.Children.Add(row);
            W.PopIn(row, 40 * i);
        }

        var inputs = new Dictionary<string, Func<string>>(StringComparer.Ordinal);
        FrameworkElement? first = null;
        foreach (var f in svc.Fields)
        {
            var label = W.Text(p, f.Label(ctx.Lang) + (f.Optional ? ctx.T(" (isteğe bağlı)", " (optional)") : string.Empty),
                11.5, p.SecondaryForeground, FontWeights.SemiBold, ui: false);
            label.Margin = new Thickness(0, 8, 0, 4);
            root.Children.Add(label);
            string? existing = ctx.IsAuditMode ? null : AccountStore.Get(ctx.Config, svc.Id, f.Key);
            if (f.Secret)
            {
                var pb = SecretBox(p, CardWidth, f.Placeholder);
                if (existing is not null) pb.Password = existing;
                inputs[f.Key] = () => pb.Password;
                root.Children.Add(pb);
                first ??= pb;
            }
            else
            {
                var tb = W.Input(p, f.Placeholder, CardWidth);
                if (existing is not null) tb.Text = existing;
                inputs[f.Key] = () => tb.Text;
                root.Children.Add(tb);
                first ??= tb;
            }
        }

        var error = W.Text(p, string.Empty, 11.5, W.Brush(p.Negative), ui: false);
        error.TextWrapping = TextWrapping.Wrap;
        error.Margin = new Thickness(0, 6, 0, 0);
        error.Visibility = Visibility.Collapsed;
        root.Children.Add(error);

        var buttons = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        void Connect()
        {
            if (ctx.IsAuditMode) return;
            var values = inputs.ToDictionary(kv => kv.Key, kv => kv.Value().Trim(), StringComparer.Ordinal);
            if (!AccountCatalog.IsComplete(svc, k => values.TryGetValue(k, out var v) && v.Length > 0 ? v : null))
            {
                error.Text = ctx.T("Zorunlu alanları doldur.", "Fill in the required fields.");
                error.Visibility = Visibility.Visible;
                W.Pulse(error, 0.96);
                return;
            }
            AccountStore.Save(ctx.Orch.ConfigStore, ctx.Config, svc.Id, values);
            ctx.Data.Accounts.Refresh(Id);
            ReopenCard(ctx);
        }
        buttons.Children.Add(W.Button(p, ctx.T("Bağla", "Connect"), Connect, "", primary: true));
        buttons.Children.Add(W.Button(p, ctx.T("Panoyu aç", "Open dashboard"), () => DockWidgetActions.OpenUrl(ctx, svc.DashboardUrl), ""));
        root.Children.Add(buttons);

        root.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            Connect();
        };
        if (first is not null && !ctx.IsAuditMode)
            first.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () => first.Focus());

        bool connected = !ctx.IsAuditMode && AccountStore.IsConnected(ctx.Config, svc.Id);
        string title = connected ? ctx.T($"{svc.Name} bağlantısı", $"{svc.Name} connection") : ctx.T($"{svc.Name}'i bağla", $"Connect {svc.Name}");
        return W.Card(p, svc.Glyph, title, root, CardWidth);
    }

    private static PasswordBox SecretBox(DockWidgetPalette p, double width, string placeholder)
    {
        var pb = new PasswordBox
        {
            Width = width,
            FontFamily = p.TextFont,
            FontSize = 13,
            Foreground = p.Foreground,
            CaretBrush = p.Foreground,
            Background = p.CardRowBackground,
            BorderBrush = p.TileBorder,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 5, 8, 5),
            VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = placeholder
        };
        System.Windows.Automation.AutomationProperties.SetName(pb, placeholder);
        return pb;
    }

    // ---- Helpers for subclasses -----------------------------------------------------------------

    /// <summary>"12.4K", "1.2M" style compact number.</summary>
    protected static string Compact(double v)
    {
        double a = Math.Abs(v);
        string s = a >= 1_000_000_000 ? (v / 1_000_000_000).ToString("0.#", CultureInfo.InvariantCulture) + "B"
            : a >= 1_000_000 ? (v / 1_000_000).ToString("0.#", CultureInfo.InvariantCulture) + "M"
            : a >= 10_000 ? (v / 1_000).ToString("0.#", CultureInfo.InvariantCulture) + "K"
            : v.ToString("#,0", CultureInfo.InvariantCulture);
        return s;
    }

    /// <summary>"$1,234" / "₺1,234" / "€12.50" money label for a currency code.</summary>
    protected static string Money(double amount, string? currency)
    {
        string code = (currency ?? "USD").ToUpperInvariant();
        string sym = code switch { "USD" => "$", "EUR" => "€", "GBP" => "£", "TRY" => "₺", "JPY" => "¥", "INR" => "₹", _ => code + " " };
        string num = Math.Abs(amount) >= 1000 ? amount.ToString("#,0", CultureInfo.InvariantCulture) : amount.ToString("#,0.00", CultureInfo.InvariantCulture);
        return sym + num;
    }

    /// <summary>"5 dk önce" / "5m ago" relative time.</summary>
    protected static string Ago(DockWidgetContext ctx, DateTime whenLocal)
    {
        var d = ctx.Now - whenLocal;
        if (d.TotalMinutes < 1) return ctx.T("az önce", "just now");
        if (d.TotalHours < 1) return ctx.T($"{(int)d.TotalMinutes} dk önce", $"{(int)d.TotalMinutes}m ago");
        if (d.TotalDays < 1) return ctx.T($"{(int)d.TotalHours} sa önce", $"{(int)d.TotalHours}h ago");
        return ctx.T($"{(int)d.TotalDays} gün önce", $"{(int)d.TotalDays}d ago");
    }
}
