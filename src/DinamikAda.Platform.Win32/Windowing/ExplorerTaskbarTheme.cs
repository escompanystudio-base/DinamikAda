using System.Globalization;
using System.Text;

namespace DinamikAda.Platform.Win32.Windowing;

/// <summary>Settings for the GPL-licensed Windows 11 Taskbar Styler and DinamikAda animation extension.</summary>
public static class ExplorerTaskbarTheme
{
    public const string ModId = "windows-11-taskbar-styler";
    public const string LibraryFileName = "windows-11-taskbar-styler_1.11.2_dinamikada12.dll";
    public const string StartModId = "windows-11-start-menu-styler";
    public const string StartLibraryFileName = "windows-11-start-menu-styler_1.8_dinamikada.dll";
    public const string SizeModId = "taskbar-icon-size";
    public const string SizeLibraryFileName = "taskbar-icon-size_1.3.11_dinamikada.dll";
    public const string OverlayModId = "taskbar-auto-hide-when-maximized";
    public const string OverlayLibraryFileName = "taskbar-auto-hide-when-maximized_1.2.6_dinamikada.dll";

    public static string NormalizeAccent(string? accent)
    {
        if (accent is not null && accent.Length == 7 && accent[0] == '#'
            && uint.TryParse(accent.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
            return accent.ToUpperInvariant();
        return "#0A84FF";
    }

    public static string Build(bool enabled, string? accent, long revision)
    {
        string rgb = NormalizeAccent(accent)[1..];
        var text = new StringBuilder();
        text.AppendLine("[Mod]")
            .AppendLine($"LibraryFileName={LibraryFileName}")
            .AppendLine($"Disabled={(enabled ? 0 : 1)}")
            .AppendLine("Include=explorer.exe")
            .AppendLine("Architecture=x86-64")
            .AppendLine("Version=1.11.2")
            .AppendLine($"SettingsChangeTime={revision}")
            .AppendLine("[Settings]")
            .AppendLine("theme=DockLike")
            .AppendLine("clickThroughTaskbar=1")
            .AppendLine("xamlDiagnosticsHandling=default")
            .AppendLine("styleConstants[0]=DockTopGap=4")
            .AppendLine("styleConstants[1]=DockBottomGap=6")
            .AppendLine("styleConstants[2]=DockBackground=<WindhawkBlur BlurAmount=\"12\" TintColor=\"#28242A35\" TintSaturation=\"1.2\" />");

        const string glassEdge = "BorderBrush:=<LinearGradientBrush StartPoint=\"0,0\" EndPoint=\"0,1\"><GradientStop Color=\"#60FFFFFF\" Offset=\"0\"/><GradientStop Color=\"#0DFFFFFF\" Offset=\"0.18\"/><GradientStop Color=\"#0DFFFFFF\" Offset=\"0.75\"/><GradientStop Color=\"#24FFFFFF\" Offset=\"1\"/></LinearGradientBrush>";

        (string Target, string[] Styles)[] controls =
        [
            ("Taskbar.TaskbarFrame > Grid#RootGrid > Taskbar.TaskbarBackground > Grid",
                ["CornerRadius=16", "BorderThickness=1", glassEdge]),
            ("StackPanel#SystemTrayFrameGrid, Grid#SystemTrayFrameGrid",
                ["CornerRadius=16", "Height=36", "VerticalAlignment=Center", "BorderThickness=0.7", "Margin=0,0,6,0", "Padding=6,0,6,0", glassEdge]),
            ("Taskbar.TaskListButtonPanel, Taskbar.TaskListLabeledButtonPanel",
                ["Width=38", "Height=42"]),
            ("Grid#IconPanel > Image#Icon, Taskbar.TaskListLabeledButtonPanel#IconPanel > Image#Icon",
                ["Width=26", "Height=26"]),
            ("Grid#IconPanel > Border#BackgroundElement, Taskbar.TaskListLabeledButtonPanel > Border#BackgroundElement, Taskbar.TaskListButtonPanel > Grid > Border#BackgroundElement, Taskbar.TaskListButtonPanel > Border#BackgroundElement",
                ["Opacity=0"]),
            ("Rectangle#RunningIndicator", ["Width=4", "Height=4", "RadiusX=2", "RadiusY=2", $"Fill:=<SolidColorBrush Color=\"#{rgb}\" />"]),
            ("Grid#SystemTrayFrameGrid", ["Padding=6,0,34,0"])
        ];
        for (int i = 0; i < controls.Length; i++)
        {
            text.AppendLine($"controlStyles[{i}].target={controls[i].Target}");
            for (int j = 0; j < controls[i].Styles.Length; j++)
                text.AppendLine($"controlStyles[{i}].styles[{j}]={controls[i].Styles[j]}");
        }
        return text.ToString();
    }

    public static string BuildSizeSettings(bool enabled, long revision) =>
        $"[Mod]\nLibraryFileName={SizeLibraryFileName}\nDisabled={(enabled ? 0 : 1)}\n" +
        $"Include=explorer.exe\nArchitecture=x86-64\nVersion=1.3.11\nSettingsChangeTime={revision}\n" +
        "[Settings]\nTaskbarHeight=52\nIconSize=26\nTaskbarButtonWidth=38\nIconSizeSmall=26\nTaskbarButtonWidthSmall=38\n";

    public static string BuildOverlaySettings(bool enabled, long revision) =>
        $"[Mod]\nLibraryFileName={OverlayLibraryFileName}\nDisabled={(enabled ? 0 : 1)}\n" +
        $"Include=explorer.exe\nArchitecture=x86-64\nVersion=1.2.6\nSettingsChangeTime={revision}\n" +
        "[Settings]\nmode=never\nforegroundWindowOnly=0\nprimaryMonitorOnly=0\noldTaskbarOnWin11=0\nexcludedPrograms[0]=\n";

    public static string BuildStartMenuSettings(bool enabled, long revision) =>
        $"[Mod]\nLibraryFileName={StartLibraryFileName}\nDisabled={(enabled ? 0 : 1)}\n" +
        $"Include=StartMenuExperienceHost.exe\nArchitecture=x86-64\nVersion=1.8\nSettingsChangeTime={revision}\n" +
        "[Settings]\ncontrolStyles[0].target=Border#AcrylicOverlay\n" +
        "controlStyles[0].styles[0]=Background=Transparent\n" +
        "controlStyles[1].target=Border#AcrylicBorder\n" +
        "controlStyles[1].styles[0]=Background:=<AcrylicBrush TintColor=\"#181A23\" TintOpacity=\"0.72\" FallbackColor=\"#FF181A23\" />\n";

    public static string BuildAnimationState(string? primary, string? secondary, string? highlight,
        bool playing, bool magnification, bool glow, bool smartHide = true, bool hasSession = false) =>
        $"[Animation]\nPrimary={NormalizeAccent(primary)}\nSecondary={NormalizeAccent(secondary)}\n" +
        $"Highlight={NormalizeAccent(highlight)}\nPlaying={(playing ? 1 : 0)}\nHasSession={(hasSession || playing ? 1 : 0)}\n" +
        $"Magnification={(magnification ? 1 : 0)}\nGlow={(glow ? 1 : 0)}\nSmartHide={(smartHide ? 1 : 0)}\n";
}
