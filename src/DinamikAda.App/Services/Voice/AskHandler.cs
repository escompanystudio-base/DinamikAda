using DinamikAda.App.Views.Voice;
using DinamikAda.Core.Voice;
using DinamikAda.Modules.Media;
using DinamikAda.Platform.Win32.Interop;

namespace DinamikAda.App.Services.Voice;

/// <summary>
/// Circle to ask: hold the chord, circle something on screen while asking out loud, release, and the answer appears
/// next to what was circled. The screenshot stays in memory only.
/// </summary>
public sealed class AskHandler : IVoiceModeHandler
{
    /// <summary>Per-session state kept in <see cref="VoiceSession.State"/>.</summary>
    private sealed class AskState(CircleAskOverlay overlay, nint previousForeground)
    {
        public CircleAskOverlay Overlay { get; } = overlay;
        public nint PreviousForeground { get; } = previousForeground;
    }

    /// <summary>The overlay still on screen from the last question (an answer card), closed when a new one starts.</summary>
    private CircleAskOverlay? _last;

    public VoiceMode Mode => VoiceMode.Ask;

    public bool Begin(VoiceSession session)
    {
        if (session.Orch.IsSmokeOrAuditMode) return false;

        _last?.Dismiss(quick: true);
        _last = null;

        nint previous = NativeMethods.GetForegroundWindow();
        var frame = CircleAskOverlay.CaptureMonitorUnderCursor();
        if (frame is null)
        {
            session.Overlay.ShowError(session.T("Ekran görüntüsü alınamadı.", "Could not capture the screen."));
            return false;
        }

        var overlay = new CircleAskOverlay(frame, session.T);
        overlay.Closed += (_, _) =>
        {
            if (ReferenceEquals(_last, overlay)) _last = null;
            // Hand focus back to the app the user was in (we are foreground, so this is allowed).
            if (previous != 0) NativeMethods.SetForegroundWindow(previous);
        };
        overlay.Show();
        overlay.BeginTracking();
        session.LevelChanged += overlay.SetVoiceLevel;
        _last = overlay;
        session.State = new AskState(overlay, previous);
        session.Overlay.ShowListening(Mode, session.T("Daire çiz ve sor…", "Circle and ask…"));
        return true;
    }

    public async Task CompleteAsync(VoiceSession session, MicCapture audio, CancellationToken ct)
    {
        if (session.State is not AskState state || state.Overlay.IsDismissed)
        {
            session.Overlay.Hide();
            return;
        }

        var overlay = state.Overlay;
        var path = overlay.StopTracking();
        double scale = overlay.DpiScale;
        var region = AskGeometry.ComputeRegion(path, overlay.WidthPx, overlay.HeightPx,
            minSize: 160 * scale, tinyThreshold: 24 * scale);
        overlay.ShowRegion(region);
        overlay.ShowThinking(region, session.T("Düşünüyor…", "Thinking…"));
        session.Overlay.ShowWorking(session.T("Düşünüyor…", "Thinking…"));

        // Closing the overlay (Esc, click outside) abandons the question quietly.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        void OnDismissed() => linked.Cancel();
        overlay.Dismissed += OnDismissed;
        try
        {
            byte[] png = await overlay.CropPngAsync(region).ConfigureAwait(true);
            var cfg = session.Config;
            string system = AskGeometry.BuildSystemPrompt(session.SpeechLanguage, cfg.Mascot.Name, cfg.Mascot.Personality);
            var parts = new List<GeminiPart>
            {
                GeminiPart.FromText(AskGeometry.BuildUserPrompt()),
                GeminiPart.FromBytes("image/png", png),
                GeminiPart.FromBytes("audio/wav", audio.Wav)
            };
            string raw = await session.Gemini.GenerateAsync(system, parts, AskGeometry.ResponseSchemaJson,
                temperature: 0.4, maxOutputTokens: 2048, ct: linked.Token).ConfigureAwait(true);

            if (overlay.IsDismissed)
            {
                session.Overlay.Hide();
                return;
            }
            var parsed = AskGeometry.ParseAnswer(raw);
            string answer = AskGeometry.CleanMarkdown(parsed.Answer);
            if (answer.Length == 0)
                throw new GeminiException(session.T("Gemini boş yanıt döndü.", "Gemini returned an empty answer."));

            // The answer card takes over; the mascot steps aside so the card is the focus.
            session.Overlay.Hide();
            overlay.ShowAnswer(region, new AskAnswer(parsed.Question, answer));
        }
        catch (OperationCanceledException) when (overlay.IsDismissed && !ct.IsCancellationRequested)
        {
            session.Overlay.Hide();
        }
        catch
        {
            overlay.Dismiss(quick: true);
            throw;
        }
        finally
        {
            overlay.Dismissed -= OnDismissed;
        }
    }

    public void Cancel(VoiceSession session)
    {
        if (session.State is AskState state)
        {
            state.Overlay.Dismiss(quick: true);
            if (ReferenceEquals(_last, state.Overlay)) _last = null;
        }
        session.State = null;
    }
}
