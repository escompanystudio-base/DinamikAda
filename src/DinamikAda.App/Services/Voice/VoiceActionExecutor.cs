using System.Diagnostics;
using System.Globalization;
using System.IO;
using DinamikAda.Core.Config;
using DinamikAda.Core.Voice;

namespace DinamikAda.App.Services.Voice;

/// <summary>The outcome of one voice action, with a short line for the overlay.</summary>
public sealed record VoiceActionResult(bool Ok, string Text);

/// <summary>
/// Runs voice actions (open apps and sites, search, to-dos, timers, media, reminders) on the UI thread.
/// Does nothing in smoke/audit mode.
/// </summary>
public sealed class VoiceActionExecutor
{
    private static readonly TimeSpan IndexWait = TimeSpan.FromSeconds(6);
    private readonly AppOrchestrator _orch;

    public VoiceActionExecutor(AppOrchestrator orch) => _orch = orch;

    private string Lang => _orch.Config.Language;
    private string T(string tr, string en) => Lang == "en" ? en : tr;

    public async Task<List<VoiceActionResult>> ExecuteAllAsync(IEnumerable<VoiceAction> actions, CancellationToken ct = default)
    {
        var results = new List<VoiceActionResult>();
        foreach (var a in actions)
        {
            ct.ThrowIfCancellationRequested();
            results.Add(await ExecuteAsync(a, ct));
        }
        return results;
    }

    public async Task<VoiceActionResult> ExecuteAsync(VoiceAction a, CancellationToken ct = default)
    {
        string done = VoiceIntents.DescribeAction(a, Lang);
        if (_orch.IsSmokeOrAuditMode) return new(false, T("Test modunda çalıştırılmadı.", "Not run in test mode."));
        try
        {
            switch (a.Kind)
            {
                case VoiceActionKind.OpenApp:
                    return await OpenAppAsync(a.Target, ct)
                        ? new(true, done)
                        : new(false, T($"{a.Target} bulunamadı.", $"Couldn't find {a.Target}."));
                case VoiceActionKind.OpenUrl:
                {
                    string? url = VoiceIntents.NormalizeUrl(a.Target);
                    if (url is null) return new(false, T("Adres geçersiz.", "That address isn't valid."));
                    ShellOpen(url);
                    return new(true, done);
                }
                case VoiceActionKind.WebSearch:
                {
                    string lang = VoiceAssistantService.SpeechLanguageFor(_orch.Config);
                    ShellOpen(VoiceIntents.BuildSearchUrl(a.Target, a.Query, lang));
                    return new(true, done);
                }
                case VoiceActionKind.AddTodo:
                    return AddTodo(a.Query) ? new(true, done) : new(false, T("Yapılacaklar listesi dolu.", "The to-do list is full."));
                case VoiceActionKind.StartTimer:
                {
                    if (!int.TryParse(a.Query, NumberStyles.Integer, CultureInfo.InvariantCulture, out int minutes) || minutes <= 0)
                        return new(false, T("Süre anlaşılamadı.", "Couldn't understand the duration."));
                    string label = string.IsNullOrWhiteSpace(a.Target) ? T("Sesli zamanlayıcı", "Voice timer") : a.Target;
                    _orch.Timers.StartCountdown(label, TimeSpan.FromMinutes(Math.Clamp(minutes, 1, 600)), isPomodoro: false);
                    return new(true, done);
                }
                case VoiceActionKind.MediaPlayPause:
                    await _orch.Media.TogglePlayPauseAsync(ct);
                    return new(true, done);
                case VoiceActionKind.MediaNext:
                    await _orch.Media.NextAsync(ct);
                    return new(true, done);
                case VoiceActionKind.MediaPrevious:
                    await _orch.Media.PreviousAsync(ct);
                    return new(true, done);
                case VoiceActionKind.Notify:
                    Notify(T("Hatırlatma", "Reminder"), a.Query);
                    return new(true, done);
                default:
                    return new(false, T("Bu komut desteklenmiyor.", "That command isn't supported."));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            CrashLogger.Log("Voice", ex, $"Voice action {a.Kind} failed.");
            return new(false, a.Kind == VoiceActionKind.OpenApp
                ? T($"{a.Target} açılamadı.", $"Couldn't open {a.Target}.")
                : T("Komut çalıştırılamadı.", "The command couldn't run."));
        }
    }

    /// <summary>Shows a small notification from the assistant (no-op in smoke/audit mode).</summary>
    public void Notify(string title, string body)
    {
        if (_orch.IsSmokeOrAuditMode) return;
        try
        {
            string source = string.IsNullOrWhiteSpace(_orch.Config.Mascot?.Name) ? "DinamikAda" : _orch.Config.Mascot.Name;
            _orch.Notifications.AddInternalAlert(source, title, body);
        }
        catch (Exception ex)
        {
            CrashLogger.Log("Voice", ex, "Voice notification failed.");
        }
    }

    private async Task<bool> OpenAppAsync(string name, CancellationToken ct)
    {
        name = VoiceIntents.CleanName(name);
        if (name.Length == 0) return false;

        // 1) The launcher's installed-app index (Start menu shortcuts and Store apps).
        try
        {
            await _orch.Launcher.RebuildAppIndexAsync(includeStoreApps: true, ct).WaitAsync(IndexWait, ct);
        }
        catch (TimeoutException)
        {
            // Still indexing: use what is there.
        }
        var apps = _orch.Launcher.GetIndexedAppsSnapshot();
        int i = VoiceIntents.FindBestNameMatch(name, apps.Select(x => x.DisplayName).ToList());
        if (i >= 0)
        {
            var result = await _orch.Launcher.LaunchInstalledAppAsync(apps[i]);
            if (result.Succeeded) return true;
        }

        // 2) Pinned dock apps, matched by file name.
        var pins = (_orch.Config.PinnedDockApps ?? new List<string>()).Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        int p = VoiceIntents.FindBestNameMatch(name, pins.Select(SafeFileName).ToList());
        if (p >= 0)
        {
            ShellOpen(pins[p]);
            return true;
        }

        // 3) Let the shell resolve a plain program name ("notepad", "calc").
        if (name.IndexOfAny(new[] { '\\', '/', ':', '"', '%', '|', '<', '>', '&' }) >= 0 || name.StartsWith('-')) return false;
        try
        {
            ShellOpen(name);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string SafeFileName(string path)
    {
        try { return Path.GetFileNameWithoutExtension(path); }
        catch (ArgumentException) { return path; }
    }

    private bool AddTodo(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return false;
        if ((_orch.Config.DockTodos?.Count ?? 0) >= 50) return false;
        var item = new DockTodoItem { Text = text.Length > 200 ? text[..200] : text };
        _orch.ConfigStore.Update(_orch.Config, c => c.DockTodos = (c.DockTodos ?? new()).Append(item).ToList());
        return true;
    }

    private static void ShellOpen(string target)
    {
        using var _ = Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
    }
}
