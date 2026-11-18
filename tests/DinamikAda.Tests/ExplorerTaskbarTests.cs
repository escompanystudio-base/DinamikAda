using DinamikAda.Core.Config;
using DinamikAda.Platform.Win32.Windowing;
using DinamikAda.Platform.Win32.Interop;
using Xunit;

namespace DinamikAda.Tests;

public sealed class ExplorerTaskbarTests
{
    [Fact]
    public void ExplorerModeSuppressesCustomDockWithoutDestroyingPreferences()
    {
        var config = new AppConfig { EnableBottomDock = true, HideWindowsTaskbar = true, UseExplorerTaskbar = true };
        config.Sanitize();
        Assert.False(config.UseCustomBottomDock);
        config.UseExplorerTaskbar = false;
        Assert.True(config.UseCustomBottomDock);
        Assert.True(config.HideWindowsTaskbar);
    }

    [Theory]
    [InlineData("#abc123", "#ABC123")]
    [InlineData("#ABCDEF\n[Mod]\nInclude=*", "#0A84FF")]
    [InlineData("#12345Z", "#0A84FF")]
    [InlineData(null, "#0A84FF")]
    public void AccentCannotInjectNativeSettings(string? input, string expected)
    {
        Assert.Equal(expected, ExplorerTaskbarTheme.NormalizeAccent(input));
        string settings = ExplorerTaskbarTheme.Build(true, input, 100);
        Assert.Contains("Include=explorer.exe", settings);
        Assert.DoesNotContain("Include=*", settings);
        Assert.Contains($"Color=\"{expected}\"", settings);
    }

    [Fact]
    public void DisableUnloadsOnlyTheExplorerStyleMod()
    {
        string settings = ExplorerTaskbarTheme.Build(false, "#123456", 101);
        Assert.Contains("Disabled=1", settings);
        Assert.Contains("SettingsChangeTime=101", settings);
        Assert.DoesNotContain("windhawk.exe|", settings);
    }

    [Fact]
    public void ModeSurvivesConfigRoundTrip()
    {
        string directory = Path.Combine(Path.GetTempPath(), "dinamikada_native_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var store = new AppConfigStore(Path.Combine(directory, "config.json"));
            store.Save(new AppConfig { UseExplorerTaskbar = true, EnableBottomDock = true });
            var loaded = store.Load();
            Assert.True(loaded.UseExplorerTaskbar);
            Assert.False(loaded.UseCustomBottomDock);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void PlaybackAndMotionPreferencesReachNativeHostIndependently()
    {
        string playing = ExplorerTaskbarTheme.BuildAnimationState("#123456", "#789abc", "#def123", true, true, true);
        Assert.Contains("Playing=1", playing);
        Assert.Contains("Secondary=#789ABC", playing);
        Assert.Contains("Highlight=#DEF123", playing);
        Assert.Contains("HasSession=1", playing);
        string paused = ExplorerTaskbarTheme.BuildAnimationState(null, null, null, false, false, false, hasSession: true);
        Assert.Contains("Playing=0", paused);
        Assert.Contains("HasSession=1", paused);
        Assert.Contains("Magnification=0", paused);
        Assert.Contains("Glow=0", paused);
        Assert.DoesNotContain("[Mod]", playing);
        Assert.DoesNotContain("SettingsChangeTime", playing);
    }

    [Fact]
    public void SkinHidesIconFillAndScopesMusicGlassToNativeShellSurfaces()
    {
        string dock = ExplorerTaskbarTheme.Build(true, null, 110);
        Assert.Contains("Grid#IconPanel > Border#BackgroundElement", dock);
        Assert.Contains("controlStyles[4].styles[0]=Opacity=0", dock);
        Assert.Contains("Rectangle#RunningIndicator", dock);
        Assert.Contains("Border#OverflowFlyoutBackgroundBorder", dock);

        string start = ExplorerTaskbarTheme.BuildStartMenuSettings(true, 110);
        Assert.Contains("Include=StartMenuExperienceHost.exe", start);
        Assert.DoesNotContain("SearchHost", start);
        Assert.Contains("Border#AcrylicBorder", start);
        Assert.Contains("Disabled=1", ExplorerTaskbarTheme.BuildStartMenuSettings(false, 111));

        string notification = ExplorerTaskbarTheme.BuildNotificationSettings(true, 110);
        Assert.Contains("Include=ShellExperienceHost.exe|ShellHost.exe", notification);
        Assert.Contains("Disabled=0", notification);
        Assert.Contains("Disabled=1", ExplorerTaskbarTheme.BuildNotificationSettings(false, 111));
    }

    [Fact]
    public void CompactGlassDockKeepsSizingSmallAndReversible()
    {
        string active = ExplorerTaskbarTheme.BuildSizeSettings(true, 102);
        Assert.Contains("TaskbarHeight=52", active);
        Assert.Contains("IconSize=26", active);
        Assert.Contains("TaskbarButtonWidth=38", active);
        Assert.Contains("Disabled=0", active);
        Assert.Contains("Disabled=1", ExplorerTaskbarTheme.BuildSizeSettings(false, 103));
    }

    [Fact]
    public void FloatingDockReleasesTheBandAndKeepsTheCapsulesInteractive()
    {
        Assert.Contains("clickThroughTaskbar=1", ExplorerTaskbarTheme.Build(true, null, 104));
        string overlay = ExplorerTaskbarTheme.BuildOverlaySettings(true, 104);
        Assert.Contains("mode=never", overlay);
        Assert.Contains("primaryMonitorOnly=0", overlay);
        Assert.Contains("Disabled=0", overlay);
        Assert.Contains("Disabled=1", ExplorerTaskbarTheme.BuildOverlaySettings(false, 105));
    }

    [Fact]
    public void DockVisibilityPreferenceDoesNotHideTheSystemTray()
    {
        Assert.Contains("SmartHide=1", ExplorerTaskbarTheme.BuildAnimationState(null, null, null, false, true, false, true));
        Assert.Contains("SmartHide=0", ExplorerTaskbarTheme.BuildAnimationState(null, null, null, false, true, false, false));
        Assert.Contains("mode=never", ExplorerTaskbarTheme.BuildOverlaySettings(true, 106));
    }

    [Theory]
    [InlineData(2, true)]
    [InlineData(52, false)]
    [InlineData(0, false)]
    public void OnlyTheActivationStripIsReleased(int reserved, bool expected)
    {
        var monitor = new NativeMethods.RECT { Right = 1920, Bottom = 1080 };
        var work = new NativeMethods.RECT { Top = 20, Right = 1900, Bottom = 1080 - reserved };
        Assert.Equal(expected, ExplorerWorkAreaPolicy.TryReleaseActivationStrip(monitor, work, 96, out var result));
        Assert.Equal(work.Top, result.Top);
        Assert.Equal(work.Right, result.Right);
        Assert.Equal(expected ? 1080 : work.Bottom, result.Bottom);
    }
}
