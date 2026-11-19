using DinamikAda.Core.Voice;
using DinamikAda.Modules.Media;

namespace DinamikAda.App.Services.Voice;

/// <summary>Dictation: speak, release, and the cleaned-up text is typed where the cursor is. (Stub; filled by the dictation group.)</summary>
public sealed class DictationHandler : IVoiceModeHandler
{
    public VoiceMode Mode => VoiceMode.Dictation;

    public bool Begin(VoiceSession session)
    {
        session.Overlay.ShowListening(Mode, session.T("Dinliyorum…", "Listening…"));
        return true;
    }

    public Task CompleteAsync(VoiceSession session, MicCapture audio, CancellationToken ct)
    {
        session.Overlay.Hide();
        return Task.CompletedTask;
    }

    public void Cancel(VoiceSession session) { }
}
