using DinamikAda.Core.Abstractions;
using DinamikAda.Core.Arbitration;
using DinamikAda.Core.Config;
using DinamikAda.Core.Physics;
using Xunit;

namespace DinamikAda.Tests;

public sealed class CorePhysicsAndArbitrationTests
{
    [Fact]
    public void SpringDamper_ConvergesCleanlyEvenWithLargeDtSpikes()
    {
        var solver = new SpringDamperSolver(132.0, stiffness: 380.0, dampingRatio: 0.80);
        solver.Retarget(520.0);

        // Simulate 1.2s alt-tab frame stall — must clamp substeps instead of exploding NaN/Infinity!
        double mid = solver.Step(1.2);
        Assert.False(double.IsNaN(mid));
        Assert.False(double.IsInfinity(mid));
        Assert.InRange(mid, 132.0, 560.0);

        for (int i = 0; i < 300; i++)
        {
            solver.Step(1.0 / 60.0);
        }

        Assert.True(solver.IsSettled);
        Assert.Equal(520.0, solver.Value, precision: 1);
        Assert.Equal(0.0, solver.Velocity, precision: 1);
    }

    [Fact]
    public void IslandCoordinator_HandlesSplitPriorityAndHudLifecycleDeterministically()
    {
        var fakeTime = new FakeTimeProvider();
        var config = new AppConfig();
        var coord = new IslandCoordinator(config, fakeTime);

        Assert.Equal(IslandPresentationState.S1Dormant, coord.Current.State);

        // Publish Media (Priority 60) -> S2Compact
        coord.PublishLiveActivity(new LiveActivityState("media", ActivityKind.Media, 60, "Track A", "Artist A"), "media");
        Assert.Equal(IslandPresentationState.S2Compact, coord.Current.State);
        Assert.Null(coord.Current.SatelliteActivity);

        // Publish higher-priority PrivacySensor (Priority 100) -> S2Split with Privacy Primary + Media Satellite
        coord.PublishLiveActivity(new LiveActivityState("privacy", ActivityKind.PrivacySensor, 100, "Mikrofon", "Discord"), "privacy");
        Assert.Equal(IslandPresentationState.S2Split, coord.Current.State);
        Assert.Equal("privacy", coord.Current.PrimaryActivity?.SourceId);
        Assert.Equal("media", coord.Current.SatelliteActivity?.SourceId);

        // Coalescing Transient HUD
        coord.Publish(new TransientHudEvent("hud:vol", HudKind.Volume, "Ses", "50%", 0.50, DurationMs: 1200));
        coord.Publish(new TransientHudEvent("hud:vol", HudKind.Volume, "Ses", "78%", 0.78, DurationMs: 1200));
        Assert.Equal(IslandPresentationState.S3TransientHud, coord.Current.State);
        Assert.Equal("78%", coord.Current.ActiveHud?.Subtitle);

        // Advance past expiration -> falls cleanly back to S2Split
        fakeTime.Advance(TimeSpan.FromMilliseconds(1500));
        coord.TickExpirations();
        Assert.Equal(IslandPresentationState.S2Split, coord.Current.State);
        Assert.Null(coord.Current.ActiveHud);
    }

    [Fact]
    public void IslandCoordinator_DismissByKey_OnlyClearsTheMatchingHud()
    {
        var coord = new IslandCoordinator(new AppConfig(), new FakeTimeProvider());
        coord.Publish(new TransientHudEvent("timer:1", HudKind.TimerAlert, "Timer", "Done", null, DurationMs: 5000));

        coord.DismissTransientHud("timer:other");
        Assert.Equal("timer:1", coord.Current.ActiveHud?.CoalesceKey);

        coord.DismissTransientHud("timer:1");
        Assert.Null(coord.Current.ActiveHud);
    }

    [Fact]
    public void IslandCoordinator_RespectsQuietIslandAndFullscreenWhitelist()
    {
        var fakeTime = new FakeTimeProvider();
        var config = new AppConfig
        {
            GameFullscreenPolicy = FullscreenPolicy.HideCompletely,
            FullscreenWhitelistExes = new List<string> { "mygame" }
        };
        var coord = new IslandCoordinator(config, fakeTime);

        // Quiet Island blocks non-sticky HUD pulses
        coord.SetQuietIsland(true);
        coord.Publish(new TransientHudEvent("notif:1", HudKind.NotificationPulse, "Toast", "Hi", DurationMs: 2000, IsSticky: false));
        Assert.Equal(IslandPresentationState.S1Dormant, coord.Current.State);

        // Un-whitelisted game hides completely
        coord.UpdateFullscreenState(new FullscreenSnapshot(true, FullscreenAppKind.Game, "othergame", 1, "QUNS=3"));
        Assert.Equal(IslandPresentationState.S0Hidden, coord.Current.State);

        // Whitelisted exe stays compact visible
        coord.UpdateFullscreenState(new FullscreenSnapshot(true, FullscreenAppKind.Game, "MYGAME", 1, "QUNS=3"));
        Assert.Equal(IslandPresentationState.S1Dormant, coord.Current.State);
    }

    [Fact]
    public void IslandMotionState_AnimatesSecondaryRigAndSettlesCleanly()
    {
        var motion = new IslandMotionState();
        motion.ApplyTarget(new IslandLayoutTarget(IslandPresentationState.S2Split, 220, 32, 16, 40, 8, false, false), isExpanding: true);
        motion.RetargetTabIndicator(64, 58);
        motion.TriggerTabTransition();
        motion.SetHoverScale(1.03);
        motion.SetCompactProgress(0.75);

        Assert.False(motion.IsAllSettled);

        for (int i = 0; i < 240; i++)
        {
            motion.StepAll(1.0 / 60.0);
        }

        Assert.True(motion.IsAllSettled);
        Assert.Equal(1.0, motion.SatelliteOpacity, precision: 2);
        Assert.Equal(64.0, motion.TabIndicatorX.Value, precision: 1);
        Assert.Equal(1.0, motion.TabContentOpacity, precision: 2);
        Assert.Equal(0.75, motion.CompactProgress.Value, precision: 2);
        Assert.Equal(1.03, motion.HoverScale.Value, precision: 2);
    }

    [Fact]
    public void IslandCoordinator_NavigatesToTabDirectlyFromCommandPalette()
    {
        var coord = new IslandCoordinator(new AppConfig(), new FakeTimeProvider());
        coord.OpenCommandPalette();
        Assert.Equal(IslandPresentationState.S5Command, coord.Current.State);

        coord.RequestNavigateTab(ExpandedTabId.System);
        Assert.Equal(IslandPresentationState.S4Expanded, coord.Current.State);
        Assert.Equal(ExpandedTabId.System, coord.Current.ActiveTab);
    }

    [Fact]
    public void IslandCoordinator_EdgePeekHairlineAppliesEvenWithActiveMedia()
    {
        var config = new AppConfig { VideoFullscreenPolicy = FullscreenPolicy.EdgePeekHairline };
        var coord = new IslandCoordinator(config, new FakeTimeProvider());
        coord.PublishLiveActivity(new LiveActivityState("media", ActivityKind.Media, 60, "Track", "Artist"), "media");
        Assert.Equal(IslandPresentationState.S2Compact, coord.Current.State);

        coord.UpdateFullscreenState(new FullscreenSnapshot(true, FullscreenAppKind.Video, "zen", 10, "QUNS=2"), 10);
        Assert.Equal(IslandPresentationState.S1Dormant, coord.Current.State);
        Assert.Equal(4.0, coord.Current.Layout.HeightDip);
    }

    [Fact]
    public void IslandCoordinator_HideCompletelyBlocksAllFullscreenKindsAndDragCatcher()
    {
        var config = new AppConfig { ProductivityFullscreenPolicy = FullscreenPolicy.HideCompletely };
        var coord = new IslandCoordinator(config, new FakeTimeProvider());
        coord.UpdateFullscreenState(new FullscreenSnapshot(true, FullscreenAppKind.Productivity, "code", 10, "BorderlessMonitorCover"), 10);

        Assert.Equal(IslandPresentationState.S0Hidden, coord.Current.State);
        coord.RequestExpand(ExpandedTabId.Now);
        coord.SetDragCatcherArmed(true);
        Assert.Equal(IslandPresentationState.S0Hidden, coord.Current.State);
        Assert.False(coord.Current.IsDragCatcherArmed);
    }

    [Fact]
    public void SpringDamper_RetargetDetectsSubMilliThresholdChanges()
    {
        var solver = new SpringDamperSolver(1.0);
        solver.SnapTo(1.0);
        Assert.True(solver.IsSettled);

        solver.Retarget(1.0002);
        Assert.False(solver.IsSettled);
    }

    [Fact]
    public void IslandCoordinator_ProducesScaledMacBookNotchGeometryAcrossStates()
    {
        var config = new AppConfig
        {
            DockPlacement = DockPlacementMode.NotchFlush,
            NotchScalePreset = NotchScalePreset.Standard,
            ShowTelemetryInPill = false
        };
        var coord = new IslandCoordinator(config, new FakeTimeProvider());

        // S1Dormant NotchFlush (without & with telemetry)
        Assert.Equal(176.0, coord.Current.Layout.WidthDip, precision: 2);
        Assert.Equal(28.0, coord.Current.Layout.HeightDip, precision: 2);
        Assert.Equal(13.0, coord.Current.Layout.CornerRadiusDip, precision: 2);
        Assert.Equal(8.0, coord.Current.Layout.EarRadius, precision: 2);
        Assert.Equal(0.0, coord.Current.Layout.TopCornerRadius, precision: 2);

        config.ShowTelemetryInPill = true;
        coord.Recompute();
        Assert.Equal(212.0, coord.Current.Layout.WidthDip, precision: 2);

        // S2Compact & S2Split
        coord.PublishLiveActivity(new LiveActivityState("media", ActivityKind.Media, 60, "Track", "Artist"), "media");
        Assert.Equal(276.0, coord.Current.Layout.WidthDip, precision: 2);
        Assert.Equal(36.0, coord.Current.Layout.HeightDip, precision: 2);
        Assert.Equal(16.0, coord.Current.Layout.CornerRadiusDip, precision: 2);
        Assert.Equal(9.0, coord.Current.Layout.EarRadius, precision: 2);
        Assert.Equal(0.0, coord.Current.Layout.TopCornerRadius, precision: 2);

        coord.PublishLiveActivity(new LiveActivityState("priv", ActivityKind.PrivacySensor, 100, "Cam"), "priv");
        Assert.Equal(276.0, coord.Current.Layout.WidthDip, precision: 2);
        Assert.Equal(96.0, coord.Current.Layout.SatelliteWidthDip, precision: 2);
        Assert.Equal(10.0, coord.Current.Layout.SatelliteGapDip, precision: 2);

        // S3TransientHud
        coord.Publish(new TransientHudEvent("hud:v", HudKind.Volume, "Vol", "50%", 0.5));
        Assert.Equal(308.0, coord.Current.Layout.WidthDip, precision: 2);
        Assert.Equal(44.0, coord.Current.Layout.HeightDip, precision: 2);
        Assert.Equal(18.0, coord.Current.Layout.CornerRadiusDip, precision: 2);
        Assert.Equal(9.0, coord.Current.Layout.EarRadius, precision: 2);
        Assert.Equal(0.0, coord.Current.Layout.TopCornerRadius, precision: 2);
        coord.DismissTransientHud();

        // S4Expanded tabs
        var expectedHeights = new Dictionary<ExpandedTabId, double>
        {
            [ExpandedTabId.Now] = 280.0,
            [ExpandedTabId.Control] = 296.0,
            [ExpandedTabId.Audio] = 332.0,
            [ExpandedTabId.Shelf] = 336.0,
            [ExpandedTabId.System] = 336.0,
            [ExpandedTabId.Widgets] = 316.0,
            [ExpandedTabId.AiQuota] = 348.0
        };
        foreach (var (tab, h) in expectedHeights)
        {
            coord.RequestNavigateTab(tab);
            Assert.Equal(616.0, coord.Current.Layout.WidthDip, precision: 2);
            Assert.Equal(h, coord.Current.Layout.HeightDip, precision: 2);
            Assert.Equal(24.0, coord.Current.Layout.CornerRadiusDip, precision: 2);
            Assert.Equal(14.0, coord.Current.Layout.EarRadius, precision: 2);
            Assert.Equal(0.0, coord.Current.Layout.TopCornerRadius, precision: 2);
        }

        // S5Command & S6DragShelf
        coord.OpenCommandPalette();
        Assert.Equal(564.0, coord.Current.Layout.WidthDip, precision: 2);
        Assert.Equal(368.0, coord.Current.Layout.HeightDip, precision: 2);
        Assert.Equal(12.0, coord.Current.Layout.EarRadius, precision: 2);

        coord.SetDragShelfActive(true);
        Assert.Equal(416.0, coord.Current.Layout.WidthDip, precision: 2);
        Assert.Equal(164.0, coord.Current.Layout.HeightDip, precision: 2);
        Assert.Equal(12.0, coord.Current.Layout.EarRadius, precision: 2);
        coord.SetDragShelfActive(false);

        // FloatingTop & Wide scaling (1.06x), plus unscaled EdgePeekHairline
        config.DockPlacement = DockPlacementMode.FloatingTop;
        config.NotchScalePreset = NotchScalePreset.Wide;
        coord.RequestExpand(ExpandedTabId.Now);
        Assert.Equal(616.0 * 1.06, coord.Current.Layout.WidthDip, precision: 2);
        Assert.Equal(280.0 * 1.06, coord.Current.Layout.HeightDip, precision: 2);
        Assert.Equal(0.0, coord.Current.Layout.EarRadius, precision: 2);
        Assert.Equal(24.0 * 1.06, coord.Current.Layout.TopCornerRadius, precision: 2);

        coord.RequestCollapse(forceUnpin: true);
        config.VideoFullscreenPolicy = FullscreenPolicy.EdgePeekHairline;
        coord.UpdateFullscreenState(new FullscreenSnapshot(true, FullscreenAppKind.Video, "vlc", 10, "QUNS=2"), 10);
        Assert.Equal(68.0, coord.Current.Layout.WidthDip, precision: 2);
        Assert.Equal(4.0, coord.Current.Layout.HeightDip, precision: 2);
        Assert.Equal(2.0, coord.Current.Layout.CornerRadiusDip, precision: 2);
        Assert.Equal(0.0, coord.Current.Layout.EarRadius, precision: 2);
        Assert.Equal(0.0, coord.Current.Layout.TopCornerRadius, precision: 2);
    }

    [Fact]
    public void IslandCoordinator_EnsureValidPinnedTabSelection_UpdatesActiveTabWithoutExpanding()
    {
        var config = new AppConfig();
        var coord = new IslandCoordinator(config, new FakeTimeProvider());
        Assert.Equal(IslandPresentationState.S1Dormant, coord.Current.State);
        Assert.Equal(ExpandedTabId.Now, coord.Current.ActiveTab);

        config.PinnedTabs = [ExpandedTabId.Audio, ExpandedTabId.System];
        coord.EnsureValidPinnedTabSelection();

        Assert.Equal(ExpandedTabId.Audio, coord.Current.ActiveTab);
        Assert.Equal(IslandPresentationState.S1Dormant, coord.Current.State);
    }

    [Fact]
    public void IslandMotionState_StepsAsymmetricSpectrumAndAuraPulseAndSettles()
    {
        var motion = new IslandMotionState();
        motion.ApplyTarget(new IslandLayoutTarget(
            IslandPresentationState.S4Expanded,
            WidthDip: 616,
            HeightDip: 280,
            CornerRadiusDip: 24,
            SatelliteWidthDip: 0,
            SatelliteGapDip: 0,
            IsClickThrough: false,
            RequiresKeyboardFocus: false,
            EarRadius: 14,
            TopCornerRadius: 0), isExpanding: true);

        motion.TriggerAlbumChange(triggerAuraPulse: true);
        double[] targets = [0.9, 0.7, 0.5, 0.3, 0.8, 0.6, 0.4, 0.2];
        motion.SetSpectrumTargets(targets);
        Assert.False(motion.IsAllSettled);

        // Single step: attack is faster than decay
        motion.StepAll(1.0 / 60.0);
        double attackedVal = motion.SpectrumBars[0];
        Assert.True(attackedVal > 0.3);

        // Let aura pulse settle within <= 660ms (40 frames @ 60Hz)
        for (int i = 0; i < 39; i++)
        {
            motion.StepAll(1.0 / 60.0);
        }
        Assert.True(motion.AlbumAuraPulse.IsSettled);

        // Decay spectrum back to zero and verify full settle
        motion.SetSpectrumTargets(ReadOnlySpan<double>.Empty);
        for (int i = 0; i < 180; i++)
        {
            motion.StepAll(1.0 / 60.0);
        }

        Assert.True(motion.IsAllSettled);
        Assert.All(motion.SpectrumBars, bar => Assert.Equal(0.0, bar, precision: 3));
        Assert.Equal(14.0, motion.EarRadius.Value, precision: 1);
        Assert.Equal(0.0, motion.TopCornerRadius.Value, precision: 1);
        Assert.Equal(1.0, motion.ContentRevealTier1, precision: 2);
        Assert.Equal(1.0, motion.ContentRevealTier2, precision: 2);
        Assert.Equal(1.0, motion.ContentRevealTier3, precision: 2);
    }

    [Fact]
    public void IslandMotionState_ZeroOverlapSequentialCrossfade_AndSoftHoverSettle()
    {
        var motion = new IslandMotionState();
        var dormantTarget = new IslandLayoutTarget(
            IslandPresentationState.S1Dormant,
            WidthDip: 184,
            HeightDip: 32,
            CornerRadiusDip: 14,
            SatelliteWidthDip: 0,
            SatelliteGapDip: 0,
            IsClickThrough: false,
            RequiresKeyboardFocus: false,
            EarRadius: 10,
            TopCornerRadius: 0);
        var expandedTarget = new IslandLayoutTarget(
            IslandPresentationState.S4Expanded,
            WidthDip: 650,
            HeightDip: 296,
            CornerRadiusDip: 24,
            SatelliteWidthDip: 0,
            SatelliteGapDip: 0,
            IsClickThrough: false,
            RequiresKeyboardFocus: false,
            EarRadius: 14,
            TopCornerRadius: 0);

        motion.ApplyTarget(dormantTarget, isExpanding: false);
        for (int i = 0; i < 120; i++)
        {
            motion.StepAll(1.0 / 120.0);
        }
        Assert.True(motion.IsAllSettled);

        // Soft hover breath then dwell-expand
        motion.SetHoverScale(1.018);
        for (int i = 0; i < 24; i++)
        {
            motion.StepAll(1.0 / 120.0);
        }

        motion.ApplyTarget(expandedTarget, isExpanding: true);
        for (int i = 0; i < 240; i++)
        {
            motion.StepAll(1.0 / 120.0);
            Assert.True(motion.HoverScale.Value >= 0.998, $"Unexpected inward hover undershoot {motion.HoverScale.Value} at frame {i}");
            bool compactStillVisible = motion.CompactOverlayGate > 0.01;
            bool expandedAlreadyVisible = motion.ContentRevealTier2 > 0.01;
            Assert.False(compactStillVisible && expandedAlreadyVisible, $"Opening crossfade overlap at frame {i}");
        }
        Assert.True(motion.IsAllSettled);
        Assert.Equal(0.0, motion.CompactOverlayGate, precision: 3);
        Assert.Equal(1.0, motion.ContentRevealTier2, precision: 3);

        // Collapse back to S1Dormant: overlay exits BEFORE compact gate opens
        motion.ApplyTarget(dormantTarget, isExpanding: false);
        for (int i = 0; i < 240; i++)
        {
            motion.StepAll(1.0 / 120.0);
            bool overlayStillVisible = motion.ContentRevealTier2 > 0.01;
            bool compactAlreadyVisible = motion.CompactOverlayGate > 0.01;
            Assert.False(overlayStillVisible && compactAlreadyVisible, $"Closing crossfade overlap at frame {i}");
        }
        Assert.True(motion.IsAllSettled);
        Assert.Equal(0.0, motion.ContentRevealTier2, precision: 3);
        Assert.Equal(1.0, motion.CompactOverlayGate, precision: 3);
    }

    [Fact]
    public void IslandCoordinator_MultiMonitorFullscreenIsolation_AndCommandTransition()
    {
        var config = new AppConfig();
        var coord = new IslandCoordinator(config, new FakeTimeProvider());

        coord.SetIslandMonitorHandle(100);
        coord.UpdateFullscreenState(new FullscreenSnapshot(true, FullscreenAppKind.Game, "eldenring", 200, "QUNS=3"), islandMonitorHandle: 100);
        Assert.Equal(IslandPresentationState.S1Dormant, coord.Current.State);

        coord.UpdateFullscreenState(new FullscreenSnapshot(true, FullscreenAppKind.Game, "eldenring", 100, "QUNS=3"), islandMonitorHandle: 100);
        Assert.Equal(IslandPresentationState.S0Hidden, coord.Current.State);

        coord.UpdateFullscreenState(new FullscreenSnapshot(false, FullscreenAppKind.None, null, 100, "Windowed"), islandMonitorHandle: 100);
        Assert.Equal(IslandPresentationState.S1Dormant, coord.Current.State);

        coord.RequestExpand(ExpandedTabId.Widgets);
        Assert.Equal(IslandPresentationState.S4Expanded, coord.Current.State);
        Assert.Equal(ExpandedTabId.Widgets, coord.Current.ActiveTab);

        coord.OpenCommandPalette();
        Assert.Equal(IslandPresentationState.S5Command, coord.Current.State);
        Assert.True(coord.Current.Layout.RequiresKeyboardFocus);

        coord.CloseCommandPalette();
        Assert.NotEqual(IslandPresentationState.S5Command, coord.Current.State);
        Assert.False(coord.Current.Layout.RequiresKeyboardFocus);
    }

    [Fact]
    public void IslandMotionState_PreservesCompactContentContinuityBetweenCompactStates()
    {
        var motion = new IslandMotionState();
        var dormant = new IslandLayoutTarget(
            State: IslandPresentationState.S1Dormant,
            WidthDip: 156,
            HeightDip: 32,
            CornerRadiusDip: 16,
            SatelliteWidthDip: 0,
            SatelliteGapDip: 0,
            IsClickThrough: false,
            RequiresKeyboardFocus: false,
            EarRadius: 14,
            TopCornerRadius: 0);
        var compact = dormant with { State = IslandPresentationState.S2Compact, WidthDip = 224 };
        var split = compact with { State = IslandPresentationState.S2Split, WidthDip = 252, SatelliteWidthDip = 72, SatelliteGapDip = 8 };

        motion.ApplyTarget(dormant, isExpanding: false);
        for (int i = 0; i < 180; i++)
        {
            motion.StepAll(1.0 / 120.0);
        }
        Assert.Equal(1.0, motion.CompactContentReveal.Value, precision: 3);

        // S1Dormant -> S2Compact -> S2Split -> S2Compact must never blink CompactContentReveal down to 0
        motion.ApplyTarget(compact, isExpanding: true);
        Assert.True(motion.CompactContentReveal.Value > 0.9, $"CompactContentReveal blinked on S1->S2Compact: {motion.CompactContentReveal.Value}");
        motion.StepAll(1.0 / 120.0);

        motion.ApplyTarget(split, isExpanding: true);
        Assert.True(motion.CompactContentReveal.Value > 0.9, $"CompactContentReveal blinked on S2Compact->S2Split: {motion.CompactContentReveal.Value}");
        motion.StepAll(1.0 / 120.0);

        motion.ApplyTarget(compact, isExpanding: false);
        Assert.True(motion.CompactContentReveal.Value > 0.9, $"CompactContentReveal blinked on S2Split->S2Compact: {motion.CompactContentReveal.Value}");
    }

    [Fact]
    public void IslandCoordinator_KeepsMediaAndQuotaInPlaceWhenPlaybackPauses()
    {
        var coord = new IslandCoordinator(new AppConfig(), TimeProvider.System);
        var music = new LiveActivityState("music", ActivityKind.Media, 65, "Track");
        var quota = new LiveActivityState("quota", ActivityKind.CustomWidget, 52, "AI 2/3", TargetTab: ExpandedTabId.AiQuota);
        coord.PublishLiveActivity(music, music.SourceId);
        coord.PublishLiveActivity(quota, quota.SourceId);
        coord.PublishLiveActivity(music with { Priority = 30 }, music.SourceId);

        Assert.Equal(IslandPresentationState.S2Split, coord.Current.State);
        Assert.Equal("music", coord.Current.PrimaryActivity?.SourceId);
        Assert.Equal("quota", coord.Current.SatelliteActivity?.SourceId);

        coord.PublishLiveActivity(new LiveActivityState("privacy", ActivityKind.PrivacySensor, 100, "Microphone"), "privacy");
        Assert.Equal("privacy", coord.Current.PrimaryActivity?.SourceId);
    }

    [Fact]
    public void IslandMotionState_QuickSplitReversalPreservesBothVisibleSurfaces()
    {
        var motion = new IslandMotionState();
        var split = new IslandLayoutTarget(IslandPresentationState.S2Split, 276, 36, 16, 96, 10, false, false);
        var expanded = split with
        {
            State = IslandPresentationState.S4Expanded,
            WidthDip = 620,
            HeightDip = 360,
            SatelliteWidthDip = 0,
            SatelliteGapDip = 0
        };
        motion.ApplyTarget(split, isExpanding: true);
        for (int i = 0; i < 120; i++) motion.StepAll(1.0 / 120.0);

        motion.ApplyTarget(expanded, isExpanding: true);
        for (int i = 0; i < 5; i++) motion.StepAll(1.0 / 240.0);
        double compactAlpha = motion.CompactContentOpacity * motion.CompactOverlayGate;
        double satelliteAlpha = motion.SatelliteOpacity;
        Assert.InRange(compactAlpha, 0.1, 0.99);
        Assert.InRange(satelliteAlpha, 0.1, 0.99);

        motion.ApplyTarget(split, isExpanding: false);
        Assert.Equal(compactAlpha, motion.CompactContentOpacity * motion.CompactOverlayGate, precision: 5);
        Assert.Equal(satelliteAlpha, motion.SatelliteOpacity, precision: 5);
        for (int i = 0; i < 120; i++) motion.StepAll(1.0 / 120.0);
        Assert.Equal(1.0, motion.SatelliteOpacity, precision: 3);
        Assert.True(motion.IsAllSettled);
    }

    [Fact]
    public void IslandMotionState_FadesCompactContentFromExpandedOrHiddenStates()
    {
        var motion = new IslandMotionState();
        var hidden = new IslandLayoutTarget(IslandPresentationState.S0Hidden, 0, 0, 0, 0, 0, true, false);
        var expanded = new IslandLayoutTarget(IslandPresentationState.S4Expanded, 620, 360, 28, 0, 0, false, false);
        var compact = new IslandLayoutTarget(IslandPresentationState.S2Compact, 224, 32, 16, 0, 0, false, false);

        motion.ApplyTarget(hidden, isExpanding: false);
        for (int i = 0; i < 180; i++)
        {
            motion.StepAll(1.0 / 120.0);
        }
        motion.ApplyTarget(compact, isExpanding: true);
        Assert.Equal(0.0, motion.CompactContentReveal.Value, precision: 3);
        Assert.Equal(1.0, motion.CompactContentReveal.Target, precision: 3);

        motion.ApplyTarget(expanded, isExpanding: true);
        for (int i = 0; i < 240; i++)
        {
            motion.StepAll(1.0 / 120.0);
        }
        motion.ApplyTarget(compact, isExpanding: false);
        Assert.Equal(0.0, motion.CompactContentReveal.Value, precision: 3);
        Assert.Equal(1.0, motion.CompactContentReveal.Target, precision: 3);
    }

    [Fact]
    public void IslandMotionState_EasedMorph_NoOvershoot_SettlesOnTime_AndRetargetsWithoutJumps()
    {
        static double EaseOutCubic(double t) => 1.0 - Math.Pow(1.0 - t, 3.0);
        var motion = new IslandMotionState
        {
            MorphEnterEase = EaseOutCubic,
            MorphExitEase = t => t * t * (3.0 - 2.0 * t),
            MorphEnterSeconds = 0.52,
            MorphExitSeconds = 0.36
        };
        var compact = new IslandLayoutTarget(IslandPresentationState.S2Compact, 224, 32, 16, 0, 0, false, false);
        var expanded = new IslandLayoutTarget(IslandPresentationState.S4Expanded, 616, 280, 24, 0, 0, false, false);

        motion.ApplyTarget(compact, isExpanding: false);
        for (int i = 0; i < 120; i++) motion.StepAll(1.0 / 60.0);
        Assert.True(motion.IsAllSettled);

        motion.ApplyTarget(expanded, isExpanding: true);
        double maxW = 0, prevW = motion.Width.Value;
        for (int i = 0; i < 18; i++) // 300 ms in
        {
            motion.StepAll(1.0 / 60.0);
            maxW = Math.Max(maxW, motion.Width.Value);
            Assert.True(motion.Width.Value >= prevW - 1e-9, "width must grow monotonically");
            prevW = motion.Width.Value;
        }

        // Interrupt mid-flight: the collapse starts exactly where the open was, with no jump.
        double before = motion.Width.Value;
        motion.ApplyTarget(compact, isExpanding: false);
        motion.StepAll(1.0 / 240.0);
        Assert.True(Math.Abs(motion.Width.Value - before) < 6.0, $"retarget jumped {before} -> {motion.Width.Value}");

        for (int i = 0; i < 30; i++) motion.StepAll(1.0 / 60.0); // 500 ms > 360 ms collapse
        Assert.True(motion.IsAllSettled);
        Assert.Equal(224.0, motion.Width.Value, precision: 3);
        Assert.Equal(1.0, motion.CompactContentReveal.Value, precision: 3);
        Assert.Equal(0.0, motion.ContentReveal.Value, precision: 3);

        motion.ApplyTarget(expanded, isExpanding: true);
        for (int i = 0; i < 34; i++)
        {
            motion.StepAll(1.0 / 60.0);
            Assert.True(motion.Width.Value <= 616.0 + 1e-9 && motion.Height.Value <= 280.0 + 1e-9, "no overshoot");
        }
        Assert.True(motion.IsAllSettled);
        Assert.Equal(616.0, motion.Width.Value, precision: 3);
        Assert.Equal(1.0, motion.ContentRevealTier3, precision: 3);
        Assert.True(maxW > 224.0);
    }
}
