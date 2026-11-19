using DinamikAda.Core.Voice;
using DinamikAda.Modules.Media;

namespace DinamikAda.App.Services.Voice;

/// <summary>Circle to ask: circle something on screen while asking; the answer appears next to it. (Stub; filled by the ask group.)</summary>
public sealed class AskHandler : IVoiceModeHandler
{
    public VoiceMode Mode => VoiceMode.Ask;

    public bool Begin(VoiceSession session)
    {
        session.Overlay.ShowListening(Mode, session.T("Daire çiz ve sor…", "Circle and ask…"));
        return true;
    }

    public Task CompleteAsync(VoiceSession session, MicCapture audio, CancellationToken ct)
    {
        session.Overlay.Hide();
        return Task.CompletedTask;
    }

    public void Cancel(VoiceSession session) { }
}
