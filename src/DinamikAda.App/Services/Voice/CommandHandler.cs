using DinamikAda.Core.Voice;
using DinamikAda.Modules.Media;

namespace DinamikAda.App.Services.Voice;

/// <summary>Voice commands: open apps, search, schedule, routines, todos, timers, media. (Stub; filled by the commands group.)</summary>
public sealed class CommandHandler : IVoiceModeHandler
{
    private readonly VoiceScheduler _scheduler;

    public CommandHandler(VoiceScheduler scheduler) => _scheduler = scheduler;

    public VoiceMode Mode => VoiceMode.Command;

    public bool Begin(VoiceSession session)
    {
        session.Overlay.ShowListening(Mode, session.T("Komutunu söyle…", "Say a command…"));
        return true;
    }

    public Task CompleteAsync(VoiceSession session, MicCapture audio, CancellationToken ct)
    {
        session.Overlay.Hide();
        return Task.CompletedTask;
    }

    public void Cancel(VoiceSession session) { }
}
