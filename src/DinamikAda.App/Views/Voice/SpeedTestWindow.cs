using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using DinamikAda.App.Services;
using DinamikAda.App.Views.DockWidgets;
using DinamikAda.Core.Config;
using DinamikAda.Core.Voice;

namespace DinamikAda.App.Views.Voice;

/// <summary>
/// Typing vs speaking speed comparison: the user types a sample sentence (WPM and accuracy are measured),
/// then sees it next to their dictation speed with animated bars and a count-up headline.
/// </summary>
public sealed class SpeedTestWindow : Window
{
    private const double TypicalSpeakingWpm = 130;
    private const double BarWidth = 360;
    private static SpeedTestWindow? _current;

    private readonly AppOrchestrator _orch;
    private readonly DockWidgetPalette _p;
    private readonly Grid _stage = new();
    private readonly Random _rng = new();
    private string _sample = string.Empty;

    private string Lang => _orch.Config.Language;
    private string T(string tr, string en) => Lang == "en" ? en : tr;
    private CultureInfo Culture => Lang == "en" ? CultureInfo.GetCultureInfo("en-US") : CultureInfo.GetCultureInfo("tr-TR");

    /// <summary>Opens the speed test (or brings the open one to the front).</summary>
    public static void Open(AppOrchestrator orch)
    {
        if (_current is { IsLoaded: true })
        {
            _current.Activate();
            return;
        }
        var w = new SpeedTestWindow(orch);
        var owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(x => x.IsActive && x != w);
        if (owner is not null)
        {
            w.Owner = owner;
            w.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        _current = w;
        w.Closed += (_, _) => { if (_current == w) _current = null; };
        w.Show();
        w.Activate();
    }

    private SpeedTestWindow(AppOrchestrator orch)
    {
        _orch = orch;
        _p = DockWidgetPalette.Create(DockVisualStyle.Black, Color.FromRgb(0x0A, 0x84, 0xFF));

        Title = T("Hız testi", "Speed test");
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        ShowInTaskbar = false;
        Topmost = true;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

        try
        {
            Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/DinamikAda.App;component/Themes/ObsidianGlassTheme.xaml", UriKind.Absolute)
            });
        }
        catch (Exception ex)
        {
            CrashLogger.Log("Voice", ex, "Speed test theme could not be loaded.");
        }

        Content = BuildShell();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; Close(); }
        };
        Loaded += (_, _) =>
        {
            PlayOpenAnimation();
            ShowTypingStep();
        };
    }

    // ---- Shell ---------------------------------------------------------------------------------

    private FrameworkElement BuildShell()
    {
        var p = _p;
        var root = new Border
        {
            Width = 520,
            CornerRadius = new CornerRadius(22),
            Background = p.CardBackground,
            BorderBrush = p.CardBorder,
            BorderThickness = new Thickness(1),
            Margin = new Thickness(14),
            Padding = new Thickness(24, 20, 24, 22),
            Effect = new DropShadowEffect { BlurRadius = 28, ShadowDepth = 6, Opacity = 0.4 }
        };

        var dock = new DockPanel { LastChildFill = true };
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 16) };
        var close = W.IconButton(p, "", 30, Close, p.CardRowBackground);
        System.Windows.Automation.AutomationProperties.SetName(close, T("Kapat", "Close"));
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        var icon = new Border
        {
            Width = 34,
            Height = 34,
            CornerRadius = new CornerRadius(11),
            Background = W.Brush(p.Accent, 0x30),
            Child = W.Glyph(p, "", 16, p.AccentBrush),
            Margin = new Thickness(0, 0, 12, 0)
        };
        DockPanel.SetDock(icon, Dock.Left);
        header.Children.Add(icon);
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(W.Text(p, T("Hız testi", "Speed test"), 18, weight: FontWeights.SemiBold));
        titles.Children.Add(W.Text(p, T("Yazmak mı, konuşmak mı daha hızlı?", "Is typing or speaking faster?"), 12, p.SecondaryForeground, ui: false));
        header.Children.Add(titles);
        DockPanel.SetDock(header, Dock.Top);
        dock.Children.Add(header);
        dock.Children.Add(_stage);
        root.Child = dock;

        root.MouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource is TextBox) return;
            if (e.OriginalSource == root || e.OriginalSource == dock || e.OriginalSource == header || e.OriginalSource is TextBlock)
            {
                try { DragMove(); } catch (InvalidOperationException) { }
            }
        };
        return root;
    }

    private void SetStep(FrameworkElement content)
    {
        _stage.Children.Clear();
        var tt = new TranslateTransform(18, 0);
        content.RenderTransform = tt;
        content.Opacity = 0;
        _stage.Children.Add(content);
        var ease = Motion.Soft;
        tt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(18, 0, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease });
        content.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(240)));
    }

    private static TextBlock StepLabel(DockWidgetPalette p, string text)
    {
        var t = W.Text(p, text.ToUpperInvariant(), 11, p.AccentBrush, FontWeights.SemiBold);
        t.Margin = new Thickness(0, 0, 0, 8);
        return t;
    }

    // ---- Step 1: typing ------------------------------------------------------------------------

    private void ShowTypingStep()
    {
        var p = _p;
        _sample = DictationText.PickSample(Lang, _rng, _sample);
        var panel = new StackPanel();
        panel.Children.Add(StepLabel(p, T("1. Adım · Yazma", "Step 1 · Typing")));

        var hint = W.Text(p, T("Aşağıdaki cümleyi yaz. Süre ilk tuşla başlar; bitince Enter'a bas.",
            "Type the sentence below. The clock starts with your first key; press Enter when done."), 12.5, p.SecondaryForeground, ui: false);
        hint.TextWrapping = TextWrapping.Wrap;
        hint.TextTrimming = TextTrimming.None;
        hint.Margin = new Thickness(0, 0, 0, 12);
        panel.Children.Add(hint);

        var sampleText = W.Text(p, _sample, 16, weight: FontWeights.Medium, ui: false);
        sampleText.TextWrapping = TextWrapping.Wrap;
        sampleText.TextTrimming = TextTrimming.None;
        sampleText.LineHeight = 24;
        panel.Children.Add(new Border
        {
            Background = p.CardRowBackground,
            BorderBrush = p.TileBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(16, 14, 16, 14),
            Child = sampleText
        });

        var input = W.Input(p, T("Buraya yaz…", "Type here…"), double.NaN);
        input.HorizontalAlignment = HorizontalAlignment.Stretch;
        input.FontSize = 15;
        input.Padding = new Thickness(12, 9, 12, 9);
        input.Margin = new Thickness(0, 12, 0, 0);
        input.AcceptsReturn = false;
        // Pasting would make the test meaningless.
        DataObject.AddPastingHandler(input, (_, e) => e.CancelCommand());
        panel.Children.Add(input);

        // Progress strip: how much of the sentence has been typed.
        var progressTrack = new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = p.Track, Margin = new Thickness(0, 10, 0, 0) };
        var progressScale = new ScaleTransform(0, 1);
        var progressFill = new Border
        {
            CornerRadius = new CornerRadius(2),
            Background = p.AccentBrush,
            RenderTransformOrigin = new Point(0, 0.5),
            RenderTransform = progressScale
        };
        progressTrack.Child = progressFill;
        panel.Children.Add(progressTrack);

        var status = W.Text(p, T("Hazır olduğunda yazmaya başla", "Start typing when you're ready"), 12, p.TertiaryForeground, ui: false);
        status.Margin = new Thickness(0, 8, 0, 0);
        panel.Children.Add(status);

        var sw = new Stopwatch();
        bool done = false;
        void Finish()
        {
            if (done || !sw.IsRunning) return;
            done = true;
            sw.Stop();
            string typed = input.Text;
            int words = GeminiJson.CountWords(typed);
            double wpm = DictationText.Wpm(words, sw.Elapsed);
            double accuracy = DictationText.Accuracy(_sample, typed);
            ShowResultStep(wpm, accuracy);
        }

        input.TextChanged += (_, _) =>
        {
            if (done) return;
            if (!sw.IsRunning && input.Text.Length > 0) sw.Start();
            double frac = _sample.Length == 0 ? 0 : Math.Clamp((double)input.Text.Length / _sample.Length, 0, 1);
            progressScale.BeginAnimation(ScaleTransform.ScaleXProperty,
                new DoubleAnimation(frac, TimeSpan.FromMilliseconds(160)) { EasingFunction = Motion.Soft });
            if (sw.IsRunning)
            {
                double live = DictationText.Wpm(GeminiJson.CountWords(input.Text), sw.Elapsed);
                status.Text = T($"{live.ToString("0", Culture)} kelime/dk · Enter ile bitir", $"{live.ToString("0", Culture)} WPM · press Enter to finish");
            }
            if (DictationText.IsComplete(_sample, input.Text)) Finish();
        };
        input.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; Finish(); }
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        buttons.Children.Add(W.Button(p, T("Başka cümle", "Another sentence"), ShowTypingStep, ""));
        buttons.Children.Add(W.Button(p, T("Bitir", "Finish"), Finish, "", primary: true));
        panel.Children.Add(buttons);

        SetStep(panel);
        Dispatcher.BeginInvoke(() => { input.Focus(); Keyboard.Focus(input); }, System.Windows.Threading.DispatcherPriority.Input);
    }

    // ---- Step 2: comparison --------------------------------------------------------------------

    private void ShowResultStep(double typingWpm, double accuracy)
    {
        var p = _p;
        double measured = _orch.Config.VoiceStats?.SpeakingWpm ?? 0;
        bool hasStats = measured > 0;
        double speakingWpm = hasStats ? measured : TypicalSpeakingWpm;
        double ratio = DictationText.SpeedRatio(typingWpm, speakingWpm);

        var panel = new StackPanel();
        panel.Children.Add(StepLabel(p, T("2. Adım · Konuşma", "Step 2 · Speaking")));

        // Headline that counts up.
        var headline = new CountUpText
        {
            FontFamily = p.UiFont,
            FontSize = 34,
            FontWeight = FontWeights.Bold,
            Foreground = p.AccentBrush,
            Margin = new Thickness(0, 0, 0, 2)
        };
        if (ratio >= 1.05)
        {
            headline.Formatter = v => T($"×{v.ToString("0.0", Culture)} daha hızlı", $"{v.ToString("0.0", Culture)}× faster");
            panel.Children.Add(headline);
            headline.AnimateTo(ratio, 1100);
        }
        else
        {
            headline.Text = ratio > 0 ? T("Yazman çok hızlı!", "You type really fast!") : T("Sonuç yok", "No result");
            panel.Children.Add(headline);
            W.PopIn(headline);
        }

        var sub = W.Text(p, ratio >= 1.05
                ? T("Konuşarak yazmak, klavyeden bu kadar daha hızlı.", "Dictating is this much faster than your keyboard.")
                : T("Uzun metinlerde dikte yine de elini dinlendirir.", "Dictation still saves your hands on long texts."),
            13, p.SecondaryForeground, ui: false);
        sub.TextWrapping = TextWrapping.Wrap;
        sub.TextTrimming = TextTrimming.None;
        sub.Margin = new Thickness(0, 0, 0, 18);
        panel.Children.Add(sub);

        double max = Math.Max(Math.Max(typingWpm, speakingWpm), 1);
        panel.Children.Add(SpeedBar("", T("Yazma", "Typing"), typingWpm, typingWpm / max, W.Brush(Color.FromRgb(0x8E, 0x8E, 0x93)), 150,
            T($"%{accuracy.ToString("0", Culture)} doğruluk", $"{accuracy.ToString("0", Culture)}% accuracy")));
        panel.Children.Add(SpeedBar("", T("Konuşma", "Speaking"), speakingWpm, speakingWpm / max, p.AccentBrush, 380,
            hasStats ? T("dikte istatistiklerinden", "from your dictation stats") : T("ortalama", "typical")));

        if (!hasStats)
        {
            var info = W.Text(p, T(
                    "Henüz dikte verin yok, bu yüzden ortalama 130 kelime/dk kullanıldı. Ctrl+Win'e basılı tutup konuş, bırakınca metin imlecin olduğu yere yazılır; kendi hızın burada görünür.",
                    "No dictation data yet, so a typical 130 WPM is used. Hold Ctrl+Win and speak; when you let go the text is typed at your cursor and your own speed shows up here."),
                12, p.TertiaryForeground, ui: false);
            info.TextWrapping = TextWrapping.Wrap;
            info.TextTrimming = TextTrimming.None;
            panel.Children.Add(new Border
            {
                Background = p.CardRowBackground,
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(12, 10, 12, 10),
                Margin = new Thickness(0, 8, 0, 0),
                Child = info
            });
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        buttons.Children.Add(W.Button(p, T("Kapat", "Close"), Close));
        buttons.Children.Add(W.Button(p, T("Tekrar dene", "Try again"), ShowTypingStep, "", primary: true));
        panel.Children.Add(buttons);

        SetStep(panel);
    }

    private FrameworkElement SpeedBar(string glyph, string label, double wpm, double fraction, Brush fill, int delayMs, string note)
    {
        var p = _p;
        var col = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };

        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var value = new CountUpText
        {
            FontFamily = p.UiFont,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = p.Foreground,
            Formatter = v => T($"{v.ToString("0", Culture)} kelime/dk", $"{v.ToString("0", Culture)} WPM")
        };
        DockPanel.SetDock(value, Dock.Right);
        top.Children.Add(value);
        var g = W.Glyph(p, glyph, 13, p.SecondaryForeground);
        g.Margin = new Thickness(0, 0, 8, 0);
        DockPanel.SetDock(g, Dock.Left);
        top.Children.Add(g);
        top.Children.Add(W.Text(p, label, 13, weight: FontWeights.SemiBold));
        col.Children.Add(top);

        var scale = new ScaleTransform(0, 1);
        var bar = new Border
        {
            Width = BarWidth + 112,
            Height = 14,
            CornerRadius = new CornerRadius(7),
            Background = p.Track,
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = new Border
            {
                CornerRadius = new CornerRadius(7),
                Background = fill,
                RenderTransformOrigin = new Point(0, 0.5),
                RenderTransform = scale
            }
        };
        col.Children.Add(bar);

        var noteText = W.Text(p, note, 11.5, p.TertiaryForeground, ui: false);
        noteText.Margin = new Thickness(0, 5, 0, 0);
        col.Children.Add(noteText);

        var begin = TimeSpan.FromMilliseconds(delayMs);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0, Math.Clamp(fraction, 0.02, 1), TimeSpan.FromMilliseconds(900))
        {
            BeginTime = begin,
            EasingFunction = Motion.Soft
        });
        value.AnimateTo(wpm, 900, delayMs);
        return col;
    }

    private void PlayOpenAnimation()
    {
        if (Content is not FrameworkElement root) return;
        var st = new ScaleTransform(0.96, 0.96);
        root.RenderTransformOrigin = new Point(0.5, 0.5);
        root.RenderTransform = st;
        var ease = Motion.Soft;
        var dur = TimeSpan.FromMilliseconds(260);
        st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.96, 1, dur) { EasingFunction = ease });
        st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.96, 1, dur) { EasingFunction = ease });
        root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
    }

    /// <summary>A TextBlock whose number is driven by a WPF animation (no manual frame loop).</summary>
    private sealed class CountUpText : TextBlock
    {
        public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
            nameof(Value), typeof(double), typeof(CountUpText),
            new PropertyMetadata(0.0, (d, _) => ((CountUpText)d).Refresh()));

        public double Value
        {
            get => (double)GetValue(ValueProperty);
            set => SetValue(ValueProperty, value);
        }

        public Func<double, string>? Formatter { get; set; }

        public CountUpText()
        {
            VerticalAlignment = VerticalAlignment.Center;
            SnapsToDevicePixels = true;
        }

        public void AnimateTo(double target, int durationMs, int delayMs = 0)
        {
            Refresh();
            BeginAnimation(ValueProperty, new DoubleAnimation(0, target, TimeSpan.FromMilliseconds(durationMs))
            {
                BeginTime = TimeSpan.FromMilliseconds(delayMs),
                EasingFunction = Motion.Soft
            });
        }

        private void Refresh()
        {
            if (Formatter is not null) Text = Formatter(Value);
        }
    }
}
