namespace DinamikAda.App.Services.Voice;

/// <summary>Runs scheduled voice tasks and daily routines. (Stub; filled by the commands group.)</summary>
public sealed class VoiceScheduler : IDisposable
{
    private readonly AppOrchestrator _orch;

    public VoiceScheduler(AppOrchestrator orch) => _orch = orch;

    public void Start() { }

    public void Dispose() { }
}
