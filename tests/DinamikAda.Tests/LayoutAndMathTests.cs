using DinamikAda.Core.Abstractions;
using DinamikAda.Core.Config;
using DinamikAda.Core.Localization;
using DinamikAda.Core.MathAndSearch;
using DinamikAda.Platform.Win32.Windowing;
using Xunit;

namespace DinamikAda.Tests;

public sealed class LayoutAndMathTests
{
    [Fact]
    public void MonitorLayoutCalculator_SupportsMixedDpiAndNegativeCoordinates()
    {
        // Secondary left monitor at negative origin (-1920, 0) @ 150% DPI
        var leftMon = new MonitorDescriptor(
            Handle: 2,
            DeviceName: @"\\.\DISPLAY2",
            FriendlyName: "Side Monitor",
            IsPrimary: false,
            LeftPx: -1920,
            TopPx: 0,
            WidthPx: 1920,
            HeightPx: 1080,
            DpiScale: 1.5);

        var place = MonitorLayoutCalculator.ComputeHostWindowBounds(
            leftMon,
            hostWidthDip: 340,
            hostHeightDip: 72,
            HorizontalAnchorDetent.Center,
            customRatio: 0.5,
            DockPlacementMode.FloatingTop,
            freeTopOffsetDip: 8.0);

        Assert.Equal((int)Math.Round(340 * 1.5), place.WidthPx);
        Assert.Equal((int)Math.Round(72 * 1.5), place.HeightPx);
        Assert.True(place.LeftPx < 0);
        Assert.True(place.LeftPx >= leftMon.LeftPx);
        Assert.True(place.LeftPx + place.WidthPx <= leftMon.LeftPx + leftMon.WidthPx);
    }

    [Fact]
    public void MonitorLayoutCalculator_SnapsHorizontalDetentsCorrectly()
    {
        var leftSnap = MonitorLayoutCalculator.SnapHorizontalDetent(centerXDip: 540, monitorWidthDip: 1920);
        Assert.Equal(HorizontalAnchorDetent.LeftSafe, leftSnap.Detent);

        var centerSnap = MonitorLayoutCalculator.SnapHorizontalDetent(centerXDip: 955, monitorWidthDip: 1920);
        Assert.Equal(HorizontalAnchorDetent.Center, centerSnap.Detent);

        var rightSnap = MonitorLayoutCalculator.SnapHorizontalDetent(centerXDip: 1380, monitorWidthDip: 1920);
        Assert.Equal(HorizontalAnchorDetent.RightSafe, rightSnap.Detent);
    }

    [Fact]
    public void QuickExpressionEvaluator_EvaluatesMathHexBinaryAndUnits()
    {
        Assert.True(QuickExpressionEvaluator.TryEvaluate("= (12 + 8) * 3 - sqrt(16)", out var fmt1, out var raw1));
        Assert.Equal("56", raw1);
        Assert.Contains("56", fmt1);

        Assert.True(QuickExpressionEvaluator.TryEvaluate("= 200 * 15%", out _, out var rawPct));
        Assert.Equal("30", rawPct);

        Assert.True(QuickExpressionEvaluator.TryEvaluate("= 0xFF", out var fmtHex, out var rawHex));
        Assert.Equal("255", rawHex);
        Assert.Contains("255", fmtHex);

        Assert.True(QuickExpressionEvaluator.TryEvaluate("100 km to mi", out var fmtUnit, out var rawUnit));
        Assert.Contains("mi", fmtUnit);
        Assert.StartsWith("62.", rawUnit);
    }

    [Fact]
    public void FuzzyMatcher_RanksPrefixHigherAndHandlesTurkishI()
    {
        int exactOrPrefix = FuzzyMatcher.Score("sıs", "Sistem Denetimi");
        int nonMatch = FuzzyMatcher.Score("xyz", "Sistem Denetimi");

        Assert.True(exactOrPrefix > 0);
        Assert.Equal(0, nonMatch);

        int spotifyMatch = FuzzyMatcher.Score("spo", "Spotify");
        int weakSubseq = FuzzyMatcher.Score("spy", "Spotify");
        Assert.True(spotifyMatch > weakSubseq);
    }

    [Fact]
    public void AppConfigStore_PersistsAndRecoversRoundTrip()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"dinamikada_cfg_{Guid.NewGuid():N}");
        try
        {
            var store = new AppConfigStore(Path.Combine(tempDir, "config.json"));
            var cfg = new AppConfig
            {
                Language = "en",
                HorizontalDetent = HorizontalAnchorDetent.RightSafe,
                GameFullscreenPolicy = FullscreenPolicy.KeepCompact,
                CommandHotkey = "Alt+Space",
                FullscreenWhitelistExes = new List<string> { "notepad" }
            };

            store.Save(cfg);
            var loaded = store.Load();

            Assert.Equal("en", loaded.Language);
            Assert.Equal(HorizontalAnchorDetent.RightSafe, loaded.HorizontalDetent);
            Assert.Equal(FullscreenPolicy.KeepCompact, loaded.GameFullscreenPolicy);
            Assert.Equal("Alt+Space", loaded.CommandHotkey);
            Assert.Contains("notepad", loaded.FullscreenWhitelistExes);
            Assert.True(loaded.UseHardwareRendering);
            Assert.Equal(0, loaded.TargetFpsCap);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void AppConfigStore_UpdateKeepsSettingsAndAccountsIntactWhenWriteFails()
    {
        string blockedPath = Path.Combine(Path.GetTempPath(), $"dinamikada-blocked-{Guid.NewGuid():N}");
        File.WriteAllText(blockedPath, "blocked parent");
        try
        {
            var config = new AppConfig { CustomFreeTopDip = 780 };
            config.AiQuotaSlots.Add(new AiQuotaSlotConfig { Id = "saved", AccountIdentifier = "studio@example.com" });
            var store = new AppConfigStore(Path.Combine(blockedPath, "config.json"));

            Assert.ThrowsAny<IOException>(() => store.Update(config, candidate =>
            {
                candidate.Language = "en";
                candidate.CustomFreeTopDip = 1000;
            }));

            Assert.Equal("tr", config.Language);
            Assert.Equal(780, config.CustomFreeTopDip);
            Assert.Equal("studio@example.com", Assert.Single(config.AiQuotaSlots).AccountIdentifier);
        }
        finally
        {
            File.Delete(blockedPath);
        }
    }

    [Fact]
    public void AppConfigStore_LoadResilientFallback_PreservesValidPropertiesWhenOneEnumIsInvalid()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"dinamikada_cfg_bad_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string path = Path.Combine(tempDir, "config.json");
        try
        {
            File.WriteAllText(path, """
            {
              "Language": "en",
              "TargetFpsCap": 144,
              "DockPlacement": "FutureRemovedMode",
              "CommandHotkey": "Alt+Space"
            }
            """);

            var store = new AppConfigStore(path);
            var loaded = store.Load();

            Assert.Equal("en", loaded.Language);
            Assert.Equal(144, loaded.TargetFpsCap);
            Assert.Equal("Alt+Space", loaded.CommandHotkey);
            Assert.Equal(DockPlacementMode.NotchFlush, loaded.DockPlacement);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void QuickExpressionEvaluator_HandlesModuloMinusScientificNotationAndRepeatedUnits()
    {
        Assert.True(QuickExpressionEvaluator.TryEvaluate("= 50 % -3", out _, out var modNeg));
        Assert.Equal("2", modNeg);

        Assert.True(QuickExpressionEvaluator.TryEvaluate("= 50% - 3", out _, out var pctSub));
        Assert.Equal("-2.5", pctSub);

        Assert.True(QuickExpressionEvaluator.TryEvaluate("= 1e3 + 250", out _, out var sci));
        Assert.Equal("1250", sci);

        Assert.True(QuickExpressionEvaluator.TryEvaluate("10 in in cm", out var fmtIn, out var rawIn));
        Assert.Contains("cm", fmtIn);
        Assert.StartsWith("25.4", rawIn);
    }

    [Fact]
    public void FuzzyMatcher_KeepsPositiveScoreOnLongTargetsAndMultiWordQueries()
    {
        string longTarget = "MicroSoftEdgeWebView2UltraLongEnterpriseWorkspaceLauncherUtilityConfigurationModule";
        int prefixScore = FuzzyMatcher.Score("micro", longTarget);
        Assert.True(prefixScore > 0);

        int multiWord = FuzzyMatcher.Score("ses ayar", "Ses ve Görüntü Ayarları");
        Assert.True(multiWord > 0);
    }

    [Fact]
    public void AppConfig_DefaultsToNotchFlushAndStandardScalePreset()
    {
        var cfg = new AppConfig();
        Assert.Equal(DockPlacementMode.NotchFlush, cfg.DockPlacement);
        Assert.Equal(NotchScalePreset.Standard, cfg.NotchScalePreset);
        Assert.True(cfg.DormantCameraLensEnabled);
        Assert.True(cfg.DynamicAlbumGlowEnabled);
        Assert.False(cfg.ShowMicPrivacyAlert);

        Assert.Equal(0.92, AppConfig.GetScaleFactor(NotchScalePreset.Compact), precision: 2);
        Assert.Equal(1.00, AppConfig.GetScaleFactor(NotchScalePreset.Standard), precision: 2);
        Assert.Equal(1.06, AppConfig.GetScaleFactor(NotchScalePreset.Wide), precision: 2);

        cfg.DockPlacement = (DockPlacementMode)999;
        cfg.NotchScalePreset = (NotchScalePreset)999;
        cfg.CustomHorizontalRatio = 0.01;
        cfg.Sanitize();

        Assert.Equal(DockPlacementMode.NotchFlush, cfg.DockPlacement);
        Assert.Equal(NotchScalePreset.Standard, cfg.NotchScalePreset);
        Assert.Equal(0.08, cfg.CustomHorizontalRatio, precision: 2);

        cfg.CustomHorizontalRatio = 0.99;
        cfg.Sanitize();
        Assert.Equal(0.92, cfg.CustomHorizontalRatio, precision: 2);
    }

    [Fact]
    public void MonitorLayoutCalculator_NotchFlushAlignsToMonitorTopPxAndEvenWidthPx()
    {
        var mon = new MonitorDescriptor(
            Handle: 3,
            DeviceName: @"\\.\DISPLAY3",
            FriendlyName: "Top Mixed DPI",
            IsPrimary: false,
            LeftPx: 0,
            TopPx: -37,
            WidthPx: 2560,
            HeightPx: 1440,
            DpiScale: 1.25);

        var oddFlush = MonitorLayoutCalculator.ComputeHostWindowBounds(
            mon,
            hostWidthDip: 175,
            hostHeightDip: 80,
            HorizontalAnchorDetent.Center,
            customRatio: 0.5,
            DockPlacementMode.NotchFlush,
            freeTopOffsetDip: 12.0);

        Assert.Equal(-37, oddFlush.TopPx);
        Assert.Equal(0, oddFlush.WidthPx & 1);
        Assert.Equal(220, oddFlush.WidthPx);

        var leftEdge = MonitorLayoutCalculator.ComputeHostWindowBounds(
            mon,
            hostWidthDip: 560,
            hostHeightDip: 80,
            HorizontalAnchorDetent.Custom,
            customRatio: 0.0,
            DockPlacementMode.NotchFlush,
            freeTopOffsetDip: 0.0);
        Assert.Equal(mon.LeftPx, leftEdge.LeftPx);

        var rightEdge = MonitorLayoutCalculator.ComputeHostWindowBounds(
            mon,
            hostWidthDip: 560,
            hostHeightDip: 80,
            HorizontalAnchorDetent.Custom,
            customRatio: 1.0,
            DockPlacementMode.NotchFlush,
            freeTopOffsetDip: 0.0);
        Assert.Equal(mon.LeftPx + mon.WidthPx, rightEdge.LeftPx + rightEdge.WidthPx);
    }

    [Fact]
    public void Strings_GetResolvesAllKeysInTurkishAndEnglish()
    {
        string[] keys =
        [
            "DockPlacementNotchFlush",
            "DockPlacementFloating",
            "DockPlacementFree",
            "DockPlacementLabel",
            "NotchScalePresetLabel",
            "NotchScaleCompact",
            "NotchScaleStandard",
            "NotchScaleWide",
            "DormantCameraLensLabel",
            "DynamicAlbumGlowLabel",
            "BezelPreviewLabel",
            "VerticalOffsetLabel",
            "FullscreenPolicyLabel",
            "EmptyShelf",
            "TimerReset",
            "AudioDeviceSwitchFailed",
            "PrevTrack",
            "PlayPauseTrack",
            "NextTrack",
            "ClearNotifications",
            "MediaSeek"
        ];

        foreach (string key in keys)
        {
            string tr = Strings.Get("tr", key);
            string en = Strings.Get("en", key);
            Assert.False(string.IsNullOrWhiteSpace(tr));
            Assert.False(string.IsNullOrWhiteSpace(en));
            Assert.NotEqual(key, tr);
            Assert.NotEqual(key, en);
        }
    }
}
