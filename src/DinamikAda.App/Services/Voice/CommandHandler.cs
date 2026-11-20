using System.IO;
using DinamikAda.Core.Voice;
using DinamikAda.Modules.Media;

namespace DinamikAda.App.Services.Voice;

/// <summary>
/// Voice commands (Alt+Win): the recording goes to Gemini with a JSON response schema, the reply is parsed into a
/// <see cref="VoiceCommandPlan"/> in Core, then actions run now and schedules/routines are stored.
/// </summary>
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

    public async Task CompleteAsync(VoiceSession session, MicCapture audio, CancellationToken ct)
    {
        var cfg = session.Config;
        string lang = session.Lang;
        session.Overlay.ShowWorking(session.T("Anlıyorum…", "On it…"));

        var ctx = new VoicePromptContext(
            DateTime.Now,
            TimeZoneInfo.Local.DisplayName,
            session.SpeechLanguage,
            PinnedAppNames(cfg.PinnedDockApps),
            cfg.Mascot?.Name ?? "Ada",
            cfg.Mascot?.Personality ?? 0,
            (cfg.VoiceRoutines ?? new()).Select(r => r.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToList());

        string json = await session.Gemini.GenerateAsync(
            VoiceIntents.BuildSystemPrompt(ctx),
            new[] { GeminiPart.FromText("The user's spoken command is in the attached audio."), GeminiPart.FromBytes("audio/wav", audio.Wav) },
            VoiceIntents.ResponseSchemaJson,
            temperature: 0.2,
            maxOutputTokens: 2048,
            ct);
        ct.ThrowIfCancellationRequested();

        var plan = VoiceIntents.Parse(json, DateTime.Now);
        string mascot = string.IsNullOrWhiteSpace(cfg.Mascot?.Name) ? "Ada" : cfg.Mascot!.Name;

        if (!plan.HasWork)
        {
            if (plan.SkippedPast > 0)
            {
                Show(session, session.T("Olmadı", "Not scheduled"),
                    Join(plan.Reply, session.T("O zaman geçmiş; ileri bir tarih söyle.", "That time has passed; say a later time.")), false);
                return;
            }
            string text = plan.Reply.Length > 0 ? plan.Reply : session.T("Anlayamadım.", "I didn't catch that.");
            Show(session, mascot, text, plan.IsAnswer);
            return;
        }

        var notes = new List<string>();
        bool anyOk = false;

        // Actions that run now.
        var results = await _scheduler.Executor.ExecuteAllAsync(plan.Actions, ct);
        anyOk |= results.Any(r => r.Ok);
        notes.AddRange(results.Where(r => !r.Ok).Select(r => r.Text));

        // Schedules, reminders and routines.
        if (plan.Tasks.Count > 0)
        {
            _scheduler.AddTasks(plan.Tasks);
            anyOk = true;
        }
        if (plan.Routines.Count > 0)
        {
            _scheduler.AddRoutines(plan.Routines);
            anyOk = true;
        }
        foreach (string name in plan.DeleteRoutines)
        {
            var match = VoiceIntents.FindRoutine(cfg.VoiceRoutines ?? new(), name);
            if (match is not null && _scheduler.DeleteRoutine(match.Id))
            {
                anyOk = true;
                if (plan.Reply.Length == 0) notes.Add(session.T($"\"{match.Name}\" rutini silindi.", $"Deleted the \"{match.Name}\" routine."));
            }
            else
            {
                notes.Add(session.T($"\"{name}\" adlı rutin bulunamadı.", $"No routine called \"{name}\"."));
            }
        }

        string body = plan.Reply.Length > 0 ? plan.Reply : VoiceIntents.Summarize(plan, lang);
        if (plan.ListRoutines)
        {
            anyOk = true;
            body = Join(plan.Reply, VoiceIntents.FormatRoutineList(cfg.VoiceRoutines ?? new(), lang));
        }
        if (notes.Count > 0) body = Join(anyOk ? body : string.Empty, string.Join("\n", notes));
        if (body.Length == 0) body = session.T("Tamam.", "Done.");

        string title = !anyOk ? session.T("Yapılamadı", "Couldn't do that")
            : plan.ListRoutines ? session.T("Rutinler", "Routines")
            : session.T("Tamam", "Done");
        Show(session, title, body, plan.IsAnswer || plan.ListRoutines);
    }

    public void Cancel(VoiceSession session) { }

    private static void Show(VoiceSession session, string title, string text, bool longForm) =>
        session.Overlay.ShowResult(title, text, VoiceIntents.ResultDisplayTime(text, longForm));

    private static string Join(string a, string b) =>
        string.IsNullOrWhiteSpace(a) ? b : string.IsNullOrWhiteSpace(b) ? a : a + "\n" + b;

    /// <summary>Dock pin file names without extension, as app-name hints for Gemini.</summary>
    private static List<string> PinnedAppNames(List<string>? pins)
    {
        var names = new List<string>();
        foreach (var p in pins ?? new List<string>())
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            string n;
            try { n = Path.GetFileNameWithoutExtension(p.Trim()); }
            catch (ArgumentException) { continue; }
            if (n.Length > 0 && !names.Contains(n, StringComparer.OrdinalIgnoreCase)) names.Add(n);
        }
        return names;
    }
}
