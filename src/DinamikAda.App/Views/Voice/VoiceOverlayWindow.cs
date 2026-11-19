using DinamikAda.App.Services;
using DinamikAda.App.Services.Voice;
using DinamikAda.Core.Voice;

namespace DinamikAda.App.Views.Voice;

/// <summary>Floating assistant overlay with the mascot. (Stub; filled by the mascot/UI group.)</summary>
public sealed class VoiceOverlayWindow : IVoiceOverlay, IDisposable
{
    private readonly AppOrchestrator _orch;

    public VoiceOverlayWindow(AppOrchestrator orch) => _orch = orch;

    public void ShowListening(VoiceMode mode, string hint) { }
    public void SetLevel(double level01) { }
    public void ShowWorking(string text) { }
    public void ShowResult(string title, string text, TimeSpan? autoHide) { }
    public void ShowError(string text) { }
    public void Hide() { }
    public void Dispose() { }
}
