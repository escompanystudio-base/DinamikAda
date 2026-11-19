using System.Runtime.InteropServices;
using DinamikAda.Core.Voice;
using DinamikAda.Modules.Media;
using DinamikAda.Platform.Win32.Input;

namespace DinamikAda.App.Services.Voice;

/// <summary>
/// Dictation: hold the chord, speak, release, and the cleaned-up text is typed where the cursor is.
/// Gemini transcribes and tidies the speech; without a key the offline Windows transcript is used.
/// When typing is impossible the text goes to the clipboard instead.
/// </summary>
public sealed class DictationHandler : IVoiceModeHandler
{
    private static readonly TimeSpan ModifierWait = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan ResultTime = TimeSpan.FromSeconds(1.6);

    public VoiceMode Mode => VoiceMode.Dictation;

    public bool Begin(VoiceSession session)
    {
        session.Overlay.ShowListening(Mode, session.T("Dinliyorum…", "Listening…"));
        return true;
    }

    public async Task CompleteAsync(VoiceSession session, MicCapture audio, CancellationToken ct)
    {
        var cfg = session.Config;
        string text;
        if (session.UseGemini)
        {
            session.Overlay.ShowWorking(session.T("Yazıya çevriliyor…", "Transcribing…"));
            string system = DictationText.BuildSystemPrompt(session.SpeechLanguage, cfg.DictationCleanup);
            var parts = new[]
            {
                GeminiPart.FromBytes("audio/wav", audio.Wav),
                GeminiPart.FromText(cfg.DictationCleanup
                    ? "Transcribe this dictation following the rules."
                    : "Transcribe this dictation verbatim following the rules.")
            };
            string reply = await session.Gemini.GenerateAsync(system, parts, null, temperature: 0.1, maxOutputTokens: 4096, ct: ct);
            text = DictationText.Clean(reply);
        }
        else
        {
            string? windows = audio.WindowsTranscript;
            if (string.IsNullOrWhiteSpace(windows))
            {
                session.Overlay.ShowError(session.T(
                    "Windows konuşma tanıma bu dil için kullanılamıyor ya da sesi anlayamadı. Ayarlar → Sesli Asistan'dan bir Gemini anahtarı ekle " +
                    "veya Windows Ayarları → Saat ve dil → Konuşma bölümünden bu dilin konuşma paketini yükle.",
                    "Windows speech recognition isn't available for this language or couldn't understand you. Add a Gemini key in " +
                    "Settings → Voice Assistant, or install the speech language pack in Windows Settings → Time & language → Speech."));
                return;
            }
            text = DictationText.Clean(DictationText.ApplySpokenFormatting(windows));
        }

        ct.ThrowIfCancellationRequested();
        if (text.Length == 0)
        {
            session.Overlay.ShowError(session.T("Bir şey duyamadım. Tekrar dene.", "I didn't catch anything. Try again."));
            return;
        }

        await InsertAsync(session, text, ct);
        VoiceAssistantService.RecordDictationStats(session.Orch, text, audio.Duration);
    }

    public void Cancel(VoiceSession session) { }

    private static async Task InsertAsync(VoiceSession session, string text, CancellationToken ct)
    {
        string preview = DictationText.Preview(text, 80);
        // Smoke/audit runs never touch the OS: no typing, no clipboard.
        if (session.Orch.IsSmokeOrAuditMode)
        {
            session.Overlay.ShowResult(session.T("Yazıldı", "Typed"), preview, ResultTime);
            return;
        }

        // Typing while Ctrl/Win are still physically down would turn letters into shortcuts.
        bool released = await KeyboardHoldService.WaitForModifiersReleasedAsync(ModifierWait, ct);
        if (released && TextInjector.Type(text))
        {
            session.Overlay.ShowResult(session.T("Yazıldı", "Typed"), preview, ResultTime);
            return;
        }

        if (await TrySetClipboardAsync(text, ct))
        {
            session.Overlay.ShowResult(session.T("Panoya kopyalandı", "Copied to clipboard"),
                session.T("Yazamadım; Ctrl+V ile yapıştır.", "Couldn't type it here; paste with Ctrl+V.") + "\n" + preview,
                TimeSpan.FromSeconds(4));
        }
        else
        {
            session.Overlay.ShowResult(session.T("Yazılamadı", "Couldn't type"), text, null);
        }
    }

    /// <summary>Puts text on the clipboard from the (STA) UI thread, retrying while another app holds it open.</summary>
    private static async Task<bool> TrySetClipboardAsync(string text, CancellationToken ct)
    {
        for (int attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                System.Windows.Clipboard.SetText(text);
                return true;
            }
            catch (ExternalException)
            {
                // CLIPBRD_E_CANT_OPEN: another process has the clipboard open; wait and retry.
            }
            await Task.Delay(60 * (attempt + 1), ct);
        }
        return false;
    }
}
