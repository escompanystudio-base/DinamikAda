using System.Windows.Threading;
using DinamikAda.App.Views.Voice;
using DinamikAda.Core.Config;
using DinamikAda.Core.Voice;
using DinamikAda.Modules.Media;
using DinamikAda.Platform.Win32.Input;

namespace DinamikAda.App.Services.Voice;

/// <summary>Everything a mode handler needs for one push-to-talk session.</summary>
public sealed class VoiceSession
{
    public required VoiceMode Mode { get; init; }
    public required AppOrchestrator Orch { get; init; }
    public required GeminiClient Gemini { get; init; }
    public required IVoiceOverlay Overlay { get; init; }
    /// <summary>True when this session should use Gemini (key present and engine allows it).</summary>
    public required bool UseGemini { get; init; }
    /// <summary>BCP-47 speech language, e.g. "tr-TR".</summary>
    public required string SpeechLanguage { get; init; }
    public DateTime StartedLocal { get; } = DateTime.Now;
    public AppConfig Config => Orch.Config;
    public string Lang => Orch.Config.Language;
    public string T(string tr, string en) => Lang == "en" ? en : tr;
    /// <summary>Free slot for a handler's per-session state (e.g. the circle overlay).</summary>
    public object? State { get; set; }

    /// <summary>Live microphone level (0..1) while the keys are held, on the UI thread.</summary>
    public event Action<double>? LevelChanged;

    internal void RaiseLevel(double level) => LevelChanged?.Invoke(level);
}

/// <summary>One voice mode (dictation, ask, command). Methods run on the UI thread.</summary>
public interface IVoiceModeHandler
{
    VoiceMode Mode { get; }
    /// <summary>Keys pressed and recording started. Return false to abort the session.</summary>
    bool Begin(VoiceSession session);
    /// <summary>Keys released; the recording is ready. Show progress/results through <see cref="VoiceSession.Overlay"/>.</summary>
    Task CompleteAsync(VoiceSession session, MicCapture audio, CancellationToken ct);
    /// <summary>The hold was too short, broken by another key, or the recording failed.</summary>
    void Cancel(VoiceSession session);
}

/// <summary>The floating assistant UI (mascot, live level, status and answers).</summary>
public interface IVoiceOverlay
{
    void ShowListening(VoiceMode mode, string hint);
    void SetLevel(double level01);
    void ShowWorking(string text);
    /// <summary>Shows an answer or confirmation; hides itself after <paramref name="autoHide"/> (null = until clicked).</summary>
    void ShowResult(string title, string text, TimeSpan? autoHide);
    void ShowError(string text);
    void Hide();
}

/// <summary>
/// Push-to-talk voice assistant: hold a chord to talk, release to act.
/// <list type="bullet">
/// <item>Ctrl+Win dictates text at the cursor.</item>
/// <item>Ctrl+Shift lets the user circle something on screen and ask about it.</item>
/// <item>Alt+Win runs voice commands, schedules and routines.</item>
/// </list>
/// Owns the keyboard hook, the microphone and the overlay, and hands each finished recording to the mode's handler.
/// </summary>
public sealed class VoiceAssistantService : IDisposable
{
    private static readonly TimeSpan MinHold = TimeSpan.FromMilliseconds(350);

    private readonly AppOrchestrator _orch;
    private readonly Dispatcher _dispatcher;
    private readonly KeyboardHoldService _keys = new();
    private readonly MicCaptureService _mic = new();
    private readonly GeminiClient _gemini;
    private readonly Dictionary<VoiceMode, IVoiceModeHandler> _handlers = new();
    private readonly DispatcherTimer _configTimer;
    private IVoiceOverlay? _overlay;
    private VoiceSession? _session;
    private CancellationTokenSource? _completeCts;
    private bool _busy;
    private string _configKey = string.Empty;
    private bool _disposed;

    public VoiceAssistantService(AppOrchestrator orch)
    {
        _orch = orch;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _gemini = new GeminiClient(orch);
        Scheduler = new VoiceScheduler(orch);
        Register(new DictationHandler());
        Register(new AskHandler());
        Register(new CommandHandler(Scheduler));
        // The hook may raise events off the UI thread (no sync context at startup): always marshal to the dispatcher.
        _keys.ChordPressed += (_, id) => _dispatcher.BeginInvoke(() => OnPressed(id));
        _keys.ChordReleased += (_, e) => _dispatcher.BeginInvoke(() => OnReleased(e.Id, e.Held));
        _keys.ChordCancelled += (_, id) => _dispatcher.BeginInvoke(() => OnCancelled(id));
        _mic.LevelChanged += (_, level) => _dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            _overlay?.SetLevel(level);
            _session?.RaiseLevel(level);
        });
        _configTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
        _configTimer.Tick += (_, _) => SyncConfig();
    }

    public GeminiClient Gemini => _gemini;
    public VoiceScheduler Scheduler { get; }
    public IVoiceOverlay Overlay => _overlay ??= new VoiceOverlayWindow(_orch);

    private void Register(IVoiceModeHandler h) => _handlers[h.Mode] = h;

    /// <summary>Installs the hook (on the UI thread) and starts the scheduler.</summary>
    public void Start()
    {
        SyncConfig();
        _configTimer.Start();
        Scheduler.Start();
    }

    private void SyncConfig()
    {
        if (_disposed) return;
        var c = _orch.Config;
        string key = $"{c.VoiceAssistantEnabled}|{c.DictationChord}|{c.AskChord}|{c.CommandChord}";
        if (key == _configKey) return;
        _configKey = key;
        if (!c.VoiceAssistantEnabled)
        {
            _keys.Uninstall();
            return;
        }
        var chords = new List<KeyValuePair<string, HoldChordText.Mods>>();
        void Add(VoiceMode m, string text)
        {
            if (HoldChordText.Parse(text) is { } mods && chords.All(x => x.Value != mods))
                chords.Add(new(m.ToString(), mods));
        }
        Add(VoiceMode.Dictation, c.DictationChord);
        Add(VoiceMode.Ask, c.AskChord);
        Add(VoiceMode.Command, c.CommandChord);
        _keys.SetChords(chords);
        if (!_keys.IsInstalled && !_keys.Install())
            CrashLogger.Log("Voice", null, "Keyboard hook could not be installed.");
    }

    /// <summary>Speech language: the configured tag, else from the app language.</summary>
    public static string SpeechLanguageFor(AppConfig c) =>
        !string.IsNullOrWhiteSpace(c.VoiceLanguage) ? c.VoiceLanguage : c.Language == "en" ? "en-US" : "tr-TR";

    private bool UsesGemini(VoiceMode mode)
    {
        var c = _orch.Config;
        if (c.VoiceEngine == VoiceEngine.Windows && mode == VoiceMode.Dictation) return false;
        return _gemini.HasKey;
    }

    private void OnPressed(string id)
    {
        if (_disposed || _busy || _session is not null || !Enum.TryParse(id, out VoiceMode mode)) return;
        if (!_handlers.TryGetValue(mode, out var handler)) return;
        var c = _orch.Config;
        bool gemini = UsesGemini(mode);
        string t(string tr, string en) => c.Language == "en" ? en : tr;
        if (!gemini && mode != VoiceMode.Dictation)
        {
            Overlay.ShowError(t("Bu özellik için Gemini API anahtarı gerekli: Ayarlar → Sesli Asistan.",
                "This needs a Gemini API key: Settings → Voice Assistant."));
            return;
        }
        var session = new VoiceSession
        {
            Mode = mode,
            Orch = _orch,
            Gemini = _gemini,
            Overlay = Overlay,
            UseGemini = gemini,
            SpeechLanguage = SpeechLanguageFor(c)
        };
        // Without Gemini, dictation relies on Windows recognition running during the recording.
        string? windowsLang = gemini ? null : session.SpeechLanguage;
        if (!_mic.Start(windowsLang, mode == VoiceMode.Ask ? TimeSpan.FromSeconds(90) : TimeSpan.FromMinutes(3)))
            return;
        _session = session;
        try
        {
            if (!handler.Begin(session))
            {
                _mic.Cancel();
                _session = null;
            }
        }
        catch (Exception ex)
        {
            CrashLogger.Log("Voice", ex, "Voice handler failed to begin.");
            _mic.Cancel();
            _session = null;
            Overlay.ShowError(t("Sesli asistan başlatılamadı.", "The voice assistant could not start."));
        }
    }

    private async void OnReleased(string id, TimeSpan held)
    {
        var session = _session;
        if (session is null || session.Mode.ToString() != id) return;
        _session = null;
        var handler = _handlers[session.Mode];
        if (held < MinHold)
        {
            _mic.Cancel();
            SafeCancel(handler, session);
            return;
        }
        _busy = true;
        _completeCts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try
        {
            var audio = await _mic.StopAsync();
            if (audio is null || audio.IsSilent)
            {
                SafeCancel(handler, session);
                session.Overlay.ShowError(session.T("Ses algılanmadı. Mikrofonu kontrol et.", "No speech detected. Check your microphone."));
                return;
            }
            await handler.CompleteAsync(session, audio, _completeCts.Token);
        }
        catch (OperationCanceledException)
        {
            session.Overlay.ShowError(session.T("İşlem zaman aşımına uğradı.", "That took too long."));
        }
        catch (GeminiException gex)
        {
            session.Overlay.ShowError(gex.Message);
        }
        catch (Exception ex)
        {
            CrashLogger.Log("Voice", ex, $"Voice {session.Mode} failed.");
            session.Overlay.ShowError(session.T("Bir şeyler ters gitti.", "Something went wrong."));
        }
        finally
        {
            _completeCts?.Dispose();
            _completeCts = null;
            _busy = false;
        }
    }

    private void OnCancelled(string id)
    {
        var session = _session;
        if (session is null || session.Mode.ToString() != id) return;
        _session = null;
        _mic.Cancel();
        SafeCancel(_handlers[session.Mode], session);
    }

    private static void SafeCancel(IVoiceModeHandler handler, VoiceSession session)
    {
        try { handler.Cancel(session); }
        catch (Exception ex) { CrashLogger.Log("Voice", ex, "Voice handler cancel failed."); }
        session.Overlay.Hide();
    }

    /// <summary>Adds dictated words and speaking time to the speed statistics.</summary>
    public static void RecordDictationStats(AppOrchestrator orch, string text, TimeSpan spoken)
    {
        int words = GeminiJson.CountWords(text);
        if (words == 0) return;
        try
        {
            orch.ConfigStore.Update(orch.Config, c => c.VoiceStats = new VoiceStats
            {
                WordsDictated = c.VoiceStats.WordsDictated + words,
                SecondsSpoken = c.VoiceStats.SecondsSpoken + spoken.TotalSeconds,
                Sessions = c.VoiceStats.Sessions + 1
            });
        }
        catch (Exception ex)
        {
            CrashLogger.Log("Voice", ex, "Could not save voice stats.");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _configTimer.Stop();
        _keys.Dispose();
        _mic.Dispose();
        _completeCts?.Cancel();
        Scheduler.Dispose();
        (_overlay as IDisposable)?.Dispose();
    }
}
