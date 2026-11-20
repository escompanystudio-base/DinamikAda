using System.Windows.Threading;
using DinamikAda.Core.Voice;

namespace DinamikAda.App.Services.Voice;

/// <summary>
/// Runs scheduled voice tasks and daily routines. Checks every 20 s on the UI thread; all due-time math lives in
/// <see cref="ScheduleMath"/>. Tasks and routines are marked as run and saved before their actions start, so a crash
/// mid-run never repeats them.
/// </summary>
public sealed class VoiceScheduler : IDisposable
{
    private readonly AppOrchestrator _orch;
    private readonly DispatcherTimer _timer;
    private bool _running;
    private bool _disposed;

    public VoiceScheduler(AppOrchestrator orch)
    {
        _orch = orch;
        Executor = new VoiceActionExecutor(orch);
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(20) };
        _timer.Tick += (_, _) => Tick();
    }

    /// <summary>Runs the actions of commands, tasks and routines.</summary>
    public VoiceActionExecutor Executor { get; }

    private string T(string tr, string en) => _orch.Config.Language == "en" ? en : tr;

    public void Start()
    {
        if (_disposed || _orch.IsSmokeOrAuditMode) return;
        _timer.Start();
        // First check soon after startup, so a routine due while the app was closed is caught within its window.
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, Tick);
    }

    /// <summary>Stores new one-off tasks.</summary>
    public void AddTasks(IReadOnlyCollection<ScheduledVoiceTask> tasks)
    {
        if (tasks.Count == 0 || _orch.IsSmokeOrAuditMode) return;
        _orch.ConfigStore.Update(_orch.Config, c => c.ScheduledVoiceTasks =
            (c.ScheduledVoiceTasks ?? new()).Concat(tasks.Select(Clone)).ToList());
    }

    /// <summary>Stores new routines.</summary>
    public void AddRoutines(IReadOnlyCollection<VoiceRoutine> routines)
    {
        if (routines.Count == 0 || _orch.IsSmokeOrAuditMode) return;
        _orch.ConfigStore.Update(_orch.Config, c => c.VoiceRoutines =
            (c.VoiceRoutines ?? new()).Concat(routines.Select(Clone)).ToList());
    }

    /// <summary>Removes a routine by id; false when it was not there.</summary>
    public bool DeleteRoutine(string id)
    {
        if (_orch.IsSmokeOrAuditMode || !(_orch.Config.VoiceRoutines?.Any(r => r.Id == id) ?? false)) return false;
        _orch.ConfigStore.Update(_orch.Config, c => c.VoiceRoutines =
            (c.VoiceRoutines ?? new()).Where(r => r.Id != id).ToList());
        return true;
    }

    private async void Tick()
    {
        if (_disposed || _running || _orch.IsSmokeOrAuditMode) return;
        _running = true;
        try
        {
            await RunDueAsync(DateTime.Now);
        }
        catch (Exception ex)
        {
            CrashLogger.Log("Voice", ex, "Voice scheduler tick failed.");
        }
        finally
        {
            _running = false;
        }
    }

    private async Task RunDueAsync(DateTime now)
    {
        var cfg = _orch.Config;
        var tasks = (cfg.ScheduledVoiceTasks ?? new()).ToList();
        var routines = (cfg.VoiceRoutines ?? new()).ToList();

        var dueTasks = new List<ScheduledVoiceTask>();
        var finished = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in tasks)
        {
            switch (ScheduleMath.CheckTask(t, now))
            {
                case TaskDue.Run:
                    dueTasks.Add(t);
                    finished.Add(t.Id);
                    break;
                case TaskDue.Stale:
                    finished.Add(t.Id);
                    break;
            }
        }

        var dueRoutines = new List<VoiceRoutine>();
        var ranOn = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var r in routines)
        {
            if (ScheduleMath.DueOccurrence(r, now) is { } at && !ranOn.ContainsKey(r.Id))
            {
                dueRoutines.Add(r);
                ranOn[r.Id] = ScheduleMath.DateKey(at);
            }
        }

        if (finished.Count == 0 && ranOn.Count == 0) return;

        // Persist first, with new lists and new objects (the config copy shares references).
        _orch.ConfigStore.Update(cfg, c =>
        {
            if (finished.Count > 0)
            {
                c.ScheduledVoiceTasks = (c.ScheduledVoiceTasks ?? new())
                    .Select(t => finished.Contains(t.Id) ? Clone(t, done: true) : t).ToList();
            }
            if (ranOn.Count > 0)
            {
                c.VoiceRoutines = (c.VoiceRoutines ?? new())
                    .Select(r => ranOn.TryGetValue(r.Id, out var key) ? Clone(r, key) : r).ToList();
            }
        });

        foreach (var t in dueTasks)
        {
            var results = await Executor.ExecuteAllAsync(t.Actions);
            // A reminder already shows its own notification.
            if (t.Actions.All(a => a.Kind == VoiceActionKind.Notify)) continue;
            Executor.Notify(T("Planlanan görev çalıştı", "Scheduled task ran"), Body(t.Title, results));
        }
        foreach (var r in dueRoutines)
        {
            var results = await Executor.ExecuteAllAsync(r.Actions);
            string name = string.IsNullOrWhiteSpace(r.Name) ? T("Rutin", "Routine") : r.Name;
            Executor.Notify(T("Rutin çalıştı", "Routine ran"), Body(name, results));
        }
    }

    private string Body(string title, List<VoiceActionResult> results)
    {
        var failed = results.Where(x => !x.Ok).Select(x => x.Text).ToList();
        return failed.Count == 0 ? title : title + " · " + string.Join(" ", failed);
    }

    private static ScheduledVoiceTask Clone(ScheduledVoiceTask t) => Clone(t, t.Done);

    private static ScheduledVoiceTask Clone(ScheduledVoiceTask t, bool done) => new()
    {
        Id = t.Id,
        Title = t.Title,
        RunAtLocal = t.RunAtLocal,
        Actions = t.Actions.Select(a => a.Clone()).ToList(),
        Done = done
    };

    private static VoiceRoutine Clone(VoiceRoutine r) => Clone(r, r.LastRunDate);

    private static VoiceRoutine Clone(VoiceRoutine r, string lastRunDate) => new()
    {
        Id = r.Id,
        Name = r.Name,
        TimeOfDay = r.TimeOfDay,
        DaysMask = r.DaysMask,
        Actions = r.Actions.Select(a => a.Clone()).ToList(),
        Enabled = r.Enabled,
        LastRunDate = lastRunDate
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
    }
}
