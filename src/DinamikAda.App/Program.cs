using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DinamikAda.App.Services;
using DinamikAda.App.Views;
using DinamikAda.Core.Abstractions;
using DinamikAda.Core.Arbitration;
using DinamikAda.Core.Config;
using ActivityKind = DinamikAda.Core.Abstractions.ActivityKind;
using DinamikAda.Modules.CommandHub;
using DinamikAda.Modules.Media;
using DinamikAda.Modules.Notifications;
using DinamikAda.Modules.Productivity;
using DinamikAda.Modules.SystemControl;
using DinamikAda.Modules.Telemetry;
using DinamikAda.Modules.Widgets;
using DinamikAda.Platform.Win32.Hotkeys;
using DinamikAda.Platform.Win32.Interop;
using DinamikAda.Platform.Win32.Windowing;
using Microsoft.Extensions.DependencyInjection;

namespace DinamikAda.App;

public static class Program
{
    private const string MutexName = "Local\\DinamikAda_SingleInstance_v1";

    [STAThread]
    public static int Main(string[] args)
    {
        bool smokeTest = args.Any(a => string.Equals(a, "--smoke-test", StringComparison.OrdinalIgnoreCase));
        bool traceMode = args.Any(a => string.Equals(a, "--trace", StringComparison.OrdinalIgnoreCase));
        bool forceSoftwareRender = args.Any(a => string.Equals(a, "--sw", StringComparison.OrdinalIgnoreCase) || string.Equals(a, "--software", StringComparison.OrdinalIgnoreCase));
        int smokeDurationSec = ParseDurationSec(args, defaultSeconds: 10);
        string? renderAuditDir = ParseRenderAuditDir(args);

        string? isolatedConfigDir = renderAuditDir is not null || smokeTest
            ? Path.Combine(Path.GetTempPath(), $"dinamikada_audit_{Guid.NewGuid():N}")
            : null;

        var configStore = isolatedConfigDir is not null
            ? new AppConfigStore(Path.Combine(isolatedConfigDir, "config.json"))
            : new AppConfigStore();
        var initialConfig = configStore.Load();
        if (isolatedConfigDir is null && args.Contains("--explorer-taskbar", StringComparer.OrdinalIgnoreCase))
        {
            initialConfig.UseExplorerTaskbar = true;
            configStore.Save(initialConfig);
        }
        if (isolatedConfigDir is null && args.Contains("--custom-dock", StringComparer.OrdinalIgnoreCase))
        {
            initialConfig.UseExplorerTaskbar = false;
            initialConfig.EnableBottomDock = true;
            configStore.Save(initialConfig);
        }
        if (isolatedConfigDir is not null)
        {
            initialConfig.HideWindowsTaskbar = false;
            initialConfig.EnableBottomDock = false;
            initialConfig.UseExplorerTaskbar = false;
            if (smokeTest && renderAuditDir is null)
            {
                initialConfig.EnableSpectrumVisualizer = false;
                initialConfig.DynamicAlbumGlowEnabled = false;
            }
        }
        if (renderAuditDir is not null)
        {
            initialConfig.DockPlacement = DockPlacementMode.NotchFlush;
            initialConfig.NotchScalePreset = NotchScalePreset.Standard;
            initialConfig.DormantCameraLensEnabled = true;
            initialConfig.DynamicAlbumGlowEnabled = true;
            initialConfig.EnableSpectrumVisualizer = true;
            initialConfig.ShowTelemetryInPill = true;
            initialConfig.Sanitize();
        }

        bool useSoftwareRender = forceSoftwareRender
            || !initialConfig.UseHardwareRendering
            || (initialConfig.AmdSafeSoftwareCompositing && DinamikAda.Platform.Win32.Interop.NativeMethods.HasAmdDisplayAdapter());
        RenderOptions.ProcessRenderMode = useSoftwareRender
            ? RenderMode.SoftwareOnly
            : RenderMode.Default;

        Mutex? singleMutex = null;
        if (!smokeTest && renderAuditDir is null)
        {
            singleMutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
            if (!createdNew)
            {
                singleMutex.Dispose();
                return 0;
            }
        }

        ServiceProvider? provider = null;
        try
        {
            var services = ConfigureServices(configStore, initialConfig, isolated: isolatedConfigDir is not null);
            provider = services.BuildServiceProvider();

            var app = new System.Windows.Application
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown
            };

            if (renderAuditDir is not null)
            {
                return RunRenderAudit(provider, renderAuditDir);
            }

            if (smokeTest)
            {
                return RunSmokeTest(app, provider, smokeDurationSec, traceMode);
            }

            var orch = provider.GetRequiredService<AppOrchestrator>();
            var taskbarManager = provider.GetRequiredService<WindowsTaskbarManager>();
            // Undo a taskbar hide left behind by a crash or forced kill of the previous session.
            if (taskbarManager.RecoverFromPreviousSession())
            {
                CrashLogger.Log("startup", null, "Restored Windows taskbar state left hidden by previous session.");
            }
            InstallGlobalExceptionHandlers(app, orch, taskbarManager);
            var window = provider.GetRequiredService<IslandWindow>();
            app.MainWindow = window;
            app.ShutdownMode = ShutdownMode.OnMainWindowClose;
            window.Show();
            orch.StartMessageLoopHooks();

            EventHandler onProcessExit = (_, _) => taskbarManager.Restore();
            AppDomain.CurrentDomain.ProcessExit += onProcessExit;
            app.SessionEnding += (_, _) => taskbarManager.Restore();

            _ = Task.Run(async () =>
            {
                try
                {
                    await orch.StartAsync();
                }
                catch (Exception ex)
                {
                    CrashLogger.Log("startup", ex, "Orchestrator start failed.");
                    orch.Notifications.AddInternalAlert("DinamikAda", "Başlatma uyarısı", ex.Message);
                }
            });

            app.Exit += (_, _) =>
            {
                AppDomain.CurrentDomain.ProcessExit -= onProcessExit;
                taskbarManager.Restore();
                NativeMethods.SetThreadExecutionState(NativeMethods.ES_CONTINUOUS);
                try
                {
                    Task.WhenAny(Task.Run(async () => await orch.DisposeAsync()), Task.Delay(1200)).GetAwaiter().GetResult();
                }
                catch
                {
                    // Never deadlock UI exit on third-party COM/WinRT teardown.
                }
            };

            return app.Run();
        }
        finally
        {
            if (provider is not null)
            {
                try { provider.DisposeAsync().AsTask().Wait(1500); } catch { }
            }
            NativeMethods.SetThreadExecutionState(NativeMethods.ES_CONTINUOUS);
            if (singleMutex is not null)
            {
                try { singleMutex.ReleaseMutex(); } catch { }
                singleMutex.Dispose();
            }
            if (isolatedConfigDir is not null)
            {
                try { Directory.Delete(isolatedConfigDir, recursive: true); } catch { }
            }
        }
    }

    private static void InstallGlobalExceptionHandlers(
        System.Windows.Application app,
        AppOrchestrator orch,
        WindowsTaskbarManager taskbarManager)
    {
        long lastAlertTicks = 0;

        app.DispatcherUnhandledException += (_, e) =>
        {
            CrashLogger.Log("ui", e.Exception, "Unhandled UI-thread exception (recovered).");
            // Keep the shell alive: a single faulty handler must not kill the island and leave the
            // Windows taskbar hidden. Throttle user-facing alerts to one per 30 s.
            e.Handled = true;
            long now = Environment.TickCount64;
            if (now - Interlocked.Read(ref lastAlertTicks) > 30_000)
            {
                Interlocked.Exchange(ref lastAlertTicks, now);
                try
                {
                    orch.Notifications.AddInternalAlert(
                        "DinamikAda",
                        "Beklenmeyen hata",
                        $"{e.Exception.GetType().Name}: {e.Exception.Message}");
                }
                catch
                {
                }
            }
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            CrashLogger.Log("fatal", e.ExceptionObject as Exception, $"Unhandled exception (terminating={e.IsTerminating}).");
            if (e.IsTerminating)
            {
                try { taskbarManager.Restore(); } catch { }
                try { NativeMethods.SetThreadExecutionState(NativeMethods.ES_CONTINUOUS); } catch { }
            }
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            CrashLogger.Log("task", e.Exception, "Unobserved task exception.");
            e.SetObserved();
        };
    }

    private static ServiceCollection ConfigureServices(AppConfigStore configStore, AppConfig initialConfig, bool isolated)
    {
        var services = new ServiceCollection();

        services.AddSingleton(configStore);
        services.AddSingleton(initialConfig);
        services.AddSingleton<TimeProvider>(TimeProvider.System);

        services.AddSingleton(sp =>
            new IslandCoordinator(sp.GetRequiredService<AppConfig>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<ITransientHudBus>(sp => sp.GetRequiredService<IslandCoordinator>());
        services.AddSingleton<IIslandHostContext>(sp => sp.GetRequiredService<IslandCoordinator>());

        services.AddSingleton<IMonitorLayoutService, Win32MonitorLayoutService>();
        services.AddSingleton<IFullscreenDetector, FullscreenDetector>();
        services.AddSingleton<IslandWindowController>();
        services.AddSingleton<GlobalHotkeyService>();

        services.AddSingleton<GsmtcMediaService>();
        services.AddSingleton<IMediaControlBackend>(sp => sp.GetRequiredService<GsmtcMediaService>());
        services.AddSingleton<CoreAudioHost>();
        services.AddSingleton<IAudioBackend>(sp => sp.GetRequiredService<CoreAudioHost>());

        services.AddSingleton<BrightnessService>();
        services.AddSingleton<IBrightnessBackend>(sp => sp.GetRequiredService<BrightnessService>());
        services.AddSingleton<ConnectivityAndSystemService>();
        services.AddSingleton<IConnectivityBackend>(sp => sp.GetRequiredService<ConnectivityAndSystemService>());
        services.AddSingleton<UsbDriveMonitorService>();
        services.AddSingleton<PrivacySensorService>();

        services.AddSingleton<NotificationHubService>();
        services.AddSingleton<INotificationBackend>(sp => sp.GetRequiredService<NotificationHubService>());

        services.AddSingleton<FileShelfService>();
        services.AddSingleton(sp => new ClipboardVaultService(maxItems: sp.GetRequiredService<AppConfig>().MaxClipboardItems));
        services.AddSingleton(sp => new FocusTimerService(sp.GetRequiredService<ITransientHudBus>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<LanDropServer>();
        services.AddSingleton<ScreenOcrService>();
        services.AddSingleton<MeetingCalendarService>();
        services.AddSingleton<TransferMonitorService>();
        services.AddSingleton<LyricsService>();
        services.AddSingleton<VoiceNoteService>();
        services.AddSingleton<WindowDesktopControlService>();
        services.AddSingleton<DeveloperBridgeService>();
        services.AddSingleton<AiQuotaTrackerService>(sp =>
        {
            var cfg = sp.GetRequiredService<AppConfig>();
            var store = sp.GetRequiredService<AppConfigStore>();
            return new AiQuotaTrackerService(
                cfg,
                () => store.Save(cfg),
                sp.GetRequiredService<ITransientHudBus>(),
                sp.GetRequiredService<TimeProvider>());
        });

        services.AddSingleton<HungProcessService>();
        services.AddSingleton<SystemTelemetryService>();
        services.AddSingleton<SystemMaintenanceService>();
        services.AddSingleton<WidgetHostService>();

        services.AddSingleton<IEnumerable<ICommandProvider>>(Array.Empty<ICommandProvider>());
        services.AddSingleton<LauncherService>(sp =>
        {
            var coord = sp.GetRequiredService<IslandCoordinator>();
            return new LauncherService(
                sp.GetRequiredService<ClipboardVaultService>(),
                sp.GetRequiredService<IEnumerable<ICommandProvider>>(),
                tab => coord.RequestNavigateTab(tab),
                key => sp.GetRequiredService<AppOrchestrator>().RunBuiltinActionAsync(key));
        });

        services.AddSingleton<DimmerOverlayManager>();
        // Smoke/render-audit runs must never read or delete the real session's crash-recovery marker.
        services.AddSingleton(_ => isolated ? new WindowsTaskbarManager(recoveryStatePath: null) : new WindowsTaskbarManager());
        services.AddSingleton<DockWindow>();
        services.AddSingleton<AppOrchestrator>();
        services.AddSingleton<IslandWindow>();

        return services;
    }

    /// <summary>
    /// Exercises Dock pin editing (drop-to-pin, folder pins, reorder, offline-target retention and
    /// Settings reload detection) against an isolated config and temp files.
    /// </summary>
    private static void SmokeCheckDockPinEditing(List<string> failures)
    {
        string dir = Path.Combine(Path.GetTempPath(), "DinamikAda-smoke-pins-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            string alpha = Path.GetFullPath(Path.Combine(dir, "alpha.txt"));
            string beta = Path.GetFullPath(Path.Combine(dir, "beta.txt"));
            string gamma = Path.GetFullPath(Path.Combine(dir, "Gamma"));
            string offline = Path.Combine(dir, "offline", "delta.exe");
            File.WriteAllText(alpha, "a");
            File.WriteAllText(beta, "b");
            Directory.CreateDirectory(gamma);

            var cfg = new AppConfig { DockPinsCustomized = true, PinnedDockApps = new List<string> { offline } };
            int saves = 0;
            var svc = new DockAppsService(cfg, () => saves++);

            bool pinnedAll = svc.PinPath(alpha) & svc.PinPath(beta) & svc.PinPath(gamma, insertIndex: 0);
            bool duplicateRejected = !svc.PinPath(alpha);
            var afterPin = cfg.PinnedDockApps.ToList();
            string[] expectedPin = { gamma, alpha, beta, offline };
            if (!pinnedAll || !duplicateRejected || !afterPin.SequenceEqual(expectedPin, StringComparer.OrdinalIgnoreCase))
            {
                failures.Add($"Dock PinPath produced [{string.Join(", ", afterPin)}], expected [{string.Join(", ", expectedPin)}].");
            }

            bool moved = svc.MovePinned(beta, 0);
            string[] expectedMove = { beta, gamma, alpha, offline };
            if (!moved || !cfg.PinnedDockApps.SequenceEqual(expectedMove, StringComparer.OrdinalIgnoreCase))
            {
                failures.Add($"Dock MovePinned produced [{string.Join(", ", cfg.PinnedDockApps)}], expected [{string.Join(", ", expectedMove)}].");
            }

            var effective = DockAppsService.ResolveEffectivePinnedTargets(cfg);
            if (!effective.SequenceEqual(new[] { beta, gamma, alpha }, StringComparer.OrdinalIgnoreCase))
            {
                failures.Add($"Dock effective pins were [{string.Join(", ", effective)}].");
            }

            if (svc.ReloadIfConfigChanged())
            {
                failures.Add("Dock reloaded pins although only the Dock itself changed them.");
            }
            cfg.PinnedDockApps = new List<string> { alpha };
            if (!svc.ReloadIfConfigChanged())
            {
                failures.Add("Dock did not pick up a pin list rewritten by Settings.");
            }

            if (saves < 4)
            {
                failures.Add($"Dock pin edits saved config {saves} times, expected at least 4.");
            }
        }
        catch (Exception ex)
        {
            failures.Add($"Dock pin editing check threw: {ex.Message}");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    private static int RunSmokeTest(System.Windows.Application app, ServiceProvider provider, int durationSec, bool traceMode = false)
    {
        int exitCode = 0;
        var orch = provider.GetRequiredService<AppOrchestrator>();
        orch.IsSmokeOrAuditMode = true;
        var window = provider.GetRequiredService<IslandWindow>();
        if (traceMode)
        {
            orch.Coordinator.SnapshotChanged += (_, snap) =>
                Console.WriteLine($"[TRACE {DateTime.UtcNow:HH:mm:ss.fff}] State={snap.State} Tab={snap.ActiveTab} Prim={snap.PrimaryActivity?.SourceId} FS={snap.Fullscreen.IsFullscreen}:{snap.Fullscreen.ProcessName} Expanded={snap.State}");
        }
        window.SetSmokeMode(true);
        app.MainWindow = window;
        window.Show();
        orch.StartMessageLoopHooks();

        var failures = new List<string>();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        bool sequenceExecuted = false;
        bool sequenceFinished = false;
        bool finished = false;
        DateTime startedUtc = DateTime.UtcNow;
        TimeSpan idleSampleStartCpu = TimeSpan.Zero;
        DateTime idleSampleStartWall = DateTime.MinValue;
        double measuredAnimFps = 0.0;
        int displayHz = 60;

        _ = Task.Run(async () =>
        {
            try
            {
                await orch.StartAsync();
            }
            catch (Exception ex)
            {
                failures.Add($"Orchestrator StartAsync failed: {ex.Message}");
            }
        });

        timer.Tick += async (_, _) =>
        {
            var elapsed = (DateTime.UtcNow - startedUtc).TotalSeconds;
            if (!sequenceExecuted && elapsed >= 1.5)
            {
                sequenceExecuted = true;
                try
                {
                    if (!window.WindowController.IsPerMonitorV2Active())
                        failures.Add("PerMonitorV2 DPI awareness is not active.");

                    long exStyle = window.WindowController.ReadCurrentExtendedStyle();
                    if ((exStyle & NativeMethods.WS_EX_TOOLWINDOW) == 0 ||
                        (exStyle & NativeMethods.WS_EX_NOACTIVATE) == 0 ||
                        (exStyle & NativeMethods.WS_EX_TOPMOST) == 0)
                    {
                        failures.Add($"Unexpected WS_EX style: 0x{exStyle:X}");
                    }

                    // Inspect the live tray without activating or closing another application.
                    var trayService = new DockAppsService(new AppConfig(), () => { });
                    var trayApps = await Task.Run(trayService.QueryBackgroundTrayApps);
                    if (trayApps.Select(item => item.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count() != trayApps.Count)
                        failures.Add("Background tray returned duplicate application entries.");
                    if (trayApps.Any(item => item.ProcessId == Environment.ProcessId ||
                        item.ProcessStartedUtcTicks <= 0 || item.Icon is { IsFrozen: false }))
                        failures.Add("Background tray returned an invalid process identity or an unfrozen icon.");
                    var staleTrayItem = new BackgroundTrayAppItem("smoke:stale", "Stale app", "missing.exe", null, uint.MaxValue, 1, 0, null);
                    if (await DockAppsService.ActivateBackgroundTrayAppAsync(staleTrayItem) != TrayAppActionResult.Stale ||
                        DockAppsService.RequestCloseTrayWindow(staleTrayItem) != TrayAppActionResult.Stale)
                        failures.Add("A stale background tray entry was accepted for activation or closing.");

                    // Temporarily isolate synthetic state-machine self-test from any fullscreen app behind the test terminal.
                    var savedGamePolicy = orch.Config.GameFullscreenPolicy;
                    var savedVideoPolicy = orch.Config.VideoFullscreenPolicy;
                    var savedProdPolicy = orch.Config.ProductivityFullscreenPolicy;
                    orch.Config.GameFullscreenPolicy = FullscreenPolicy.KeepCompact;
                    orch.Config.VideoFullscreenPolicy = FullscreenPolicy.KeepCompact;
                    orch.Config.ProductivityFullscreenPolicy = FullscreenPolicy.KeepCompact;
                    orch.Coordinator.Recompute();

                    try
                    {
                        // Exercise S1 -> S2 -> S2Split -> S3 -> S4 -> S5 -> S6 -> S1
                        orch.Coordinator.RequestCollapse(forceUnpin: true);
                        orch.Coordinator.PublishLiveActivity(new LiveActivityState("smoke", ActivityKind.Media, 1000, "Smoke Track", "Smoke Artist"), "smoke");
                        PumpFrames();
                        if (orch.Coordinator.Current.State is not (IslandPresentationState.S2Compact or IslandPresentationState.S2Split))
                            failures.Add($"Expected S2Compact/S2Split, got {orch.Coordinator.Current.State}");

                        orch.Coordinator.PublishLiveActivity(new LiveActivityState("smoke:sat", ActivityKind.CustomWidget, 900, "AI 2/3", TargetTab: ExpandedTabId.AiQuota), "smoke:sat");
                        PumpFrames();
                        if (orch.Coordinator.Current.State != IslandPresentationState.S2Split)
                            failures.Add($"Expected S2Split with dual activity, got {orch.Coordinator.Current.State}");
                        await Task.Delay(350);
                        if (!window.IsSatelliteInteractive)
                            failures.Add("AI satellite was not interactive after the split reveal.");
                        orch.Coordinator.RequestExpand(ExpandedTabId.AiQuota);
                        PumpFrames();
                        if (window.IsSatelliteInteractive)
                            failures.Add("Outgoing AI satellite still intercepted input during expansion.");
                        await Task.Delay(40);
                        orch.Coordinator.RequestCollapse(forceUnpin: true);
                        await Task.Delay(350);
                        if (!window.IsSatelliteInteractive)
                            failures.Add("AI satellite did not recover after a rapid open-close transition.");
                        orch.Coordinator.PublishLiveActivity(null, "smoke:sat");

                        orch.Coordinator.Publish(new TransientHudEvent("smoke:hud", HudKind.Volume, "Smoke Volume", "50%", 0.5, DurationMs: 500));
                        PumpFrames();
                        if (orch.Coordinator.Current.State != IslandPresentationState.S3TransientHud)
                            failures.Add($"Expected S3TransientHud, got {orch.Coordinator.Current.State}");

                        orch.Coordinator.DismissTransientHud();
                        orch.Coordinator.RequestExpand(ExpandedTabId.Audio);
                        PumpFrames();
                        if (orch.Coordinator.Current.State != IslandPresentationState.S4Expanded)
                            failures.Add($"Expected S4Expanded, got {orch.Coordinator.Current.State}");

                        orch.Coordinator.OpenCommandPalette();
                        PumpFrames();
                        if (orch.Coordinator.Current.State != IslandPresentationState.S5Command)
                            failures.Add($"Expected S5Command, got {orch.Coordinator.Current.State}");
                        var cmdResults = await orch.Launcher.QueryAsync("= 21*2", CancellationToken.None);
                        if (!cmdResults.Any(r => r.CopyText == "42"))
                            failures.Add("CommandHub quick calculator did not return 42 for '= 21*2'.");
                        orch.Coordinator.CloseCommandPalette();

                        orch.Coordinator.SetDragShelfActive(true);
                        PumpFrames();
                        if (orch.Coordinator.Current.State != IslandPresentationState.S6DragShelf)
                            failures.Add($"Expected S6DragShelf, got {orch.Coordinator.Current.State}");
                        orch.Coordinator.SetDragShelfActive(false);

                        SmokeCheckDockPinEditing(failures);
                        var widgetDock = new DockWindow(orch, provider.GetRequiredService<WindowsTaskbarManager>());
                        failures.AddRange(widgetDock.AuditBuildAllWidgets());

                        orch.Coordinator.RequestCollapse(forceUnpin: true);
                        orch.Coordinator.PublishLiveActivity(null, "smoke");
                        PumpFrames();

                        // Measure real active spring animation pacing against display VSync cadence
                        (measuredAnimFps, displayHz) = await window.MeasureAnimationBurstAsync(420);
                    }
                    finally
                    {
                        orch.Config.GameFullscreenPolicy = savedGamePolicy;
                        orch.Config.VideoFullscreenPolicy = savedVideoPolicy;
                        orch.Config.ProductivityFullscreenPolicy = savedProdPolicy;
                        orch.Coordinator.Recompute();
                    }

                    // Verify CoreAudio enumerated endpoints cleanly (or fallback gracefully without crashing)
                    var audioSnap = orch.Audio.Current;
                    if (audioSnap is null) failures.Add("CoreAudio snapshot was null.");

                    // Wait for spring physics to fully settle before measuring idle CPU baseline
                    await Task.Delay(950);
                    PumpFrames();
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                    NativeMethods.SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, new nint(-1), new nint(-1));
                    var proc = Process.GetCurrentProcess();
                    proc.Refresh();
                    idleSampleStartCpu = proc.TotalProcessorTime;
                    idleSampleStartWall = DateTime.UtcNow;
                }
                catch (Exception ex)
                {
                    failures.Add($"Sequence exception: {ex.Message}");
                }
                finally
                {
                    sequenceFinished = true;
                }
            }

            if (sequenceFinished && !finished && elapsed >= durationSec)
            {
                finished = true;
                timer.Stop();

                var proc = Process.GetCurrentProcess();
                proc.Refresh();
                double wsMb = proc.WorkingSet64 / (1024.0 * 1024.0);
                double privMb = proc.PrivateMemorySize64 / (1024.0 * 1024.0);

                double idleCpuPct = 0.0;
                if (idleSampleStartWall != DateTime.MinValue)
                {
                    double wallSec = Math.Max(0.2, (DateTime.UtcNow - idleSampleStartWall).TotalSeconds);
                    double cpuSec = (proc.TotalProcessorTime - idleSampleStartCpu).TotalSeconds;
                    idleCpuPct = (cpuSec / (wallSec * Environment.ProcessorCount)) * 100.0;
                }

                bool webViewLoaded = Process.GetProcessesByName("msedgewebview2").Any(p => p.SessionId == proc.SessionId && p.StartTime >= startedUtc.ToLocalTime());

                double maxPrivateMb = RenderOptions.ProcessRenderMode == RenderMode.SoftwareOnly ? 80.0 : 165.0;
                if (wsMb >= 80.0) failures.Add($"WorkingSet64 exceeded budget: {wsMb:F2} MB >= 80 MB");
                if (privMb >= maxPrivateMb) failures.Add($"PrivateMemorySize64 exceeded budget: {privMb:F2} MB >= {maxPrivateMb:F0} MB");
                if (idleCpuPct >= 0.5) failures.Add($"Idle CPU exceeded 0.5%: {idleCpuPct:F3}%");
                if (webViewLoaded) failures.Add("Unexpected msedgewebview2 process spawned.");

                Console.WriteLine(
                    $"SMOKE_METRICS: WorkingSetMB={wsMb:F2}; PrivateMB={privMb:F2}; IdleCpuPct={idleCpuPct:F3}; " +
                    $"MeasuredAnimFps={measuredAnimFps:F1}; DisplayHz={displayHz}; RenderMode={RenderOptions.ProcessRenderMode}; " +
                    $"PerMonitorV2={window.WindowController.IsPerMonitorV2Active()}; ExStyle=0x{window.WindowController.ReadCurrentExtendedStyle():X}; " +
                    $"WebView2={webViewLoaded}; State={orch.Coordinator.Current.State}; RenderLoop={window.IsRenderingLoopActive}; " +
                    $"Prim={orch.Coordinator.Current.PrimaryActivity?.SourceId}; Sat={orch.Coordinator.Current.SatelliteActivity?.SourceId}; " +
                    $"Playing={orch.Media.Current.IsPlaying}; Loopback={orch.Audio.Current.IsLoopbackActive}");

                if (failures.Count > 0)
                {
                    foreach (var f in failures) Console.Error.WriteLine($"SMOKE_FAIL: {f}");
                    exitCode = 1;
                }
                else
                {
                    Console.WriteLine("SMOKE_PASS: All runtime invariants satisfied.");
                    exitCode = 0;
                }

                Console.Out.Flush();
                Console.Error.Flush();
                window.Close();
                app.Shutdown(exitCode);
            }
        };

        timer.Start();
        app.Exit += (_, _) =>
        {
            try
            {
                Task.WhenAny(Task.Run(async () => await orch.DisposeAsync()), Task.Delay(1200)).GetAwaiter().GetResult();
            }
            catch
            {
                // Bounded teardown.
            }
        };
        app.Run();
        return exitCode;
    }

    private static void PumpFrames()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new DispatcherOperationCallback(_ =>
        {
            frame.Continue = false;
            return null;
        }), null);
        Dispatcher.PushFrame(frame);
    }

    private static int ParseDurationSec(string[] args, int defaultSeconds)
    {
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (string.Equals(arg, "--duration-sec", StringComparison.OrdinalIgnoreCase) &&
                i + 1 < args.Length &&
                int.TryParse(args[i + 1], out int val) && val is >= 3 and <= 60)
            {
                return val;
            }
            if (arg.StartsWith("--smoke-ms=", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(arg.AsSpan("--smoke-ms=".Length), out int msEq) && msEq >= 2000)
            {
                return Math.Clamp((int)Math.Ceiling(msEq / 1000.0), 3, 60);
            }
            if (string.Equals(arg, "--smoke-ms", StringComparison.OrdinalIgnoreCase) &&
                i + 1 < args.Length &&
                int.TryParse(args[i + 1], out int msVal) && msVal >= 2000)
            {
                return Math.Clamp((int)Math.Ceiling(msVal / 1000.0), 3, 60);
            }
        }
        return defaultSeconds;
    }

    private static string? ParseRenderAuditDir(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--render-audit", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    return Path.GetFullPath(args[i + 1]);
                }
                return Path.GetFullPath("render_audit");
            }

            const string prefix = "--render-audit=";
            if (args[i].StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                string val = args[i][prefix.Length..].Trim('"');
                if (!string.IsNullOrWhiteSpace(val))
                {
                    return Path.GetFullPath(val);
                }
            }
        }
        return null;
    }

    private static int RunRenderAudit(ServiceProvider provider, string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        var orch = provider.GetRequiredService<AppOrchestrator>();
        orch.IsSmokeOrAuditMode = true;
        var coord = orch.Coordinator;
        var window = provider.GetRequiredService<IslandWindow>();
        var hotkeyService = provider.GetRequiredService<GlobalHotkeyService>();

        byte[] albumBytes = CreateDemoAlbumArtPngBytes();
        double[] demoSpectrum = [0.42, 0.78, 0.94, 0.64, 0.85, 0.56, 0.73, 0.36];
        var nowUtc = DateTimeOffset.UtcNow;

        var demoMedia = new MediaPlaybackSnapshot(
            HasSession: true,
            SessionId: "audit:spotify",
            AppName: "Spotify",
            Title: "Midnight City (Obsidian Mix)",
            Artist: "M83 • Hurry Up, We're Dreaming",
            IsPlaying: true,
            CanPlayPause: true,
            CanNext: true,
            CanPrev: true,
            CanSeek: true,
            Position: TimeSpan.FromSeconds(108),
            Duration: TimeSpan.FromSeconds(244),
            LastUpdatedUtc: nowUtc,
            PlaybackRate: 1.0,
            AccentHex: "#3B82F6",
            ThumbnailBytes: albumBytes,
            AllSessions:
            [
                new MediaSessionSummary("audit:spotify", "Spotify", "Midnight City (Obsidian Mix)", true),
                new MediaSessionSummary("audit:edge", "Microsoft Edge", "WWDC Design Session", false)
            ],
            SecondaryAccentHex: "#A855F7",
            HighlightAccentHex: "#38BDF8");

        var demoAudio = new AudioMixerSnapshot(
            MasterVolume01: 0.72f,
            IsMasterMuted: false,
            DefaultOutputId: "out-mac",
            DefaultOutputName: "AirPods Pro (Spatial Stereo Hands-Free)",
            MicVolume01: 0.84f,
            IsMicMuted: false,
            HasMicDevice: true,
            IsLoopbackActive: true,
            IsBluetoothHfpFallback: false,
            SpectrumBands: [.. demoSpectrum],
            AppSessions:
            [
                new AudioAppSessionSnapshot("spotify.exe", 4210, "Spotify", "Spotify", 0.88f, false, 0.65f, null),
                new AudioAppSessionSnapshot("discord.exe", 9140, "Discord", "Discord Voice", 0.64f, false, 0.25f, null)
            ],
            OutputDevices:
            [
                new AudioEndpointSnapshot("out-mac", "AirPods Pro (Spatial Stereo Hands-Free)", true, false, true),
                new AudioEndpointSnapshot("out-spk", "Realtek High Definition Audio Speakers", false, false, false)
            ],
            InputDevices:
            [
                new AudioEndpointSnapshot("in-usb", "MacBook Studio Array Microphone (USB-C)", true, true, false),
                new AudioEndpointSnapshot("in-bt", "AirPods Pro Hands-Free AG Audio", false, true, true)
            ],
            OutputFormatLabel: "48 kHz · 24-bit",
            SupportedOutputFormats:
            [
                new AudioFormatOption(192000, 24, false, 2, "192 kHz · 24-bit", false),
                new AudioFormatOption(96000, 24, false, 2, "96 kHz · 24-bit", false),
                new AudioFormatOption(48000, 24, false, 2, "48 kHz · 24-bit", true)
            ]);

        IReadOnlyList<DisplayBrightnessInfo> demoDisplays =
        [
            new DisplayBrightnessInfo("disp-int", "Built-in Liquid Retina XDR", 1, true, false, true, 78, 0),
            new DisplayBrightnessInfo("disp-ext", "Studio Display 27\"", 2, false, true, false, 64, 15)
        ];

        var demoConn = new ConnectivitySnapshot(
            IsPackagedIdentity: true,
            IsWifiAvailable: true,
            IsWifiOn: true,
            WifiSsid: "Studio-5G",
            WifiSignalPercent: 92,
            IsBluetoothAvailable: true,
            IsBluetoothOn: true,
            PairedBluetoothDevices:
            [
                new BluetoothDeviceSnapshot("bt-1", "AirPods Pro", true, 88, "out-mac"),
                new BluetoothDeviceSnapshot("bt-2", "MX Master 3S", true, 65, null)
            ],
            WindowsFocusProfile: FocusQuietProfile.Off,
            IsDarkTheme: true,
            IsCaffeineActive: true,
            PowerModeIndex: 2,
            IsEnergySaverOn: false);

        var demoPrivacy = new PrivacySensorSnapshot(
            IsMicActive: true,
            IsCamActive: true,
            MicConsumers: ["discord.exe"],
            CamConsumers: ["obs64.exe"]);

        IReadOnlyList<TimerSnapshot> demoTimers =
        [
            new TimerSnapshot("tm-1", "Deep Focus", true, false, true, false, TimeSpan.FromMinutes(25), TimeSpan.FromSeconds(272)),
            new TimerSnapshot("tm-2", "Coffee Break", false, false, false, true, TimeSpan.FromMinutes(5), TimeSpan.Zero)
        ];

        IReadOnlyList<NotificationEntry> demoNotifs =
        [
            new NotificationEntry(1, "Slack", "Design Review Ready", "MacBook Notch ear contour v2 passed 240Hz check and ambient glow audit.", nowUtc.AddMinutes(-2), true, AppIconBytes: albumBytes),
            new NotificationEntry(2, "GitHub", "Build Succeeded", "DinamikAda.App x64 Release — 0 warnings.", nowUtc.AddMinutes(-8), false)
        ];

        IReadOnlyList<ShelfItem> demoShelf =
        [
            new ShelfItem("sh-1", "ObsidianSpec_v3.pdf", @"C:\Temp\ObsidianSpec_v3.pdf", false, 1_482_240, nowUtc.AddMinutes(-5)),
            new ShelfItem("sh-2", "DesignAssets", @"C:\Temp\DesignAssets", true, 0, nowUtc.AddMinutes(-14))
        ];

        IReadOnlyList<ClipboardEntry> demoClip =
        [
            new ClipboardEntry("cp-1", "Text", "dotnet build -c Release -p:Platform=x64", "dotnet build -c Release -p:Platform=x64", null, [], true, nowUtc.AddMinutes(-1)),
            new ClipboardEntry("cp-2", "Text", "k_sq = 0.60; k_e = 0.62; TopChord = 0", "k_sq = 0.60; k_e = 0.62; TopChord = 0", null, [], false, nowUtc.AddMinutes(-4)),
            new ClipboardEntry("cp-3", "Text", "https://localhost:5001/dinamikada/telemetry", "https://localhost:5001/dinamikada/telemetry", null, [], false, nowUtc.AddMinutes(-9))
        ];

        var demoTelemetry = new SystemTelemetrySnapshot(
            CpuPercent: 34.2,
            MemoryPercent: 58.4,
            MemoryUsedGb: 18.7,
            MemoryTotalGb: 32.0,
            GpuPercent: 21.0,
            NetUpMbps: 14.6,
            NetDownMbps: 128.4,
            HasBattery: true,
            BatteryPercent: 82,
            IsCharging: true,
            BatteryWatts: 24.5,
            BatteryRemaining: TimeSpan.FromMinutes(195),
            TopOrHungProcesses:
            [
                new ProcessSummary(4204, "devenv.exe", "DinamikAda — Microsoft Visual Studio", 1420.5, false, false),
                new ProcessSummary(8812, "OldPluginHost.exe", "Legacy Render Bridge (Not Responding)", 312.8, true, false),
                new ProcessSummary(1920, "Spotify.exe", "Spotify Free", 284.2, false, false)
            ]);

        IReadOnlyList<WidgetRenderSnapshot> demoWidgets =
        [
            new WidgetRenderSnapshot(
                "w-pomodoro",
                "Focus Pulse",
                "Deep Work Block",
                "Sprint 2 of 4 • Notifications silenced",
                "Active",
                "#0A84FF",
                Progress0To100: 68.0,
                ShowPillBadge: true,
                PillLabel: "18m",
                IsFaulted: false,
                FaultReason: null,
                Actions:
                [
                    new WidgetActionDescriptor("pause", "Pause", "\uE769"),
                    new WidgetActionDescriptor("skip", "Next", "\uE893")
                ]),
            new WidgetRenderSnapshot(
                "w-weather",
                "Weather Live",
                "19°C • Clear Night",
                "Istanbul • Humidity 54% • Wind 11 km/h",
                "Live",
                "#64D2FF",
                Progress0To100: null,
                ShowPillBadge: false,
                PillLabel: null,
                IsFaulted: false,
                FaultReason: null,
                Actions:
                [
                    new WidgetActionDescriptor("refresh", "Refresh", "\uE72C")
                ])
        ];

        IReadOnlyList<CommandEntry> demoCommands =
        [
            new CommandEntry("c1", "Spotify", "Switch to Spotify desktop window", "Window", "Alt+Tab", 100, _ => Task.FromResult(new CommandExecutionResult(true)), "\uE8D6"),
            new CommandEntry("c2", "= 240 * 1.5", "Copy result: 360", "Calculator", "Enter", 95, _ => Task.FromResult(new CommandExecutionResult(true)), "\uE8EF", "360"),
            new CommandEntry("c3", "Open DinamikAda Settings", "Configure notch geometry & monitors", "Built-in", "Ctrl+,", 90, _ => Task.FromResult(new CommandExecutionResult(true)), "\uE713")
        ];

        void CaptureFrame(string fileName, IslandCoordinatorSnapshot snap, int width = 800, int height = 460)
        {
            window.ApplyRenderAuditState(
                snap,
                demoSpectrum,
                demoMedia,
                demoDisplays,
                demoConn,
                demoPrivacy,
                demoTimers,
                demoNotifs,
                demoShelf,
                demoClip,
                demoTelemetry,
                demoWidgets,
                demoCommands,
                demoAudio);

            var bmp = window.RenderAuditBackdropBitmap(width, height);
            SavePng(bmp, Path.Combine(outputDir, fileName));
        }

        int savedCount = 0;

        // 01: S1 Dormant (NotchFlush)
        coord.RequestCollapse(forceUnpin: true);
        coord.PublishLiveActivity(null, "audit:prim");
        coord.PublishLiveActivity(null, "audit:sat");
        CaptureFrame("01_s1_dormant_flush.png", coord.Current, 640, 120);
        savedCount++;

        // 02: S1 Hairline Edge Peek
        orch.Config.VideoFullscreenPolicy = FullscreenPolicy.EdgePeekHairline;
        coord.UpdateFullscreenState(new FullscreenSnapshot(true, FullscreenAppKind.Video, "vlc.exe", 0, "Audit"));
        CaptureFrame("02_s1_hairline_peek.png", coord.Current, 640, 90);
        savedCount++;

        // Clear fullscreen isolation for interactive captures
        coord.UpdateFullscreenState(new FullscreenSnapshot(false, FullscreenAppKind.None, null, 0, "Audit"));

        // 03: S2 Compact Media
        coord.PublishLiveActivity(new LiveActivityState("audit:prim", ActivityKind.Media, 60, demoMedia.Title, demoMedia.Artist, "#0A84FF", ThumbnailBytes: albumBytes), "audit:prim");
        CaptureFrame("03_s2_compact_media.png", coord.Current, 640, 140);
        savedCount++;

        // 04: S2 Split Satellite
        coord.PublishLiveActivity(new LiveActivityState("audit:sat", ActivityKind.Timer, 50, "04:32", "Focus", "#FF9F0A", IconGlyph: "\uE916"), "audit:sat");
        CaptureFrame("04_s2_split_satellite.png", coord.Current, 640, 140);
        savedCount++;
        coord.PublishLiveActivity(new LiveActivityState("audit:sat", ActivityKind.CustomWidget, 50, "AI 2/3", "AI kota durumu", "#10A37F",
            IconGlyph: "\uE945", TargetTab: ExpandedTabId.AiQuota), "audit:sat");
        CaptureFrame("39_s2_media_ai_split.png", coord.Current, 640, 140);
        savedCount++;

        // 05: S2 Floating Capsule
        orch.Config.DockPlacement = DockPlacementMode.FloatingTop;
        coord.PublishLiveActivity(null, "audit:sat");
        coord.Recompute();
        CaptureFrame("05_s2_floating_capsule.png", coord.Current, 640, 140);
        savedCount++;

        // Restore NotchFlush for remaining Notch screenshots
        orch.Config.DockPlacement = DockPlacementMode.NotchFlush;
        coord.Recompute();

        // 06: S3 Transient HUD
        coord.Publish(new TransientHudEvent("audit:hud", HudKind.Volume, "Speaker Volume", "AirPods Pro", 0.72, "#0A84FF", 4000, IconGlyph: "\uE767"));
        CaptureFrame("06_s3_transient_hud.png", coord.Current, 640, 140);
        savedCount++;
        coord.DismissTransientHud();

        // 07..12: S4 Expanded Tabs
        coord.RequestExpand(ExpandedTabId.Now);
        CaptureFrame("07_s4_tab_now.png", coord.Current);
        savedCount++;

        coord.RequestNavigateTab(ExpandedTabId.Audio);
        CaptureFrame("08_s4_tab_audio.png", coord.Current);
        savedCount++;

        coord.RequestNavigateTab(ExpandedTabId.Control);
        CaptureFrame("09_s4_tab_control.png", coord.Current);
        savedCount++;

        coord.RequestNavigateTab(ExpandedTabId.Shelf);
        CaptureFrame("10_s4_tab_shelf.png", coord.Current);
        savedCount++;

        coord.RequestNavigateTab(ExpandedTabId.System);
        CaptureFrame("11_s4_tab_system.png", coord.Current);
        savedCount++;

        coord.RequestNavigateTab(ExpandedTabId.Widgets);
        CaptureFrame("12_s4_tab_widgets.png", coord.Current);
        savedCount++;

        // 13: S5 Command Palette
        coord.OpenCommandPalette();
        CaptureFrame("13_s5_command_palette.png", coord.Current);
        savedCount++;
        coord.CloseCommandPalette();

        // 14: S6 Drag Shelf
        coord.SetDragShelfActive(true);
        CaptureFrame("14_s6_drag_shelf.png", coord.Current);
        savedCount++;
        coord.SetDragShelfActive(false);

        // 15: SettingsWindow with Live MacBook Bezel Preview
        var settingsWin = new SettingsWindow(orch, hotkeyService, () => { });
        SavePng(settingsWin.RenderToBitmap(), Path.Combine(outputDir, "15_settings_window.png"));
        savedCount++;
        SavePng(settingsWin.RenderToBitmap(scrollToDockSection: true), Path.Combine(outputDir, "15b_settings_dock_pins.png"));
        savedCount++;
        SavePng(settingsWin.RenderToBitmap(440, 430, showIntegrations: true), Path.Combine(outputDir, "37_settings_integrations_narrow.png"));
        savedCount++;

        // 16: Desktop PC S4 Control Center (Ethernet + Master Audio + DDC/CI Display + Real Power Scheme)
        var desktopConn = new ConnectivitySnapshot(
            IsPackagedIdentity: false,
            IsWifiAvailable: false,
            IsWifiOn: false,
            WifiSsid: null,
            WifiSignalPercent: null,
            IsBluetoothAvailable: false,
            IsBluetoothOn: false,
            PairedBluetoothDevices: ImmutableArray<BluetoothDeviceSnapshot>.Empty,
            WindowsFocusProfile: FocusQuietProfile.Off,
            IsDarkTheme: true,
            IsCaffeineActive: false,
            PowerModeIndex: 2,
            IsEnergySaverOn: false,
            IsEthernetConnected: true,
            EthernetName: "Realtek PCIe GbE",
            EthernetSpeedText: "1 Gbps",
            HasBattery: false,
            ActivePowerPlanName: "Yüksek performans");
        IReadOnlyList<DisplayBrightnessInfo> desktopDisplays =
        [
            new DisplayBrightnessInfo("disp-dell", "DELL G2723H", 1, false, true, false, 70, 0)
        ];
        var desktopAudio = demoAudio with { DefaultOutputName = "Realtek High Definition Audio (Hoparlör)", MasterVolume01 = 0.72f, IsMasterMuted = false };
        var desktopPrivacy = new PrivacySensorSnapshot(true, false, ["Discord"], []);

        coord.RequestExpand(ExpandedTabId.Control);
        window.ApplyRenderAuditState(
            coord.Current,
            demoSpectrum,
            demoMedia,
            desktopDisplays,
            desktopConn,
            desktopPrivacy,
            demoTimers,
            demoNotifs,
            demoShelf,
            demoClip,
            demoTelemetry,
            demoWidgets,
            demoCommands,
            desktopAudio);
        SavePng(window.RenderAuditBackdropBitmap(800, 460), Path.Combine(outputDir, "16_s4_tab_control_desktop.png"));
        savedCount++;

        // 17: Desktop PC S4 System Telemetry (Real GPU card + Top CPU consumer + Private Working Set)
        var desktopTelemetry = new SystemTelemetrySnapshot(
            CpuPercent: 48.2,
            GpuPercent: 61.4,
            MemoryPercent: 63.8,
            MemoryUsedGb: 20.4,
            MemoryTotalGb: 32.0,
            NetDownMbps: 42.6,
            NetUpMbps: 8.4,
            HasBattery: false,
            BatteryPercent: 100,
            IsCharging: true,
            BatteryWatts: null,
            BatteryRemaining: null,
            TopOrHungProcesses: ImmutableArray.Create(
                new ProcessSummary(4212, "RobloxPlayerBeta", "Roblox", 2276.0, false, false, 24.6),
                new ProcessSummary(8144, "msedge", "YouTube — Edge", 640.0, false, false, 9.2),
                new ProcessSummary(9920, "Discord", "Discord", 385.0, false, false, 4.1)),
            GpuName: "AMD Radeon RX 6750 XT",
            GpuEngine: "Video Codec");

        coord.RequestExpand(ExpandedTabId.System);
        window.ApplyRenderAuditState(
            coord.Current,
            demoSpectrum,
            demoMedia,
            desktopDisplays,
            desktopConn,
            desktopPrivacy,
            demoTimers,
            demoNotifs,
            demoShelf,
            demoClip,
            desktopTelemetry,
            demoWidgets,
            demoCommands,
            desktopAudio);
        SavePng(window.RenderAuditBackdropBitmap(800, 460), Path.Combine(outputDir, "17_s4_tab_system_desktop.png"));
        savedCount++;

        // 18: S2 Split Meeting Satellite + Synced Lyric Compact
        coord.RequestCollapse(forceUnpin: true);
        coord.PublishLiveActivity(new LiveActivityState("audit:prim", ActivityKind.Media, 60, demoMedia.Title, demoMedia.Artist, "#0A84FF", ThumbnailBytes: albumBytes), "audit:prim");
        coord.PublishLiveActivity(new LiveActivityState("meeting:calendar", ActivityKind.Meeting, 85, "Toplantı • 4:12", "Weekly Architecture Sync", "#30D158", IconGlyph: "\uE787"), "meeting:calendar");
        window.ApplyRenderAuditState(
            coord.Current,
            demoSpectrum,
            demoMedia,
            demoDisplays,
            demoConn,
            demoPrivacy,
            demoTimers,
            demoNotifs,
            demoShelf,
            demoClip,
            demoTelemetry,
            demoWidgets,
            demoCommands,
            demoAudio,
            demoLyric: "Waiting in a car, waiting for a ride in the dark");
        SavePng(window.RenderAuditBackdropBitmap(640, 140), Path.Combine(outputDir, "18_s2_split_meeting.png"));
        savedCount++;

        // 19: S3 BT Low Battery / Transfer Complete HUD
        coord.PublishLiveActivity(null, "meeting:calendar");
        coord.Publish(new TransientHudEvent("audit:hud-tr", HudKind.TransferComplete, "İndirme Tamamlandı", "DinamikAda-v2.zip • 18.4 MB/s", 1.0, "#30D158", 4000, InteractiveOnHover: true, IconGlyph: "\uE896"));
        CaptureFrame("19_s3_transfer_hud.png", coord.Current, 640, 140);
        savedCount++;
        coord.DismissTransientHud();

        // 20: S4 Now Tab with Synced LRC + Meeting Countdown Banner
        coord.RequestExpand(ExpandedTabId.Now);
        window.ApplyRenderAuditState(
            coord.Current,
            demoSpectrum,
            demoMedia,
            demoDisplays,
            demoConn,
            demoPrivacy,
            demoTimers,
            demoNotifs,
            demoShelf,
            demoClip,
            demoTelemetry,
            demoWidgets,
            demoCommands,
            demoAudio,
            demoLyric: "Waiting in a car, waiting for a ride in the dark",
            demoMeeting: new MeetingOccurrence("Weekly Architecture Sync", DateTimeOffset.UtcNow.AddMinutes(4).AddSeconds(12), DateTimeOffset.UtcNow.AddMinutes(34), "https://meet.google.com/abc-defg-hij"));
        SavePng(window.RenderAuditBackdropBitmap(800, 460), Path.Combine(outputDir, "20_s4_tab_now_lyrics_meeting.png"));
        savedCount++;

        // 21: S4 Shelf Tab with LAN AirDrop QR Overlay
        coord.RequestExpand(ExpandedTabId.Shelf);
        window.ApplyRenderAuditState(
            coord.Current,
            demoSpectrum,
            demoMedia,
            demoDisplays,
            demoConn,
            demoPrivacy,
            demoTimers,
            demoNotifs,
            demoShelf,
            demoClip,
            demoTelemetry,
            demoWidgets,
            demoCommands,
            demoAudio,
            demoQrShare: new LanDropShareInfo("http://192.168.1.42:51842/s/a1b2c3", "http://127.0.0.1:51842/s/a1b2c3", true, 2, true, DateTimeOffset.UtcNow.AddMinutes(30)));
        SavePng(window.RenderAuditBackdropBitmap(800, 460), Path.Combine(outputDir, "21_s4_tab_shelf_qr.png"));
        savedCount++;

        // 22: S4 System Tab with VRAM, Temp/Fan, Disk Volumes+IO & Live Ping
        var fullTelemetry = desktopTelemetry with
        {
            GpuVramUsedGb = 5.4,
            GpuVramTotalGb = 12.0,
            GpuTemperatureC = 64.0,
            GpuFanPercent = 42,
            DiskReadMbps = 142.5,
            DiskWriteMbps = 38.2,
            PingMs = 14.0,
            PingHost = "1.1.1.1",
            DiskVolumes =
            [
                new DiskVolumeSnapshot("C:", "OS", 214.0, 476.0, 45.0, false),
                new DiskVolumeSnapshot("D:", "Games", 680.0, 931.0, 73.0, false)
            ]
        };
        coord.RequestExpand(ExpandedTabId.System);
        window.ApplyRenderAuditState(
            coord.Current,
            demoSpectrum,
            demoMedia,
            desktopDisplays,
            desktopConn,
            desktopPrivacy,
            demoTimers,
            demoNotifs,
            demoShelf,
            demoClip,
            fullTelemetry,
            demoWidgets,
            demoCommands,
            desktopAudio);
        SavePng(window.RenderAuditBackdropBitmap(800, 460), Path.Combine(outputDir, "22_s4_tab_system_vram_disk_ping.png"));
        savedCount++;

        // 23: S4 Audio Tab with Per-App Output Routing & Smart Clipboard / EXIF Shelf
        var routingAudio = desktopAudio with
        {
            AppSessions =
            [
                new AudioAppSessionSnapshot("spotify.exe", 4210, "Spotify", "Spotify", 0.88f, false, 0.65f, null, "out-mac"),
                new AudioAppSessionSnapshot("discord.exe", 9140, "Discord", "Discord Voice", 0.64f, false, 0.25f, null, "out-spk")
            ],
            OutputDevices =
            [
                new AudioEndpointSnapshot("out-mac", "AirPods Pro Stereo", true, false, true),
                new AudioEndpointSnapshot("out-spk", "Realtek Speakers", false, false, false),
                new AudioEndpointSnapshot("out-hdmi", "DELL G2723H Audio", false, false, false)
            ]
        };
        coord.RequestExpand(ExpandedTabId.Audio);
        window.ApplyRenderAuditState(
            coord.Current,
            demoSpectrum,
            demoMedia,
            desktopDisplays,
            desktopConn,
            desktopPrivacy,
            demoTimers,
            demoNotifs,
            demoShelf,
            demoClip,
            fullTelemetry,
            demoWidgets,
            demoCommands,
            routingAudio);
        SavePng(window.RenderAuditBackdropBitmap(800, 460), Path.Combine(outputDir, "23_s4_tab_audio_routing.png"));
        savedCount++;

        // 24: S4 Control Tab with Active Scheduled Power Countdown
        coord.RequestExpand(ExpandedTabId.Control);
        window.ApplyRenderAuditState(
            coord.Current,
            demoSpectrum,
            demoMedia,
            desktopDisplays,
            desktopConn,
            desktopPrivacy,
            demoTimers,
            demoNotifs,
            demoShelf,
            demoClip,
            fullTelemetry,
            demoWidgets,
            demoCommands,
            desktopAudio,
            demoScheduledPower: new ScheduledPowerSnapshot(
                ScheduledPowerActionKind.Sleep,
                TimeSpan.FromMinutes(30),
                TimeSpan.FromMinutes(18).Add(TimeSpan.FromSeconds(42)),
                false,
                true));
        SavePng(window.RenderAuditBackdropBitmap(800, 460), Path.Combine(outputDir, "24_s4_tab_control_sched_power.png"));
        savedCount++;

        // 25: S4 Widgets Tab (clean Widget Cards & Color Picker)
        coord.RequestExpand(ExpandedTabId.Widgets);
        window.ApplyRenderAuditState(
            coord.Current,
            demoSpectrum,
            demoMedia,
            desktopDisplays,
            desktopConn,
            desktopPrivacy,
            demoTimers,
            demoNotifs,
            demoShelf,
            demoClip,
            fullTelemetry,
            demoWidgets,
            demoCommands,
            desktopAudio);
        SavePng(window.RenderAuditBackdropBitmap(800, 460), Path.Combine(outputDir, "25_s4_tab_widgets_snap.png"));
        savedCount++;

        // 34 & 35: S4 AI Quota Hub Tab (grouped by entered AI providers only) + Provider-Specific Popup
        var auditUtc = DateTimeOffset.UtcNow;
        AiQuotaSlotSnapshot[] demoAiQuota =
        [
            new("ai1", "ChatGPT", "ChatGPT · Atölye", "#10A37F", "studio-1@example.com", "Plus", 24, 7, "24 Kas", "$20/ay", "pct", 100, 47, 53, 53.0, 20, "codex", "", true, "● OAuth Canlı", "Haftalık: %79 kaldı (↻ 21 Kas)", "gpt-5.3-codex", 300, auditUtc.AddMinutes(239), TimeSpan.FromMinutes(239), true, false, false, 0.20, true, WeeklyRemainingPercent: 79.0, WeeklyResetUtc: auditUtc.AddDays(4).AddHours(16)),
            new("ai2", "ChatGPT", "ChatGPT · Kişisel", "#10A37F", "studio-2@example.com", "Plus", 15, 28, "15 Ara", "$20/ay", "pct", 100, 0, 100, 100.0, 20, "codex", "", true, "● OAuth Canlı", "Haftalık: %100 kaldı (↻ 24 Kas)", "gpt-5.3-codex", 300, auditUtc.AddMinutes(299), TimeSpan.FromMinutes(299), true, false, false, 0.01, true, WeeklyRemainingPercent: 100.0, WeeklyResetUtc: auditUtc.AddDays(7)),
            new("ai3", "Antigravity", "Antigravity · Atölye", "#38BDF8", "lab-1@example.com", "AI Pro", 0, null, null, "", "pct", 100, 67, 33, 33.0, 20, "antigravity", "", true, "● OAuth Canlı", "Google OAuth · 3 havuz", "claude-opus-5-5", 300, auditUtc.AddDays(3).AddHours(14), TimeSpan.FromDays(3).Add(TimeSpan.FromHours(14)), true, false, false, 0.33, true, ModelBuckets:
            [
                new("gemini", "Gemini Models", "Gemini 4 Hydrogen, Gemini 3.8 Flash", 40.0, auditUtc.AddDays(3).AddHours(14), 100.0, auditUtc.AddHours(5), "#4E8DF5"),
                new("claude_gpt", "Claude & GPT", "Claude Opus 5.5, Claude Sonnet 5.5, GPT-OSS 120B", 33.0, auditUtc.AddDays(6).AddHours(15), 100.0, auditUtc.AddHours(5), "#D97757"),
                new("gemini_argon", "Gemini 4 Argon", "Gemini 4 Argon (Pro)", 43.0, auditUtc.AddDays(4), null, null, "#A78BFA")
            ]),
            new("ai4", "Antigravity", "Antigravity · Kişisel", "#38BDF8", "lab-2@example.com", "AI Pro", 0, null, null, "", "pct", 100, 0, 100, 100.0, 20, "antigravity", "", true, "● OAuth Canlı", "Google OAuth · 2 havuz", "gemini-4-hydrogen", 300, null, TimeSpan.Zero, true, false, false, null, true, ModelBuckets:
            [
                new("gemini", "Gemini Models", "Gemini 4 Hydrogen, Gemini 3.8 Flash", 100.0, null, 100.0, null, "#4E8DF5"),
                new("claude_gpt", "Claude & GPT", "Claude Opus 5.5, Claude Sonnet 5.5, GPT-OSS 120B", 100.0, null, 100.0, null, "#D97757")
            ])
        ];
        coord.RequestExpand(ExpandedTabId.AiQuota);
        window.ApplyRenderAuditState(
            coord.Current,
            demoSpectrum,
            demoMedia,
            desktopDisplays,
            desktopConn,
            desktopPrivacy,
            demoTimers,
            demoNotifs,
            demoShelf,
            demoClip,
            fullTelemetry,
            demoWidgets,
            demoCommands,
            desktopAudio,
            demoAiQuota: demoAiQuota);
        SavePng(window.RenderAuditBackdropBitmap(800, 460), Path.Combine(outputDir, "34_s4_tab_aiquota.png"));
        savedCount++;

        var demoReconnect = demoAiQuota[1] with
        {
            IsCliSynced = false,
            CliBadgeText = null,
            SecondarySummary = null,
            IsReady = false,
            HasPrimaryQuota = false,
            WeeklyRemainingPercent = null,
            WeeklyResetUtc = null,
            NextResetUtc = null,
            RequiresReconnect = true
        };
        var demoClaude = demoAiQuota[0] with
        {
            Id = "ai5",
            Provider = "Claude",
            Label = "Claude · Atölye",
            AccentHex = "#D97757",
            AccountIdentifier = "claude@example.com",
            LocalSourceMode = "claude",
            CliBadgeText = "Claude Code /usage",
            SecondarySummary = null,
            ActiveModelName = null
        };
        window.ApplyRenderAuditState(
            coord.Current,
            demoSpectrum,
            demoMedia,
            desktopDisplays,
            desktopConn,
            desktopPrivacy,
            demoTimers,
            demoNotifs,
            demoShelf,
            demoClip,
            fullTelemetry,
            demoWidgets,
            demoCommands,
            desktopAudio,
            demoAiQuota: [demoAiQuota[0], demoReconnect, demoClaude]);
        SavePng(window.RenderAuditBackdropBitmap(800, 460), Path.Combine(outputDir, "41_s4_tab_aiquota_claude.png"));
        savedCount++;

        window.OpenAiQuotaEditorForAudit(demoAiQuota[2]);
        SavePng(window.RenderAuditBackdropBitmap(800, 460), Path.Combine(outputDir, "35_s4_tab_aiquota_editor.png"));
        savedCount++;

        // 26: S3 USB Drive Plug HUD with Open Folder & Safe Eject Actions
        coord.RequestCollapse(forceUnpin: true);
        coord.Publish(new TransientHudEvent(
            CoalesceKey: "audit:hud-usb",
            Kind: HudKind.UsbDrive,
            Title: "USB Sürücü Takıldı • KINGSTON (E:)",
            Subtitle: "24.8 GB boş / 59.6 GB",
            Value01: 0.58,
            AccentHex: "#30D158",
            DurationMs: 6500,
            InteractiveOnHover: true,
            IsSticky: false,
            IconGlyph: "\uECF0",
            PrimaryActionId: "usb.open",
            PrimaryActionLabel: "Klasörü Aç",
            SecondaryActionId: "usb.eject",
            SecondaryActionLabel: "Güvenle Çıkar",
            ActionPayload: @"E:\"));
        CaptureFrame("26_s3_usb_drive_hud.png", coord.Current, 640, 140);
        savedCount++;
        coord.DismissTransientHud();

        // 27: S3 Sticky Final-60s Scheduled Power Warning HUD (+10m & Cancel)
        coord.Publish(new TransientHudEvent(
            CoalesceKey: "audit:hud-sched-power",
            Kind: HudKind.ScheduledPower,
            Title: "Son 1 Dakika Uyarısı • Uyku",
            Subtitle: "42 sn kaldı",
            Value01: 0.08,
            AccentHex: "#FF9F0A",
            DurationMs: 10000,
            InteractiveOnHover: true,
            IsSticky: true,
            IconGlyph: "\uE7E8",
            PrimaryActionId: "power.snooze10",
            PrimaryActionLabel: "+10 dk Ertele",
            SecondaryActionId: "power.cancel",
            SecondaryActionLabel: "Kapat"));
        CaptureFrame("27_s3_sched_power_hud.png", coord.Current, 640, 140);
        savedCount++;
        coord.DismissTransientHud();

        // 28: S2 Split with Red Mic-Muted Satellite Pill
        coord.PublishLiveActivity(new LiveActivityState("audit:prim", ActivityKind.Media, 60, demoMedia.Title, demoMedia.Artist, "#0A84FF", ThumbnailBytes: albumBytes), "audit:prim");
        coord.PublishLiveActivity(new LiveActivityState("audio:mic-muted", ActivityKind.MicMuted, 95, DinamikAda.Core.Localization.Strings.MicMutedBadge(orch.Config.Language), "Ctrl+Shift+M", "#FF453A", IconGlyph: "\uEC54", TargetTab: ExpandedTabId.Audio), "audio:mic-muted");
        CaptureFrame("28_s2_split_mic_muted.png", coord.Current, 640, 140);
        savedCount++;
        coord.PublishLiveActivity(null, "audio:mic-muted");

        // 29: S3 Color Picked Swatch HUD (RGB + HSL Buttons)
        coord.Publish(new TransientHudEvent(
            CoalesceKey: "audit:hud-color",
            Kind: HudKind.ColorPicked,
            Title: "Renk Seçici • #38BDF8",
            Subtitle: "rgb(56, 189, 248) • hsl(198, 93%, 60%)",
            Value01: null,
            AccentHex: "#38BDF8",
            DurationMs: 5500,
            InteractiveOnHover: true,
            IsSticky: false,
            IconGlyph: "\uEF3C",
            PrimaryActionId: "color.copy-rgb",
            PrimaryActionLabel: "RGB",
            SecondaryActionId: "color.copy-hsl",
            SecondaryActionLabel: "HSL",
            ActionPayload: "rgb(56, 189, 248)|hsl(198, 93%, 60%)"));
        CaptureFrame("29_s3_color_picked_hud.png", coord.Current, 640, 140);
        savedCount++;
        coord.DismissTransientHud();

        // 30: S4 Feed Tab with Opaque In-Island Notification Detail Overlay
        coord.RequestExpand(ExpandedTabId.Now);
        window.ApplyRenderAuditState(
            coord.Current,
            demoSpectrum,
            demoMedia,
            desktopDisplays,
            desktopConn,
            desktopPrivacy,
            demoTimers,
            demoNotifs,
            demoShelf,
            demoClip,
            fullTelemetry,
            demoWidgets,
            demoCommands,
            routingAudio);
        window.OpenNotificationDetailForAudit(demoNotifs[0]);
        SavePng(window.RenderAuditBackdropBitmap(800, 460), Path.Combine(outputDir, "30_s4_notif_detail_overlay.png"));
        savedCount++;

        orch.AiQuota.SeedStarterSlotsIfNeeded();
        var dockAudit = provider.GetRequiredService<DockWindow>();
        SavePng(dockAudit.RenderForAudit(960, 96), Path.Combine(outputDir, "31_bottom_dock.png"));
        savedCount++;
        SavePng(dockAudit.RenderCalendarPopupForAudit(300, 340), Path.Combine(outputDir, "32_dock_calendar_popup.png"));
        savedCount++;
        SavePng(dockAudit.RenderTrayPopupForAudit(), Path.Combine(outputDir, "33_dock_tray_popup.png"));
        savedCount++;
        var emptyTrayAudit = new DockWindow(orch, provider.GetRequiredService<WindowsTaskbarManager>());
        SavePng(emptyTrayAudit.RenderTrayPopupForAudit(324, 220, empty: true), Path.Combine(outputDir, "40_dock_tray_empty_narrow.png"));
        savedCount++;
        SavePng(dockAudit.RenderStartPopupForAudit(), Path.Combine(outputDir, "36_dock_start_popup.png"));
        savedCount++;
        SavePng(dockAudit.RenderStartPopupForAudit(450, 540, "visual"), Path.Combine(outputDir, "38_dock_start_search_narrow.png"));
        savedCount++;

        // Dock widgets: showcase rows in every style, the top edge, cards and the picker.
        string[] widgetShowcase = ["np-mini", "cpu", "ram", "network", "weather", "flip-clock", "battery-ring", "todos", "crypto", "day-progress"];
        string[] widgetShowcase2 = ["now-playing", "ai-limits", "pomodoro", "forecast", "segment-clock", "battery-core", "quick-toggles"];
        string[] widgetShowcase3 = ["analog", "day-number", "world-clocks", "screen-time", "sun", "storage", "year-progress"];
        var widgetDockAudit = new DockWindow(orch, provider.GetRequiredService<WindowsTaskbarManager>());
        foreach (var style in Enum.GetValues<DockVisualStyle>())
        {
            SavePng(widgetDockAudit.RenderWidgetDockForAudit(widgetShowcase, style), Path.Combine(outputDir, $"50_dock_widgets_{style.ToString().ToLowerInvariant()}.png"));
            savedCount++;
        }
        SavePng(widgetDockAudit.RenderWidgetDockForAudit(widgetShowcase2, DockVisualStyle.Midnight), Path.Combine(outputDir, "51_dock_widgets_row2.png"));
        savedCount++;
        SavePng(widgetDockAudit.RenderWidgetDockForAudit(widgetShowcase3, DockVisualStyle.LiquidGlass), Path.Combine(outputDir, "52_dock_widgets_row3.png"));
        savedCount++;
        SavePng(widgetDockAudit.RenderWidgetDockForAudit(widgetShowcase, DockVisualStyle.Midnight, edge: DockScreenEdge.Top), Path.Combine(outputDir, "53_dock_widgets_top_edge.png"));
        savedCount++;
        foreach (var cardId in new[] { "weather", "screen-time", "todos", "now-playing", "network", "ai-limits" })
        {
            SavePng(widgetDockAudit.RenderWidgetCardForAudit(cardId), Path.Combine(outputDir, $"54_widget_card_{cardId}.png"));
            savedCount++;
        }
        SavePng(widgetDockAudit.RenderWidgetPickerForAudit(), Path.Combine(outputDir, "55_widget_picker.png"));
        savedCount++;

        Console.WriteLine($"RENDER_AUDIT_PASS: {savedCount} files -> {outputDir}");
        return 0;
    }

    private static byte[] CreateDemoAlbumArtPngBytes()
    {
        const int size = 128;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var bg = new LinearGradientBrush(
                Color.FromRgb(16, 48, 128),
                Color.FromRgb(124, 34, 178),
                new System.Windows.Point(0, 0),
                new System.Windows.Point(1, 1));
            bg.Freeze();
            dc.DrawRoundedRectangle(bg, null, new System.Windows.Rect(0, 0, size, size), 18, 18);

            var sun = new RadialGradientBrush(
                Color.FromArgb(220, 100, 210, 255),
                Color.FromArgb(0, 10, 132, 255));
            sun.Freeze();
            dc.DrawEllipse(sun, null, new System.Windows.Point(64, 52), 46, 46);

            var ringBrush = new SolidColorBrush(Color.FromArgb(170, 255, 255, 255));
            ringBrush.Freeze();
            var ringPen = new Pen(ringBrush, 4.0);
            ringPen.Freeze();
            dc.DrawEllipse(null, ringPen, new System.Windows.Point(64, 64), 26, 26);
        }

        var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    private static void SavePng(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        encoder.Save(fs);
    }
}
