using System.Collections.Immutable;
using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Http;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DinamikAda.Core.Abstractions;

namespace DinamikAda.Modules.Widgets;

public interface IIslandWidget
{
    string WidgetId { get; }
    string Name { get; }
    Task<WidgetRenderSnapshot> RefreshAsync(CancellationToken ct);
    Task<bool> ExecuteActionAsync(string actionId, CancellationToken ct);
}

public sealed class WidgetCircuitBreaker
{
    private readonly int _threshold;
    private readonly TimeSpan _cooldown;
    private readonly TimeProvider _timeProvider;
    private int _consecutiveFailures;
    private DateTimeOffset? _openedUntilUtc;

    public bool IsOpen
    {
        get
        {
            if (_openedUntilUtc is null) return false;
            return _timeProvider.GetUtcNow() < _openedUntilUtc.Value;
        }
    }

    public string? LastError { get; private set; }

    public WidgetCircuitBreaker(int threshold = 3, TimeSpan? cooldown = null, TimeProvider? timeProvider = null)
    {
        _threshold = Math.Max(1, threshold);
        _cooldown = cooldown ?? TimeSpan.FromSeconds(60);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public bool CanExecute()
    {
        if (_openedUntilUtc is null) return true;
        if (_timeProvider.GetUtcNow() >= _openedUntilUtc.Value)
        {
            _openedUntilUtc = null;
            _consecutiveFailures = 0;
            return true;
        }
        return false;
    }

    public void RecordSuccess()
    {
        _consecutiveFailures = 0;
        _openedUntilUtc = null;
        LastError = null;
    }

    public void RecordFailure(string error)
    {
        _consecutiveFailures++;
        LastError = error;
        if (_consecutiveFailures >= _threshold)
        {
            _openedUntilUtc = _timeProvider.GetUtcNow().Add(_cooldown);
        }
    }

    public void Reset()
    {
        _consecutiveFailures = 0;
        _openedUntilUtc = null;
        LastError = null;
    }
}

public sealed record DeclarativeWidgetAction(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("command")] string Command,
    [property: JsonPropertyName("args")] string? Args = null);

public sealed record DeclarativeWidgetDefinition(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("kind")] string Kind, // "cli" | "http" | "static"
    [property: JsonPropertyName("command")] string? Command = null,
    [property: JsonPropertyName("args")] string? Args = null,
    [property: JsonPropertyName("url")] string? Url = null,
    [property: JsonPropertyName("refreshSeconds")] int RefreshSeconds = 30,
    [property: JsonPropertyName("accentHex")] string AccentHex = "#0A84FF",
    [property: JsonPropertyName("title")] string? DefaultTitle = null,
    [property: JsonPropertyName("subtitle")] string? DefaultSubtitle = null,
    [property: JsonPropertyName("status")] string? DefaultStatus = null,
    [property: JsonPropertyName("actions")] List<DeclarativeWidgetAction>? Actions = null,
    [property: JsonPropertyName("showPillBadge")] bool ShowPillBadge = false,
    [property: JsonPropertyName("pillLabel")] string? PillLabel = null);

internal sealed class CollectiblePluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;

    public CollectiblePluginLoadContext(string pluginPath) : base(isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(pluginPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // Keep core abstraction contracts shared from host ALC!
        if (string.Equals(assemblyName.Name, "DinamikAda.Core", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(assemblyName.Name, "DinamikAda.Modules.Widgets", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string? resolved = _resolver.ResolveAssemblyToPath(assemblyName);
        return resolved is not null ? LoadShadowCopy(resolved) : null;
    }

    /// <summary>
    /// Loads an assembly from an in-memory copy instead of the file path. LoadFromAssemblyPath keeps
    /// the DLL locked, so users could not overwrite a plugin while the app runs and the
    /// FileSystemWatcher hot-reload never saw a new version.
    /// </summary>
    public Assembly LoadShadowCopy(string assemblyPath)
    {
        using var dll = new MemoryStream(File.ReadAllBytes(assemblyPath));
        string pdbPath = Path.ChangeExtension(assemblyPath, ".pdb");
        if (File.Exists(pdbPath))
        {
            try
            {
                using var pdb = new MemoryStream(File.ReadAllBytes(pdbPath));
                return LoadFromStream(dll, pdb);
            }
            catch (IOException)
            {
                dll.Position = 0;
            }
        }
        return LoadFromStream(dll);
    }
}

public sealed class WidgetHostService : IDisposable
{
    private sealed class WidgetSlot
    {
        public required string Id;
        public required string Name;
        public required WidgetCircuitBreaker Breaker;
        public required TimeSpan RefreshInterval;
        public DateTimeOffset NextRefreshUtc;
        public DeclarativeWidgetDefinition? JsonDef;
        public IIslandWidget? DllWidget;
        public CollectiblePluginLoadContext? Alc;
        public WidgetRenderSnapshot Snapshot = null!;
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly string _pluginsDir;
    private readonly TimeProvider _timeProvider;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private readonly object _sync = new();
    private readonly Dictionary<string, WidgetSlot> _slots = new(StringComparer.OrdinalIgnoreCase);

    private FileSystemWatcher? _watcher;
    private Timer? _scheduler;
    private CancellationTokenSource? _pipeCts;
    private CancellationTokenSource? _reloadDebounceCts;
    private int _isRefreshing;
    private volatile bool _pausedWhenCollapsed;
    private volatile bool _disposed;

    public string PluginsDirectory => _pluginsDir;
    public event EventHandler<IReadOnlyList<WidgetRenderSnapshot>>? WidgetsChanged;

    public void SetPausedWhenCollapsed(bool paused)
    {
        bool wasPaused = _pausedWhenCollapsed;
        _pausedWhenCollapsed = paused;
        if (wasPaused && !paused && !_disposed)
        {
            _ = RefreshDueWidgetsAsync();
        }
    }

    public WidgetHostService(string? pluginsDir = null, TimeProvider? timeProvider = null)
    {
        _pluginsDir = pluginsDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DinamikAda",
            "plugins");
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public IReadOnlyList<WidgetRenderSnapshot> GetSnapshots()
    {
        lock (_sync)
        {
            return _slots.Values.Select(s => s.Snapshot).OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    public void Start()
    {
        Directory.CreateDirectory(_pluginsDir);
        SeedStarterWidgetsIfEmpty();
        ReloadAllWidgets();

        _watcher = new FileSystemWatcher(_pluginsDir)
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
            EnableRaisingEvents = true
        };
        _watcher.Created += (_, _) => DebouncedReload();
        _watcher.Changed += (_, _) => DebouncedReload();
        _watcher.Deleted += (_, _) => DebouncedReload();
        _watcher.Renamed += (_, _) => DebouncedReload();

        _scheduler = new Timer(_ => _ = RefreshDueWidgetsAsync(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
        _pipeCts = new CancellationTokenSource();
        _ = RunNamedPipeServerAsync(_pipeCts.Token);
    }

    public async Task ExecuteActionAsync(string widgetId, string actionId, CancellationToken ct = default)
    {
        WidgetSlot? slot;
        IIslandWidget? dllWidget;
        DeclarativeWidgetDefinition? jsonDef;
        lock (_sync)
        {
            if (!_slots.TryGetValue(widgetId, out slot)) return;
            dllWidget = slot.DllWidget;
            jsonDef = slot.JsonDef;
        }

        try
        {
            if (dllWidget is not null)
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(4));
                await dllWidget.ExecuteActionAsync(actionId, timeoutCts.Token).WaitAsync(timeoutCts.Token);
            }
            else if (jsonDef?.Actions is not null)
            {
                var action = jsonDef.Actions.FirstOrDefault(a => string.Equals(a.Id, actionId, StringComparison.OrdinalIgnoreCase));
                if (action is not null)
                {
                    if (action.Command.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                        action.Command.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                    {
                        Process.Start(new ProcessStartInfo(action.Command) { UseShellExecute = true });
                    }
                    else
                    {
                        using var proc = Process.Start(new ProcessStartInfo(action.Command, action.Args ?? string.Empty)
                        {
                            UseShellExecute = false,
                            CreateNoWindow = true
                        });
                        if (proc is not null)
                        {
                            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                            timeoutCts.CancelAfter(TimeSpan.FromSeconds(4));
                            try
                            {
                                await proc.WaitForExitAsync(timeoutCts.Token);
                            }
                            catch (OperationCanceledException)
                            {
                                try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
                                throw;
                            }

                            if (proc.ExitCode != 0)
                            {
                                throw new InvalidOperationException($"Action exited with code {proc.ExitCode}");
                            }
                        }
                    }
                }
            }

            lock (_sync)
            {
                slot.NextRefreshUtc = DateTimeOffset.MinValue;
            }
            await RefreshSlotAsync(slot, ct);
            EmitChanged();
        }
        catch (Exception ex)
        {
            lock (_sync)
            {
                slot.Breaker.RecordFailure(ex.Message);
                slot.Snapshot = slot.Snapshot with { IsFaulted = slot.Breaker.IsOpen, FaultReason = ex.Message };
            }
            EmitChanged();
        }
    }

    public void ResetBreaker(string widgetId)
    {
        lock (_sync)
        {
            if (_slots.TryGetValue(widgetId, out var slot))
            {
                slot.Breaker.Reset();
                slot.NextRefreshUtc = DateTimeOffset.MinValue;
                slot.Snapshot = slot.Snapshot with { IsFaulted = false, FaultReason = null };
            }
        }
        EmitChanged();
    }

    private void DebouncedReload()
    {
        CancellationToken ct;
        lock (_sync)
        {
            if (_disposed) return;
            _reloadDebounceCts?.Cancel();
            _reloadDebounceCts?.Dispose();
            _reloadDebounceCts = new CancellationTokenSource();
            ct = _reloadDebounceCts.Token;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(350, ct).ConfigureAwait(false);
                if (!ct.IsCancellationRequested && !_disposed)
                {
                    ReloadAllWidgets();
                }
            }
            catch (OperationCanceledException) { }
        }, CancellationToken.None);
    }

    public void ReloadAllWidgets()
    {
        if (_disposed) return;
        var loadedSlots = new Dictionary<string, WidgetSlot>(StringComparer.OrdinalIgnoreCase);

        if (Directory.Exists(_pluginsDir))
        {
            foreach (string jsonPath in Directory.EnumerateFiles(_pluginsDir, "*.widget.json"))
            {
                try
                {
                    var def = JsonSerializer.Deserialize<DeclarativeWidgetDefinition>(File.ReadAllText(jsonPath), JsonOpts);
                    if (def is null || string.IsNullOrWhiteSpace(def.Id)) continue;

                    var actions = def.Actions?.Select(a => new WidgetActionDescriptor(a.Id, a.Label)).ToImmutableArray()
                        ?? ImmutableArray<WidgetActionDescriptor>.Empty;

                    string accent = string.IsNullOrWhiteSpace(def.AccentHex) ? "#0A84FF" : def.AccentHex;
                    loadedSlots[def.Id] = new WidgetSlot
                    {
                        Id = def.Id,
                        Name = string.IsNullOrWhiteSpace(def.Name) ? def.Id : def.Name,
                        Breaker = new WidgetCircuitBreaker(3, TimeSpan.FromSeconds(60), _timeProvider),
                        RefreshInterval = TimeSpan.FromSeconds(Math.Clamp(def.RefreshSeconds, 5, 3600)),
                        NextRefreshUtc = DateTimeOffset.MinValue,
                        JsonDef = def,
                        Snapshot = new WidgetRenderSnapshot(
                            def.Id,
                            def.Name,
                            def.DefaultTitle ?? def.Name,
                            def.DefaultSubtitle ?? "Hazır",
                            def.DefaultStatus ?? "Bekliyor",
                            accent,
                            null,
                            def.ShowPillBadge,
                            def.PillLabel,
                            false,
                            null,
                            actions)
                    };
                }
                catch (Exception ex)
                {
                    string badId = Path.GetFileNameWithoutExtension(jsonPath);
                    loadedSlots[badId] = CreateFaultedSlot(badId, badId, ex.Message);
                }
            }

            foreach (string dllPath in Directory.EnumerateFiles(_pluginsDir, "*.dll"))
            {
                CollectiblePluginLoadContext? alc = null;
                bool attachedToSlot = false;
                try
                {
                    alc = new CollectiblePluginLoadContext(dllPath);
                    var asm = alc.LoadShadowCopy(dllPath);
                    foreach (var type in asm.GetTypes().Where(t => !t.IsAbstract && typeof(IIslandWidget).IsAssignableFrom(t)))
                    {
                        if (Activator.CreateInstance(type) is not IIslandWidget widget) continue;
                        attachedToSlot = true;
                        loadedSlots[widget.WidgetId] = new WidgetSlot
                        {
                            Id = widget.WidgetId,
                            Name = widget.Name,
                            Breaker = new WidgetCircuitBreaker(3, TimeSpan.FromSeconds(60), _timeProvider),
                            RefreshInterval = TimeSpan.FromSeconds(15),
                            NextRefreshUtc = DateTimeOffset.MinValue,
                            DllWidget = widget,
                            Alc = alc,
                            Snapshot = new WidgetRenderSnapshot(
                                widget.WidgetId,
                                widget.Name,
                                widget.Name,
                                "C# Eklentisi",
                                "Yüklendi",
                                "#0A84FF",
                                null,
                                false,
                                null,
                                false,
                                null,
                                ImmutableArray<WidgetActionDescriptor>.Empty)
                        };
                    }
                }
                catch (Exception ex)
                {
                    string badId = Path.GetFileNameWithoutExtension(dllPath);
                    loadedSlots[badId] = CreateFaultedSlot(badId, badId, $"DLL yükleme hatası: {ex.Message}");
                }
                finally
                {
                    if (!attachedToSlot && alc is not null)
                    {
                        try { alc.Unload(); } catch { }
                    }
                }
            }
        }

        var replacedAlcs = new HashSet<CollectiblePluginLoadContext>();
        var replacedWidgets = new List<IIslandWidget>();
        lock (_sync)
        {
            var nextAlcs = loadedSlots.Values
                .Where(s => s.Alc is not null)
                .Select(s => s.Alc!)
                .ToHashSet();
            var nextWidgets = loadedSlots.Values
                .Where(s => s.DllWidget is not null)
                .Select(s => s.DllWidget!)
                .ToHashSet();

            // Preserve runtime snapshots updated via NamedPipe for pipe-injected widgets
            foreach (var kv in _slots)
            {
                if (kv.Key.StartsWith("pipe:", StringComparison.OrdinalIgnoreCase) && !loadedSlots.ContainsKey(kv.Key))
                {
                    loadedSlots[kv.Key] = kv.Value;
                }
            }

            foreach (var old in _slots.Values)
            {
                if (old.DllWidget is not null && !nextWidgets.Contains(old.DllWidget))
                {
                    replacedWidgets.Add(old.DllWidget);
                }
                if (old.Alc is not null && !nextAlcs.Contains(old.Alc))
                {
                    replacedAlcs.Add(old.Alc);
                }
            }

            _slots.Clear();
            foreach (var kv in loadedSlots) _slots[kv.Key] = kv.Value;
        }

        foreach (var widget in replacedWidgets)
        {
            DisposeWidgetInstance(widget);
        }

        foreach (var alc in replacedAlcs)
        {
            try { alc.Unload(); } catch { }
        }

        EmitChanged();
        _ = RefreshDueWidgetsAsync();
    }

    public async Task RefreshDueWidgetsAsync()
    {
        if (_disposed || Interlocked.CompareExchange(ref _isRefreshing, 1, 0) != 0) return;
        try
        {
            var now = _timeProvider.GetUtcNow();
            bool paused = _pausedWhenCollapsed;
            List<WidgetSlot> due;
            lock (_sync)
            {
                due = _slots.Values
                    .Where(s => (!paused || s.Snapshot.ShowPillBadge)
                                && (s.DllWidget is not null || (s.JsonDef is not null && !string.Equals(s.JsonDef.Kind, "static", StringComparison.OrdinalIgnoreCase)))
                                && s.NextRefreshUtc <= now)
                    .ToList();
            }

            bool anyUpdated = false;
            foreach (var slot in due)
            {
                if (_disposed) break;
                if (await RefreshSlotAsync(slot, CancellationToken.None))
                {
                    anyUpdated = true;
                }
            }

            if (anyUpdated) EmitChanged();
        }
        finally
        {
            Interlocked.Exchange(ref _isRefreshing, 0);
        }
    }

    private async Task<bool> RefreshSlotAsync(WidgetSlot slot, CancellationToken ct)
    {
        IIslandWidget? dllWidget;
        DeclarativeWidgetDefinition? jsonDef;
        WidgetRenderSnapshot prevSnapshot;
        TimeSpan refreshInterval;

        lock (_sync)
        {
            if (!slot.Breaker.CanExecute())
            {
                slot.Snapshot = slot.Snapshot with
                {
                    IsFaulted = true,
                    FaultReason = $"Devre kesici açık: {slot.Breaker.LastError ?? "Arka arkaya hata"}"
                };
                slot.NextRefreshUtc = _timeProvider.GetUtcNow().AddSeconds(15);
                return true;
            }
            dllWidget = slot.DllWidget;
            jsonDef = slot.JsonDef;
            prevSnapshot = slot.Snapshot;
            refreshInterval = slot.RefreshInterval;
        }

        try
        {
            if (dllWidget is not null)
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(4));
                var updated = await dllWidget.RefreshAsync(cts.Token).WaitAsync(cts.Token);
                lock (_sync)
                {
                    slot.Breaker.RecordSuccess();
                    slot.Snapshot = updated;
                    slot.NextRefreshUtc = _timeProvider.GetUtcNow().Add(refreshInterval);
                }
                return true;
            }

            if (jsonDef is not null)
            {
                var updated = await ExecuteDeclarativeRefreshAsync(jsonDef, prevSnapshot, ct);
                lock (_sync)
                {
                    slot.Breaker.RecordSuccess();
                    slot.Snapshot = updated;
                    slot.NextRefreshUtc = _timeProvider.GetUtcNow().Add(refreshInterval);
                }
                return true;
            }
        }
        catch (Exception ex)
        {
            lock (_sync)
            {
                slot.Breaker.RecordFailure(ex.Message);
                slot.Snapshot = slot.Snapshot with
                {
                    IsFaulted = slot.Breaker.IsOpen,
                    FaultReason = ex.Message
                };
                slot.NextRefreshUtc = _timeProvider.GetUtcNow().Add(refreshInterval);
            }
            return true;
        }

        return false;
    }

    private async Task<WidgetRenderSnapshot> ExecuteDeclarativeRefreshAsync(DeclarativeWidgetDefinition def, WidgetRenderSnapshot prev, CancellationToken ct)
    {
        string rawJson = string.Empty;

        if (string.Equals(def.Kind, "cli", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(def.Command))
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo(def.Command, def.Args ?? string.Empty)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8
                }
            };
            proc.Start();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(4));
            try
            {
                Task<string> stderrTask = proc.StandardError.ReadToEndAsync(timeoutCts.Token);
                rawJson = await proc.StandardOutput.ReadToEndAsync(timeoutCts.Token);
                await stderrTask;
                await proc.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    if (!proc.HasExited)
                        proc.Kill(entireProcessTree: true);
                }
                catch { }
                throw;
            }

            if (proc.ExitCode != 0)
            {
                throw new InvalidOperationException($"CLI çıkış kodu: {proc.ExitCode}");
            }
        }
        else if (string.Equals(def.Kind, "http", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(def.Url))
        {
            rawJson = await _http.GetStringAsync(def.Url, ct);
        }
        else
        {
            return prev;
        }

        using var doc = JsonDocument.Parse(rawJson);
        var root = doc.RootElement;
        string title = root.TryGetProperty("title", out var tEl) ? tEl.GetString() ?? prev.Title : prev.Title;
        string sub = root.TryGetProperty("subtitle", out var sEl) ? sEl.GetString() ?? prev.Subtitle : prev.Subtitle;
        string status = root.TryGetProperty("status", out var stEl) ? stEl.GetString() ?? prev.Status : prev.Status;
        string? rawAccent = root.TryGetProperty("accentHex", out var acEl) ? acEl.GetString() : null;
        string accent = string.IsNullOrWhiteSpace(rawAccent) ? prev.AccentHex : rawAccent;
        double? progress = root.TryGetProperty("progress", out var pEl) && pEl.TryGetDouble(out double pVal) ? pVal : prev.Progress0To100;
        bool pillBadge = root.TryGetProperty("showPillBadge", out var bEl)
            ? (bEl.ValueKind == JsonValueKind.True)
            : prev.ShowPillBadge;
        string? pillLabel = root.TryGetProperty("pillLabel", out var lEl) ? lEl.GetString() : prev.PillLabel;

        return prev with
        {
            Title = title,
            Subtitle = sub,
            Status = status,
            AccentHex = accent,
            Progress0To100 = progress is double value && double.IsFinite(value) ? Math.Clamp(value, 0, 100) : null,
            ShowPillBadge = pillBadge,
            PillLabel = pillLabel,
            IsFaulted = false,
            FaultReason = null
        };
    }

    private async Task RunNamedPipeServerAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    "DinamikAda.Widgets",
                    PipeDirection.In,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(ct);
                // A client that connects and never closes must not block every other widget push,
                // and payloads are bounded so a runaway writer can't exhaust memory.
                string? payload = await ReadBoundedPipePayloadAsync(server, ct);
                if (!string.IsNullOrWhiteSpace(payload))
                {
                    IngestNamedPipePayload(payload);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                await Task.Delay(300, ct).ConfigureAwait(false);
            }
        }
    }

    private const int MaxPipePayloadBytes = 64 * 1024;
    private const int MaxPipeWidgetSlots = 16;
    private static readonly TimeSpan PipeReadTimeout = TimeSpan.FromSeconds(5);

    private static async Task<string?> ReadBoundedPipePayloadAsync(Stream stream, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(PipeReadTimeout);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[8192];
        try
        {
            while (true)
            {
                int read = await stream.ReadAsync(chunk, timeout.Token).ConfigureAwait(false);
                if (read <= 0) break;
                if (buffer.Length + read > MaxPipePayloadBytes) return null;
                buffer.Write(chunk, 0, read);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null; // client stalled past the timeout
        }
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    public void IngestNamedPipePayload(string jsonPayload)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonPayload);
            var root = doc.RootElement;
            string rawId = root.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "external" : "external";
            string id = rawId.StartsWith("pipe:", StringComparison.OrdinalIgnoreCase) ? rawId : $"pipe:{rawId}";
            string name = root.TryGetProperty("name", out var nEl) ? nEl.GetString() ?? rawId : rawId;
            string title = root.TryGetProperty("title", out var tEl) ? tEl.GetString() ?? name : name;
            string sub = root.TryGetProperty("subtitle", out var sEl) ? sEl.GetString() ?? string.Empty : string.Empty;
            string status = root.TryGetProperty("status", out var stEl) ? stEl.GetString() ?? "Pipe" : "Pipe";
            string accent = root.TryGetProperty("accentHex", out var acEl) ? acEl.GetString() ?? "#30D158" : "#30D158";
            double? progress = root.TryGetProperty("progress", out var pEl) && pEl.TryGetDouble(out double pv) ? pv : null;
            bool badge = root.TryGetProperty("showPillBadge", out var bEl) && bEl.ValueKind == JsonValueKind.True;
            string? label = root.TryGetProperty("pillLabel", out var lEl) ? lEl.GetString() : null;

            var snap = new WidgetRenderSnapshot(
                id,
                name,
                title,
                sub,
                status,
                accent,
                progress,
                badge,
                label,
                false,
                null,
                ImmutableArray<WidgetActionDescriptor>.Empty);

            lock (_sync)
            {
                if (!_slots.ContainsKey(id) &&
                    _slots.Keys.Count(k => k.StartsWith("pipe:", StringComparison.OrdinalIgnoreCase)) >= MaxPipeWidgetSlots)
                {
                    return; // cap external widgets so arbitrary ids can't grow the slot table forever
                }
                _slots[id] = new WidgetSlot
                {
                    Id = id,
                    Name = name,
                    Breaker = new WidgetCircuitBreaker(3, TimeSpan.FromSeconds(60), _timeProvider),
                    RefreshInterval = TimeSpan.FromHours(24),
                    NextRefreshUtc = DateTimeOffset.MaxValue,
                    Snapshot = snap
                };
            }
            EmitChanged();
        }
        catch
        {
            // Ignore malformed external pipe message.
        }
    }

    private const string StarterUptimeArgs =
        "-NoProfile -Command \"[Console]::OutputEncoding=[System.Text.Encoding]::UTF8; $u=[TimeSpan]::FromSeconds([Diagnostics.Stopwatch]::GetTimestamp()/[Diagnostics.Stopwatch]::Frequency); @{ title='Açık Kalma'; subtitle=('{0}g {1}s {2}dk' -f $u.Days,$u.Hours,$u.Minutes); status='Aktif'; accentHex='#30D158' } | ConvertTo-Json -Compress\"";

    private void SeedStarterWidgetsIfEmpty()
    {
        string uptimePath = Path.Combine(_pluginsDir, "uptime.widget.json");
        if (File.Exists(uptimePath))
        {
            try
            {
                var existing = JsonSerializer.Deserialize<DeclarativeWidgetDefinition>(File.ReadAllText(uptimePath, Encoding.UTF8), JsonOpts);
                if (existing is not null &&
                    string.Equals(existing.Id, "starter.uptime", StringComparison.OrdinalIgnoreCase) &&
                    existing.Args is not null &&
                    (existing.Args.Contains("TickCount64", StringComparison.OrdinalIgnoreCase) ||
                     !existing.Args.Contains("OutputEncoding", StringComparison.Ordinal)))
                {
                    var migrated = existing with { Args = StarterUptimeArgs };
                    File.WriteAllText(uptimePath, JsonSerializer.Serialize(migrated, JsonOpts), new UTF8Encoding(false));
                }
            }
            catch
            {
                // Ignore malformed user widget customization cleanly.
            }
        }

        if (Directory.EnumerateFiles(_pluginsDir).Any()) return;

        var uptimeWidget = new DeclarativeWidgetDefinition(
            Id: "starter.uptime",
            Name: "Sistem Çalışma Süresi",
            Kind: "cli",
            Command: "powershell.exe",
            Args: StarterUptimeArgs,
            RefreshSeconds: 20,
            AccentHex: "#30D158",
            DefaultTitle: "Açık Kalma",
            DefaultSubtitle: "Ölçülüyor...",
            DefaultStatus: "Hazır",
            Actions: new List<DeclarativeWidgetAction>
            {
                new("refresh", "Yenile", "cmd.exe", "/c echo ok")
            });

        File.WriteAllText(uptimePath, JsonSerializer.Serialize(uptimeWidget, JsonOpts), new UTF8Encoding(false));
    }

    private WidgetSlot CreateFaultedSlot(string id, string name, string error) => new()
    {
        Id = id,
        Name = name,
        Breaker = new WidgetCircuitBreaker(3, TimeSpan.FromSeconds(60), _timeProvider),
        RefreshInterval = TimeSpan.FromSeconds(60),
        NextRefreshUtc = DateTimeOffset.MaxValue,
        Snapshot = new WidgetRenderSnapshot(
            id,
            name,
            name,
            "Eklenti Hatası",
            "Devre dışı",
            "#FF453A",
            null,
            false,
            null,
            true,
            error,
            ImmutableArray<WidgetActionDescriptor>.Empty)
    };

    private static void DisposeWidgetInstance(IIslandWidget? widget)
    {
        if (widget is null) return;
        try
        {
            if (widget is IDisposable d) d.Dispose();
            else if (widget is IAsyncDisposable ad) ad.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch { }
    }

    private void EmitChanged() => WidgetsChanged?.Invoke(this, GetSnapshots());

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CancellationTokenSource? reloadCts;
        var widgetsToDispose = new List<IIslandWidget>();
        var alcsToUnload = new HashSet<CollectiblePluginLoadContext>();

        lock (_sync)
        {
            reloadCts = _reloadDebounceCts;
            _reloadDebounceCts = null;
            foreach (var slot in _slots.Values)
            {
                if (slot.DllWidget is not null)
                {
                    widgetsToDispose.Add(slot.DllWidget);
                }
                if (slot.Alc is not null)
                {
                    alcsToUnload.Add(slot.Alc);
                }
            }
            _slots.Clear();
        }

        try { reloadCts?.Cancel(); } catch { }
        reloadCts?.Dispose();
        try { _pipeCts?.Cancel(); } catch { }
        _pipeCts?.Dispose();
        _scheduler?.Dispose();
        _watcher?.Dispose();
        _http.Dispose();

        foreach (var widget in widgetsToDispose)
        {
            DisposeWidgetInstance(widget);
        }

        foreach (var alc in alcsToUnload)
        {
            try { alc.Unload(); } catch { }
        }
    }
}
