using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DinamikAda.Core.Abstractions;
using DinamikAda.Core.Localization;
using DinamikAda.Core.Notifications;

namespace DinamikAda.App.Views;

/// <summary>
/// Notification toasts that rise from the dock: meeting reminders (5 min before), finished timers / Pomodoros,
/// AI quota resets, plus <see cref="ShowDockToast(DockToastRequest)"/> for any other caller.
/// </summary>
public partial class DockWindow
{
    private static readonly TimeSpan MeetingToastPoll = TimeSpan.FromSeconds(20);

    private readonly ToastDeduper _toastDedupe = new();
    private DockToastWindow? _toastHost;
    private DispatcherTimer? _meetingToastTimer;

    /// <summary>
    /// Public API: shows a toast above the dock. Safe from any thread (off the UI thread it is queued and returns
    /// true). Returns false when it was dropped because toasts are off (<c>AppConfig.DockToastsEnabled</c>), the dock
    /// is hidden, a fullscreen app runs, Quiet Island is on (unless <see cref="DockToastRequest.IsImportant"/>), or the
    /// same <see cref="DockToastRequest.Key"/> already toasted within the last 24 hours.
    /// Callers resolve the dock from DI (<c>GetRequiredService&lt;DockWindow&gt;()</c>).
    /// </summary>
    public bool ShowDockToast(DockToastRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => ShowDockToast(request));
            return true;
        }
        if (_isClosed || _isSmokeOrAuditMode || _toastHost is null) return false;

        var cfg = _orch.Config;
        bool dockShown = cfg.UseCustomBottomDock && !_isFullscreenHidden && Visibility == Visibility.Visible;
        bool fullscreen = _orch.FullscreenDetector.Current.IsFullscreen;
        if (!dockShown || !DockToastPlanner.ShouldShow(cfg.DockToastsEnabled, fullscreen, cfg.QuietIslandEnabled, request.IsImportant))
            return false;
        if (!_toastDedupe.TryMark(request.Key, DateTimeOffset.UtcNow)) return false;

        _toastHost.Show(request);
        return true;
    }

    /// <summary>Convenience overload; <paramref name="key"/> defaults to title + subtitle (dedupes identical toasts).</summary>
    public bool ShowDockToast(string title, string? subtitle = null, string? actionLabel = null, Action? action = null,
        string? accentHex = null, string? iconGlyph = null, string? key = null, bool important = false) =>
        ShowDockToast(new DockToastRequest(key ?? "custom:" + title + "|" + subtitle, title, subtitle, accentHex, iconGlyph,
            actionLabel, action, important));

    private void InitializeDockToasts()
    {
        if (_isSmokeOrAuditMode || _toastHost is not null) return;
        _toastHost = new DockToastWindow(() => _orch.Config.Language, ResolveToastAnchor);
        _orch.Coordinator.HudRequested += OnHudForDockToast;
        _meetingToastTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = MeetingToastPoll };
        _meetingToastTimer.Tick += (_, _) => CheckMeetingToast();
        _meetingToastTimer.Start();
        CheckMeetingToast();
    }

    private void ShutdownDockToasts()
    {
        _orch.Coordinator.HudRequested -= OnHudForDockToast;
        _meetingToastTimer?.Stop();
        _meetingToastTimer = null;
        _toastHost?.Close();
        _toastHost = null;
    }

    private DockToastAnchor? ResolveToastAnchor()
    {
        if (_hwnd == 0) return null;
        return new DockToastAnchor(ComputeCurrentDockHitRectPx(), _activeMonitor.DpiScale, IsDockAtTop, _hwnd);
    }

    private void OnHudForDockToast(object? sender, TransientHudEvent hud)
    {
        // HudRequested only fires for HUDs that passed Quiet Island / fullscreen suppression on the island side.
        if (_isClosed) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_isClosed) return;
            var req = DockToastPlanner.FromHud(hud, _orch.Config.Language,
                openTimers: () => _orch.Coordinator.RequestExpand(ExpandedTabId.Now),
                openAiQuota: () => _orch.Coordinator.RequestExpand(ExpandedTabId.AiQuota));
            if (req is null) return;
            // The same timer or account can legitimately finish again later; only collapse repeats of one event.
            if (!_toastDedupe.TryMark(req.Key + "@hud", DateTimeOffset.UtcNow, TimeSpan.FromSeconds(90))) return;
            ShowDockToast(req with { Key = req.Key + "@" + DateTime.UtcNow.Ticks });
        });
    }

    private void CheckMeetingToast()
    {
        if (_isClosed || _toastHost is null) return;
        var meeting = _orch.Calendar.ActiveOrNextMeeting;
        if (meeting is null) return;
        string lang = _orch.Config.Language;
        var plan = DockToastPlanner.PlanMeetingReminder(meeting.Summary, meeting.StartUtc, meeting.IsAllDay, DateTimeOffset.UtcNow, lang);
        if (plan is null || _toastDedupe.HasSeen(plan.Key, DateTimeOffset.UtcNow)) return;

        string? link = meeting.JoinUrl;
        ShowDockToast(new DockToastRequest(
            plan.Key,
            plan.Title,
            plan.Subtitle,
            AccentHex: "#FF453A",
            IconGlyph: "",
            ActionLabel: Strings.Pick(lang, "Takvim", "Calendar"),
            Action: () => OpenMeetingFromToast(link),
            IsImportant: true));
    }

    private void OpenMeetingFromToast(string? joinUrl)
    {
        if (!string.IsNullOrWhiteSpace(joinUrl))
        {
            try
            {
                Process.Start(new ProcessStartInfo(joinUrl) { UseShellExecute = true });
                return;
            }
            catch { }
        }
        _orch.Coordinator.RequestExpand(ExpandedTabId.Now);
    }

    // ---- render audit ----------------------------------------------------------------------------------------

    /// <summary>Three stacked toasts above the dock over a wallpaper-like backdrop (deterministic).</summary>
    public RenderTargetBitmap RenderToastsForAudit(int width = 820, int height = 380)
    {
        // Fixed dock content (no live clock widgets) so the frame is deterministic.
        RenderWidgetDockForAudit(["ai-limits"], Core.Config.DockVisualStyle.Midnight, width, 96);
        if (_clockTimeText is not null) _clockTimeText.Text = "15:55";
        if (_clockDateText is not null) _clockDateText.Text = _orch.Config.Language == "en" ? "19 Nov" : "19 Kas";
        DockRootGrid.Measure(new Size(width, 96));
        DockRootGrid.Arrange(new Rect(0, 0, width, 96));
        DockRootGrid.UpdateLayout();
        var dockBmp = new RenderTargetBitmap(width, 96, 96, 96, PixelFormats.Pbgra32);
        dockBmp.Render(DockRootGrid);
        dockBmp.Freeze();
        string lang = _orch.Config.Language;
        bool en = lang == "en";
        var requests = new[]
        {
            new DockToastRequest("audit:quota", en ? "Claude limit reset" : "Claude limiti sıfırlandı",
                en ? "Session is ready again · 100% left" : "Oturum yeniden hazır · %100 kaldı",
                AccentHex: "#D97757", IconGlyph: "", ActionLabel: en ? "Manage" : "Yönet"),
            new DockToastRequest("audit:timer", en ? "Focus session finished" : "Odak oturumu bitti",
                en ? "25 min Pomodoro · time for a break" : "25 dk Pomodoro · mola zamanı",
                AccentHex: "#FF9F0A", IconGlyph: ""),
            new DockToastRequest("audit:meeting", en ? "Design review in 5 min" : "Tasarım incelemesi 5 dk sonra",
                en ? "at 16:00" : "Saat 16:00",
                AccentHex: "#FF453A", IconGlyph: "", ActionLabel: en ? "Calendar" : "Takvim")
        }; // oldest first; the meeting reminder is the newest and sits nearest the dock

        const double cardW = 356;
        var canvas = new Canvas { Width = width, Height = height };
        var wallpaper = new LinearGradientBrush(
            new GradientStopCollection
            {
                new(Color.FromRgb(0x1B, 0x2A, 0x4A), 0),
                new(Color.FromRgb(0x3A, 0x2A, 0x55), 0.55),
                new(Color.FromRgb(0x6B, 0x3A, 0x4A), 1)
            }, new Point(0, 0), new Point(1, 1));
        canvas.Background = wallpaper;
        canvas.Children.Add(new System.Windows.Shapes.Ellipse
        {
            Width = 420, Height = 420, Opacity = 0.55,
            Fill = new RadialGradientBrush(Color.FromArgb(0xC0, 0xFF, 0x9F, 0x6A), Color.FromArgb(0, 0xFF, 0x9F, 0x6A))
        });
        Canvas.SetLeft(canvas.Children[^1], width * 0.58);
        Canvas.SetTop(canvas.Children[^1], -120);

        var dockImg = new System.Windows.Controls.Image { Source = dockBmp, Width = width, Height = 96 };
        Canvas.SetTop(dockImg, height - 96);

        double dockTop = height - 96 + (96 - Math.Max(52, DockPillContainer.ActualHeight)) - 6;
        var cards = requests.Select(r => DockToastWindow.BuildCardForAudit(r, lang, cardW)).ToList();
        var heightsNewestFirst = cards.AsEnumerable().Reverse().Select(c => c.DesiredSize.Height).ToList();
        for (int i = 0; i < cards.Count; i++)
        {
            int indexFromDock = cards.Count - 1 - i;
            double y = dockTop - DockToastWindow.DockGapDip
                - DockToastPlanner.OffsetFromDock(heightsNewestFirst, indexFromDock, DockToastWindow.CardGapDip)
                - cards[i].DesiredSize.Height;
            Canvas.SetLeft(cards[i], Math.Round((width - cardW) / 2));
            Canvas.SetTop(cards[i], Math.Round(y));
            canvas.Children.Add(cards[i]);
        }
        canvas.Children.Add(dockImg);

        canvas.Measure(new Size(width, height));
        canvas.Arrange(new Rect(0, 0, width, height));
        canvas.UpdateLayout();
        var bmp = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(canvas);
        if (bmp.CanFreeze) bmp.Freeze();
        return bmp;
    }
}
