using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DinamikAda.App.Services.Voice;
using DinamikAda.App.Views.Voice;
using DinamikAda.Core.Config;
using DinamikAda.Core.Localization;
using DinamikAda.Core.Voice;
using DinamikAda.Modules.Media;

namespace DinamikAda.App.Views;

/// <summary>Settings → Voice Assistant (built in code, saved through AssignFormValues; lists apply right away).</summary>
public partial class SettingsWindow
{
    private sealed record VoiceSettingsValues(
        bool Enabled, string Model, VoiceEngine Engine, string Language, bool Cleanup,
        string DictationChord, string AskChord, string CommandChord,
        bool MascotEnabled, string MascotName, string MascotColor, int EyeStyle, int Accessory, int Personality);

    private static readonly string[] MascotSwatches =
        ["#7C5CFF", "#3DB8FF", "#2FD69A", "#FFC23D", "#FF7A59", "#FF6FB5", "#9AA3B5", "#B7E34B"];

    private static readonly Regex HexColor = new("^#?[0-9A-Fa-f]{6}$", RegexOptions.Compiled);

    private bool _voiceSettingsLoaded;
    private VoiceSettingsValues? _voiceBaseline;
    private CheckBox? _chkVoiceEnabled, _chkVoiceCleanup, _chkMascotEnabled;
    private TextBox? _txtGeminiModel, _txtChordDictation, _txtChordAsk, _txtChordCommand, _txtMascotName, _txtMascotHex;
    private ComboBox? _cmbVoiceEngine, _cmbVoiceLanguage, _cmbMascotEyes, _cmbMascotAccessory, _cmbMascotPersonality;
    private PasswordBox? _pwdGeminiKey;
    private bool _clearGeminiKey;
    private MascotControl? _mascotPreview;
    private TextBlock? _mascotPreviewCaption;
    private string _mascotColor = "#7C5CFF";
    private readonly List<Button> _swatchButtons = new();
    private DispatcherTimer? _mascotCycle;
    private int _mascotCycleIndex;
    private CancellationTokenSource? _geminiTestCts;

    private static VoiceSettingsValues VoiceValuesFromConfig(AppConfig c)
    {
        var m = c.Mascot ?? new MascotSettings();
        return new VoiceSettingsValues(
            c.VoiceAssistantEnabled, c.GeminiModel, c.VoiceEngine, c.VoiceLanguage ?? string.Empty, c.DictationCleanup,
            c.DictationChord, c.AskChord, c.CommandChord,
            m.Enabled, m.Name, NormalizeHex(m.ColorHex) ?? "#7C5CFF", m.EyeStyle, m.Accessory, m.Personality);
    }

    private VoiceSettingsValues? ReadVoiceValuesFromForm()
    {
        if (!_voiceSettingsLoaded || _chkVoiceEnabled is null || _voiceBaseline is null) return null;
        var b = _voiceBaseline;
        return new VoiceSettingsValues(
            _chkVoiceEnabled.IsChecked == true,
            string.IsNullOrWhiteSpace(_txtGeminiModel!.Text) ? b.Model : _txtGeminiModel.Text.Trim(),
            (VoiceEngine)Math.Max(0, _cmbVoiceEngine!.SelectedIndex),
            (_cmbVoiceLanguage!.SelectedItem as ChoiceItem<string>)?.Value ?? b.Language,
            _chkVoiceCleanup!.IsChecked == true,
            HoldChordText.Normalize(_txtChordDictation!.Text, b.DictationChord),
            HoldChordText.Normalize(_txtChordAsk!.Text, b.AskChord),
            HoldChordText.Normalize(_txtChordCommand!.Text, b.CommandChord),
            _chkMascotEnabled!.IsChecked == true,
            string.IsNullOrWhiteSpace(_txtMascotName!.Text) ? "Ada" : _txtMascotName.Text.Trim(),
            _mascotColor,
            Math.Max(0, _cmbMascotEyes!.SelectedIndex),
            Math.Max(0, _cmbMascotAccessory!.SelectedIndex),
            Math.Max(0, _cmbMascotPersonality!.SelectedIndex));
    }

    private void LoadVoiceSettings()
    {
        _voiceBaseline = VoiceValuesFromConfig(_orch.Config);
        BuildVoiceSettings(_voiceBaseline);
        _voiceSettingsLoaded = true;
        Closed += (_, _) =>
        {
            _mascotCycle?.Stop();
            _geminiTestCts?.Cancel();
        };
    }

    /// <summary>Localizes the nav tab and header, and rebuilds the section from the form's current values.</summary>
    private void LocalizeVoiceSettings(string lang)
    {
        NavVoice.Content = Strings.Pick(lang, "Sesli Asistan", "Voice Assistant");
        HdrVoiceSection.Text = Strings.Pick(lang, "Sesli Asistan", "Voice Assistant");
        if (!_voiceSettingsLoaded) return;
        var values = ReadVoiceValuesFromForm();
        if (values is not null) BuildVoiceSettings(values);
    }

    /// <summary>Render audit: show the voice tab.</summary>
    internal void ShowVoiceSectionForAudit()
    {
        NavVoice.IsChecked = true;
        LocalizeVoiceSettings(_currentLang); // pick up audit sample data
    }

    /// <summary>Render audit: scroll the voice tab so the mascot card is at the top.</summary>
    internal void ScrollVoiceSectionForAudit(int card)
    {
        var cards = VoiceSettingsHost.Children.OfType<Border>().ToList();
        if (card < 0 || card >= cards.Count) return;
        var target = cards[card];
        var pos = target.TransformToAncestor(SettingsScroller).Transform(new Point(0, 0));
        SettingsScroller.ScrollToVerticalOffset(SettingsScroller.VerticalOffset + pos.Y - 8);
        SettingsScroller.UpdateLayout();
    }

    private void BuildVoiceSettings(VoiceSettingsValues v)
    {
        string lang = _currentLang;
        string T(string tr, string en) => Strings.Pick(lang, tr, en);
        var host = VoiceSettingsHost;
        host.Children.Clear();
        _swatchButtons.Clear();
        _mascotCycle?.Stop();

        Style? subStyle = TryFindResource("NotchSubtitleTextStyle") as Style;
        TextBlock Text(string text, double size = 0, double opacity = 1, bool bold = false)
        {
            var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
            if (subStyle is not null) t.Style = subStyle;
            if (size > 0) t.FontSize = size;
            t.Opacity = opacity;
            if (bold) t.FontWeight = FontWeights.SemiBold;
            return t;
        }

        StackPanel Card(string title, string glyph, Color tint, string? subtitle = null)
        {
            var border = new Border { Margin = new Thickness(0, 10, 0, 0), Padding = new Thickness(13, 11, 13, 12) };
            if (TryFindResource("InnerSubCardStyle") is Style cs) border.Style = cs;
            border.Padding = new Thickness(13, 11, 13, 12);
            border.CornerRadius = new CornerRadius(12);
            var stack = new StackPanel();
            var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            head.Children.Add(new Border
            {
                Width = 20, Height = 20, CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 0, 8, 0),
                Background = new SolidColorBrush(Color.FromArgb(0x2E, tint.R, tint.G, tint.B)),
                Child = new TextBlock
                {
                    Text = glyph, FontSize = 10.5,
                    FontFamily = TryFindResource("IconFont") as FontFamily ?? new FontFamily("Segoe Fluent Icons"),
                    Foreground = new SolidColorBrush(tint),
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                }
            });
            var titleBlock = Text(title, 13, 1, bold: true);
            titleBlock.VerticalAlignment = VerticalAlignment.Center;
            if (TryFindResource("TextPrimaryBrush") is Brush primary) titleBlock.Foreground = primary;
            head.Children.Add(titleBlock);
            stack.Children.Add(head);
            if (subtitle is not null)
            {
                var s = Text(subtitle, 11.2, 0.72);
                s.Margin = new Thickness(0, 0, 0, 6);
                stack.Children.Add(s);
            }
            border.Child = stack;
            host.Children.Add(border);
            return stack;
        }

        Grid FormGrid(StackPanel parent)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star), MaxWidth = 200, MinWidth = 110 });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
            parent.Children.Add(g);
            return g;
        }

        void AddRow(Grid grid, string label, FrameworkElement control)
        {
            int row = grid.RowDefinitions.Count;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var lbl = Text(label);
            lbl.VerticalAlignment = VerticalAlignment.Center;
            lbl.Margin = new Thickness(0, 4, 8, 4);
            Grid.SetRow(lbl, row);
            Grid.SetRow(control, row);
            Grid.SetColumn(control, 1);
            control.Margin = new Thickness(0, 4, 0, 4);
            System.Windows.Automation.AutomationProperties.SetName(control, label);
            grid.Children.Add(lbl);
            grid.Children.Add(control);
        }

        ComboBox Combo(IEnumerable<object> items, int selected)
        {
            var c = new ComboBox { Height = 31 };
            if (TryFindResource("ObsidianComboBoxStyle") is Style s) c.Style = s;
            foreach (var i in items) c.Items.Add(i);
            c.SelectedIndex = Math.Clamp(selected, 0, Math.Max(0, c.Items.Count - 1));
            return c;
        }

        TextBox Box(string text, string? tip = null)
        {
            var t = new TextBox { Text = text, MinHeight = 32, ToolTip = tip };
            if (TryFindResource("ObsidianTextBoxStyle") is Style s) t.Style = s;
            return t;
        }

        CheckBox Toggle(string text, bool on)
        {
            var c = new CheckBox { Content = text, IsChecked = on, Margin = new Thickness(0, 4, 0, 4) };
            if (TryFindResource("IosToggleSwitchStyle") is Style s) c.Style = s;
            return c;
        }

        Button ActionButton(string text)
        {
            var b = new Button { Content = text, Height = 30, Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 8, 4) };
            if (TryFindResource("DarkActionButtonStyle") is Style s) b.Style = s;
            return b;
        }

        var purple = Color.FromRgb(0xBF, 0x8C, 0xFF);
        var cyan = Color.FromRgb(0x5A, 0xD2, 0xFF);
        var amber = Color.FromRgb(0xFF, 0xB8, 0x4D);
        var green = Color.FromRgb(0x30, 0xD1, 0x58);

        // ---- 1. Assistant + shortcuts ------------------------------------------------------------------
        var intro = Text(T("Bir kısayolu basılı tut, konuş ve bırak. Yapay zekâ kendi ücretsiz Gemini anahtarınla çalışır; anahtar yoksa dikte için Windows'un çevrimdışı tanıması kullanılır.",
                           "Hold a shortcut, speak, and let go. AI runs on your own free Gemini key; without one, dictation falls back to Windows' offline recognition."), 11.5, 0.78);
        intro.Margin = new Thickness(0, 2, 0, 4);
        host.Children.Add(intro);

        var basics = Card(T("Asistan", "Assistant"), "", purple);
        _chkVoiceEnabled = Toggle(T("Sesli asistanı etkinleştir", "Enable the voice assistant"), v.Enabled);
        basics.Children.Add(_chkVoiceEnabled);
        var chips = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        chips.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        chips.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        (string chord, string title, string desc, Color tint)[] modes =
        [
            (v.DictationChord, T("Dikte", "Dictate"), T("Konuştuğunu imlecin olduğu yere temiz bir metin olarak yazar.", "Types what you say, cleaned up, wherever the cursor is."), cyan),
            (v.AskChord, T("Sor", "Ask"), T("Basılı tutarken ekranda bir yeri daire içine al ve onunla ilgili soru sor.", "While holding, circle something on screen and ask about it."), purple),
            (v.CommandChord, T("Komut", "Command"), T("Uygulama aç, ara, görev zamanla, rutin kur, yapılacak ekle, zamanlayıcı başlat, müziği yönet.", "Open apps, search, schedule tasks, set routines, add todos, start timers, control media."), amber)
        ];
        foreach (var (chord, title, desc, tint) in modes)
        {
            int r = chips.RowDefinitions.Count;
            chips.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var chip = new Border
            {
                CornerRadius = new CornerRadius(8), Padding = new Thickness(9, 3, 9, 4), Margin = new Thickness(0, 3, 10, 3),
                VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left,
                Background = new SolidColorBrush(Color.FromArgb(0x24, tint.R, tint.G, tint.B)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, tint.R, tint.G, tint.B)), BorderThickness = new Thickness(1),
                Child = new TextBlock { Text = chord.Replace("+", " + "), FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(tint) }
            };
            Grid.SetRow(chip, r);
            chips.Children.Add(chip);
            var line = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 3), VerticalAlignment = VerticalAlignment.Top };
            if (subStyle is not null) line.Style = subStyle;
            line.FontSize = 11.5;
            line.Inlines.Add(new System.Windows.Documents.Run(title + "  ") { FontWeight = FontWeights.SemiBold });
            line.Inlines.Add(new System.Windows.Documents.Run(desc) { Foreground = new SolidColorBrush(Color.FromArgb(0xB8, 0xEE, 0xEC, 0xF6)) });
            Grid.SetRow(line, r);
            Grid.SetColumn(line, 1);
            chips.Children.Add(line);
        }
        basics.Children.Add(chips);

        // ---- 2. Gemini ---------------------------------------------------------------------------------
        bool hasKey = !string.IsNullOrWhiteSpace(_orch.Config.GeminiApiKeyProtected) && !_clearGeminiKey;
        var gem = Card("Gemini", "", cyan,
            T("Anahtar yalnızca bu bilgisayarda şifreli saklanır; istekler doğrudan Google'a gider.",
              "The key is stored encrypted on this PC only; requests go straight to Google."));
        var gemGrid = FormGrid(gem);
        _pwdGeminiKey = new PasswordBox { MinHeight = 32, ToolTip = T("aistudio.google.com üzerinden ücretsiz anahtar", "Free key from aistudio.google.com") };
        if (TryFindResource("ObsidianPasswordBoxStyle") is Style pw) _pwdGeminiKey.Style = pw;
        AddRow(gemGrid, hasKey ? T("API anahtarı (kayıtlı, değiştirmek için yaz)", "API key (saved, type to replace)") : T("Gemini API anahtarı", "Gemini API key"), _pwdGeminiKey);
        _txtGeminiModel = Box(v.Model, "gemini-2.5-flash");
        AddRow(gemGrid, T("Model", "Model"), _txtGeminiModel);

        var keyButtons = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        var getKey = ActionButton(T("Ücretsiz anahtar al ↗", "Get a free key ↗"));
        getKey.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo("https://aistudio.google.com/apikey") { UseShellExecute = true }); }
            catch (Exception ex) { ShowInlineError(T("Tarayıcı açılamadı: ", "Could not open the browser: ") + ex.Message); }
        };
        var test = ActionButton(T("Test et", "Test"));
        keyButtons.Children.Add(getKey);
        keyButtons.Children.Add(test);
        if (hasKey)
        {
            var remove = ActionButton(T("Anahtarı kaldır", "Remove key"));
            remove.Click += (_, _) =>
            {
                _clearGeminiKey = true;
                _pwdGeminiKey.Clear();
                LocalizeVoiceSettings(_currentLang);
            };
            keyButtons.Children.Add(remove);
        }
        else if (_clearGeminiKey)
        {
            var pending = Text(T("Anahtar kaydedince silinecek.", "The key will be removed when you save."), 11, 0.75);
            pending.VerticalAlignment = VerticalAlignment.Center;
            keyButtons.Children.Add(pending);
        }
        gem.Children.Add(keyButtons);
        var testStatus = Text(string.Empty, 11.5);
        testStatus.Visibility = Visibility.Collapsed;
        testStatus.Margin = new Thickness(0, 4, 0, 0);
        gem.Children.Add(testStatus);
        test.Click += async (_, _) => await RunGeminiTestAsync(test, testStatus, T);

        // ---- 3. Speech ---------------------------------------------------------------------------------
        var speech = Card(T("Konuşma", "Speech"), "", green);
        var speechGrid = FormGrid(speech);
        _cmbVoiceEngine = Combo([T("Otomatik (anahtar varsa Gemini)", "Automatic (Gemini when a key is set)"), "Gemini", T("Windows (çevrimdışı, yalnızca dikte)", "Windows (offline, dictation only)")], (int)v.Engine);
        AddRow(speechGrid, T("Tanıma motoru", "Recognition engine"), _cmbVoiceEngine);
        var langs = new List<ChoiceItem<string>> { new(string.Empty, T("Uygulama dili", "App language")) };
        var tags = new List<string> { "tr-TR", "en-US" };
        if (!_orch.IsSmokeOrAuditMode)
        {
            try { tags.AddRange(MicCaptureService.WindowsDictationLanguages()); }
            catch (Exception) { /* speech platform unavailable: keep the defaults */ }
        }
        if (!string.IsNullOrWhiteSpace(v.Language)) tags.Add(v.Language);
        foreach (var tag in tags.Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.OrdinalIgnoreCase))
            langs.Add(new ChoiceItem<string>(tag, LanguageLabel(tag)));
        _cmbVoiceLanguage = Combo(langs, Math.Max(0, langs.FindIndex(l => string.Equals(l.Value, v.Language, StringComparison.OrdinalIgnoreCase))));
        AddRow(speechGrid, T("Konuşma dili", "Speech language"), _cmbVoiceLanguage);
        _chkVoiceCleanup = Toggle(T("Dikteyi temizle (dolgu kelimeler, düzeltmeler, noktalama)", "Clean up dictation (fillers, self-corrections, punctuation)"), v.Cleanup);
        speech.Children.Add(_chkVoiceCleanup);

        // ---- 4. Shortcuts ------------------------------------------------------------------------------
        var keys = Card(T("Basılı tutma kısayolları", "Hold shortcuts"), "", amber,
            T("En az iki değiştirici tuş: Ctrl, Shift, Alt, Win (ör. Ctrl+Win).", "At least two modifier keys: Ctrl, Shift, Alt, Win (e.g. Ctrl+Win)."));
        var keyGrid = FormGrid(keys);
        _txtChordDictation = Box(v.DictationChord, "Ctrl+Win");
        _txtChordAsk = Box(v.AskChord, "Ctrl+Shift");
        _txtChordCommand = Box(v.CommandChord, "Alt+Win");
        AddRow(keyGrid, T("Dikte", "Dictate"), _txtChordDictation);
        AddRow(keyGrid, T("Sor", "Ask"), _txtChordAsk);
        AddRow(keyGrid, T("Komut", "Command"), _txtChordCommand);
        var chordError = Text(string.Empty, 11.5);
        chordError.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x9A, 0x8F));
        chordError.Visibility = Visibility.Collapsed;
        chordError.Margin = new Thickness(0, 4, 0, 0);
        keys.Children.Add(chordError);
        var chordBoxes = new[] { _txtChordDictation, _txtChordAsk, _txtChordCommand };
        foreach (var box in chordBoxes)
        {
            string lastValid = HoldChordText.Normalize(box.Text, box.Text);
            void Validate()
            {
                var parsed = HoldChordText.Parse(box.Text);
                string? error = null;
                if (parsed is null)
                    error = T($"“{box.Text.Trim()}” geçerli değil; en az iki değiştirici tuş gerekir. Önceki değer korundu.",
                              $"“{box.Text.Trim()}” isn't valid; it needs at least two modifier keys. Kept the previous value.");
                else if (chordBoxes.Any(o => o != box && HoldChordText.Parse(o.Text) == parsed))
                    error = T($"{HoldChordText.Format(parsed.Value)} başka bir mod için kullanılıyor. Önceki değer korundu.",
                              $"{HoldChordText.Format(parsed.Value)} is already used by another mode. Kept the previous value.");
                if (error is not null)
                {
                    box.Text = lastValid;
                    chordError.Text = error;
                    chordError.Visibility = Visibility.Visible;
                    return;
                }
                lastValid = HoldChordText.Format(parsed!.Value);
                box.Text = lastValid;
                chordError.Visibility = Visibility.Collapsed;
            }
            box.LostKeyboardFocus += (_, _) => Validate();
            box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Validate(); e.Handled = true; } };
        }

        // ---- 5. Mascot ---------------------------------------------------------------------------------
        var mascotCard = Card(T("Maskot", "Mascot"), "", Color.FromRgb(0xFF, 0x6F, 0xB5),
            T("Asistanın karakterini kişiselleştir. Ad ve kişilik, sesli yanıtların tonunu da belirler.",
              "Personalize the assistant's character. The name and personality also set the tone of spoken answers."));
        _chkMascotEnabled = Toggle(T("Maskotu göster", "Show the mascot"), v.MascotEnabled);
        mascotCard.Children.Add(_chkMascotEnabled);

        var mascotRow = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        mascotRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        mascotRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var stage = new Border
        {
            Width = 132, Height = 150, CornerRadius = new CornerRadius(14), Margin = new Thickness(0, 4, 14, 4),
            VerticalAlignment = VerticalAlignment.Top,
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x22, 255, 255, 255)), BorderThickness = new Thickness(1)
        };
        var stageGrid = new Grid();
        _mascotPreview = new MascotControl { Width = 112, Height = 112, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 6, 0, 0), AnimationsEnabled = !_orch.IsSmokeOrAuditMode };
        _mascotPreviewCaption = new TextBlock
        {
            FontSize = 11, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 9),
            Foreground = new SolidColorBrush(Color.FromArgb(0xD0, 0xF2, 0xF0, 0xFA))
        };
        stageGrid.Children.Add(_mascotPreview);
        stageGrid.Children.Add(_mascotPreviewCaption);
        stage.Child = stageGrid;
        mascotRow.Children.Add(stage);

        var slots = new StackPanel();
        Grid.SetColumn(slots, 1);
        mascotRow.Children.Add(slots);
        var slotGrid = FormGrid(slots);
        _txtMascotName = Box(v.MascotName, "Ada");
        _txtMascotName.MaxLength = 24;
        AddRow(slotGrid, T("Ad", "Name"), _txtMascotName);

        _mascotColor = NormalizeHex(v.MascotColor) ?? "#7C5CFF";
        var colorPanel = new StackPanel();
        var swatches = new WrapPanel { Margin = new Thickness(0, 2, 0, 4) };
        foreach (var hex in MascotSwatches)
        {
            var sw = new Button
            {
                Width = 24, Height = 24, Margin = new Thickness(0, 0, 6, 4), Cursor = Cursors.Hand, Tag = hex,
                Template = SwatchTemplate(), Background = new SolidColorBrush(MascotControl.ParseColor(hex)), ToolTip = hex
            };
            System.Windows.Automation.AutomationProperties.SetName(sw, hex);
            sw.Click += (_, _) => SetMascotColor(hex);
            _swatchButtons.Add(sw);
            swatches.Children.Add(sw);
        }
        colorPanel.Children.Add(swatches);
        _txtMascotHex = Box(_mascotColor, "#RRGGBB");
        _txtMascotHex.Width = 110;
        _txtMascotHex.HorizontalAlignment = HorizontalAlignment.Left;
        void CommitHex()
        {
            if (NormalizeHex(_txtMascotHex.Text) is { } ok) SetMascotColor(ok);
            else _txtMascotHex.Text = _mascotColor;
        }
        _txtMascotHex.LostKeyboardFocus += (_, _) => CommitHex();
        _txtMascotHex.KeyDown += (_, e) => { if (e.Key == Key.Enter) { CommitHex(); e.Handled = true; } };
        colorPanel.Children.Add(_txtMascotHex);
        AddRow(slotGrid, T("Renk", "Colour"), colorPanel);

        _cmbMascotEyes = Combo([T("Yuvarlak", "Round"), T("Uykulu", "Sleepy"), T("Mutlu", "Happy"), T("Pırıltılı", "Sparkle")], v.EyeStyle);
        AddRow(slotGrid, T("Gözler", "Eyes"), _cmbMascotEyes);
        _cmbMascotAccessory = Combo([T("Yok", "None"), T("Kulaklık", "Headphones"), T("Fiyonk", "Bow"), T("Anten", "Antenna"), T("Gözlük", "Glasses")], v.Accessory);
        AddRow(slotGrid, T("Aksesuar", "Accessory"), _cmbMascotAccessory);
        _cmbMascotPersonality = Combo([T("Samimi", "Friendly"), T("Kısa ve öz", "Concise"), T("Eğlenceli", "Playful")], v.Personality);
        AddRow(slotGrid, T("Kişilik", "Personality"), _cmbMascotPersonality);
        mascotCard.Children.Add(mascotRow);

        _txtMascotName.TextChanged += (_, _) => RefreshMascotPreview();
        _cmbMascotEyes.SelectionChanged += (_, _) => RefreshMascotPreview();
        _cmbMascotAccessory.SelectionChanged += (_, _) => RefreshMascotPreview();
        _chkMascotEnabled.Click += (_, _) => RefreshMascotPreview();
        RefreshMascotPreview();
        StartMascotCycle(stage);

        // ---- 6. Scheduled tasks & routines -------------------------------------------------------------
        var sched = Card(T("Zamanlanmış görevler ve rutinler", "Scheduled tasks & routines"), "", Color.FromRgb(0x64, 0xD2, 0xFF),
            T($"{v.CommandChord} basılı tutup söyle: “Yarın 10'da Amazon'u aç” ya da “Her sabah 9'da Unity ve Spotify'ı aç”.",
              $"Hold {v.CommandChord} and say: “Open Amazon tomorrow at 10” or “Every morning at 9 open Unity and Spotify”."));
        var lists = new StackPanel();
        sched.Children.Add(lists);
        FillVoiceLists(lists, T);

        // ---- 7. Speed ----------------------------------------------------------------------------------
        var speed = Card(T("Hız", "Speed"), "", green);
        var stats = _orch.Config.VoiceStats ?? new VoiceStats();
        double wpm = stats.SpeakingWpm;
        var speedRow = new DockPanel();
        var openTest = ActionButton(T("Hız testini aç", "Open speed test"));
        openTest.VerticalAlignment = VerticalAlignment.Center;
        openTest.Margin = new Thickness(10, 0, 0, 0);
        openTest.Click += (_, _) => SpeedTestWindow.Open(_orch);
        DockPanel.SetDock(openTest, Dock.Right);
        speedRow.Children.Add(openTest);
        var speedText = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var big = new TextBlock { FontSize = 26, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(0x7D, 0xE8, 0x9C)) };
        big.Inlines.Add(new System.Windows.Documents.Run(wpm > 0 ? wpm.ToString("0", CultureInfo.InvariantCulture) : "—"));
        big.Inlines.Add(new System.Windows.Documents.Run(T("  kelime/dk", "  words/min")) { FontSize = 12, FontWeight = FontWeights.Normal, Foreground = new SolidColorBrush(Color.FromArgb(0xB0, 0xEE, 0xEC, 0xF6)) });
        speedText.Children.Add(big);
        const double typicalTypingWpm = 40;
        string compare = wpm > 0
            ? T($"Ortalama klavye hızının (~{typicalTypingWpm:0} k/dk) yaklaşık {(wpm / typicalTypingWpm).ToString("0.0", CultureInfo.GetCultureInfo("tr-TR"))} katı · {stats.WordsDictated} kelime, {stats.Sessions} dikte",
                $"About {(wpm / typicalTypingWpm).ToString("0.0", CultureInfo.InvariantCulture)}× a typical typing speed (~{typicalTypingWpm:0} wpm) · {stats.WordsDictated} words, {stats.Sessions} dictations")
            : T("Henüz veri yok. Birkaç dikteden sonra konuşma hızın burada görünür.", "No data yet. Your speaking speed shows up here after a few dictations.");
        speedText.Children.Add(Text(compare, 11.2, 0.75));
        speedRow.Children.Add(speedText);
        speed.Children.Add(speedRow);
    }

    private async Task RunGeminiTestAsync(Button test, TextBlock status, Func<string, string, string> T)
    {
        void Show(string text, Color color)
        {
            status.Text = text;
            status.Foreground = new SolidColorBrush(color);
            status.Visibility = Visibility.Visible;
        }

        string typed = _pwdGeminiKey?.Password.Trim() ?? string.Empty;
        try
        {
            // The test runs against the saved config, so save a typed key (and model) first.
            if (typed.Length > 0)
            {
                string protectedKey = GeminiClient.ProtectKey(typed);
                _orch.ConfigStore.Update(_orch.Config, c => c.GeminiApiKeyProtected = protectedKey);
                _pwdGeminiKey!.Clear();
                _clearGeminiKey = false;
            }
            else if (_clearGeminiKey || GeminiClient.GetKey(_orch.Config) is null)
            {
                Show(T("Önce bir API anahtarı gir.", "Enter an API key first."), Color.FromRgb(0xFF, 0x9A, 0x8F));
                return;
            }
            string model = _txtGeminiModel?.Text.Trim() ?? string.Empty;
            if (model.Length > 0 && !string.Equals(model, _orch.Config.GeminiModel, StringComparison.Ordinal))
            {
                _orch.ConfigStore.Update(_orch.Config, c => c.GeminiModel = model);
                if (_voiceBaseline is not null) _voiceBaseline = _voiceBaseline with { Model = _orch.Config.GeminiModel };
            }
        }
        catch (Exception ex)
        {
            Show(T("Anahtar kaydedilemedi: ", "Could not save the key: ") + ex.Message, Color.FromRgb(0xFF, 0x9A, 0x8F));
            return;
        }

        _geminiTestCts?.Cancel();
        var cts = _geminiTestCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        test.IsEnabled = false;
        Show(T("Test ediliyor…", "Testing…"), Color.FromArgb(0xC0, 0xEE, 0xEC, 0xF6));
        try
        {
            await new GeminiClient(_orch).GenerateAsync(null, [GeminiPart.FromText("Reply with OK")], null, 0, 256, cts.Token);
            Show(T("✓ Bağlantı çalışıyor. Anahtar kaydedildi.", "✓ It works. The key is saved."), Color.FromRgb(0x7D, 0xE8, 0x9C));
        }
        catch (GeminiException ex)
        {
            Show("✗ " + ex.Message, Color.FromRgb(0xFF, 0x9A, 0x8F));
        }
        catch (OperationCanceledException)
        {
            if (!IsLoaded) return;
            Show(T("✗ Yanıt gelmedi (zaman aşımı).", "✗ No answer (timed out)."), Color.FromRgb(0xFF, 0x9A, 0x8F));
        }
        catch (Exception)
        {
            Show(T("✗ Test başarısız oldu.", "✗ The test failed."), Color.FromRgb(0xFF, 0x9A, 0x8F));
        }
        finally
        {
            test.IsEnabled = true;
        }
    }

    private void SetMascotColor(string hex)
    {
        _mascotColor = NormalizeHex(hex) ?? _mascotColor;
        if (_txtMascotHex is not null && !string.Equals(_txtMascotHex.Text, _mascotColor, StringComparison.OrdinalIgnoreCase))
            _txtMascotHex.Text = _mascotColor;
        RefreshMascotPreview();
    }

    private void RefreshMascotPreview()
    {
        if (_mascotPreview is null || _cmbMascotEyes is null || _cmbMascotAccessory is null || _txtMascotName is null) return;
        _mascotPreview.Apply(new MascotSettings
        {
            ColorHex = _mascotColor,
            EyeStyle = Math.Max(0, _cmbMascotEyes.SelectedIndex),
            Accessory = Math.Max(0, _cmbMascotAccessory.SelectedIndex),
            Name = _txtMascotName.Text
        });
        _mascotPreview.Opacity = _chkMascotEnabled?.IsChecked == true ? 1 : 0.35;
        foreach (var b in _swatchButtons)
            b.BorderBrush = string.Equals(b.Tag as string, _mascotColor, StringComparison.OrdinalIgnoreCase)
                ? Brushes.White : Brushes.Transparent;
        UpdateMascotCaption();
    }

    private void UpdateMascotCaption()
    {
        if (_mascotPreviewCaption is null || _mascotPreview is null) return;
        string name = string.IsNullOrWhiteSpace(_txtMascotName?.Text) ? "Ada" : _txtMascotName!.Text.Trim();
        string state = _mascotPreview.State switch
        {
            MascotState.Listening => Strings.Pick(_currentLang, "dinliyor", "listening"),
            MascotState.Working => Strings.Pick(_currentLang, "düşünüyor", "thinking"),
            MascotState.Talking => Strings.Pick(_currentLang, "anlatıyor", "talking"),
            MascotState.Error => Strings.Pick(_currentLang, "endişeli", "worried"),
            _ => Strings.Pick(_currentLang, "hazır", "ready")
        };
        _mascotPreviewCaption.Text = $"{name} · {state}";
    }

    private void StartMascotCycle(Border stage)
    {
        var tint = MascotControl.ParseColor(_mascotColor);
        stage.Background = new RadialGradientBrush(Color.FromArgb(0x40, tint.R, tint.G, tint.B), Color.FromArgb(0x08, 255, 255, 255))
        {
            Center = new Point(0.5, 0.42), GradientOrigin = new Point(0.5, 0.42), RadiusX = 0.7, RadiusY = 0.6
        };
        if (_orch.IsSmokeOrAuditMode || _mascotPreview is null) return;
        MascotState[] cycle = [MascotState.Idle, MascotState.Listening, MascotState.Working, MascotState.Talking, MascotState.Idle, MascotState.Error];
        _mascotCycle ??= new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2.6) };
        _mascotCycle.Tick -= OnMascotCycleTick;
        _mascotCycle.Tick += OnMascotCycleTick;
        _mascotCycle.Start();

        void OnMascotCycleTick(object? sender, EventArgs e)
        {
            if (_mascotPreview is null) return;
            _mascotCycleIndex = (_mascotCycleIndex + 1) % cycle.Length;
            _mascotPreview.State = cycle[_mascotCycleIndex];
            if (_mascotPreview.State == MascotState.Listening) { _mascotPreview.SetLevel(0.7); _mascotPreview.SetLevel(0.7); }
            var t = MascotControl.ParseColor(_mascotColor);
            stage.Background = new RadialGradientBrush(Color.FromArgb(0x40, t.R, t.G, t.B), Color.FromArgb(0x08, 255, 255, 255))
            {
                Center = new Point(0.5, 0.42), GradientOrigin = new Point(0.5, 0.42), RadiusX = 0.7, RadiusY = 0.6
            };
            UpdateMascotCaption();
        }
    }

    /// <summary>Scheduled tasks and routines from config; deletes and toggles apply right away with new lists.</summary>
    private void FillVoiceLists(StackPanel lists, Func<string, string, string> T)
    {
        lists.Children.Clear();
        Style? subStyle = TryFindResource("NotchSubtitleTextStyle") as Style;
        var culture = CultureInfo.GetCultureInfo(_currentLang == "en" ? "en-US" : "tr-TR");
        var cfg = _orch.Config;

        TextBlock Header(string text)
        {
            var h = new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 4), FontSize = 11.5, Opacity = 0.85 };
            if (subStyle is not null) { h.Style = subStyle; h.FontWeight = FontWeights.SemiBold; h.FontSize = 11.5; }
            return h;
        }

        Button Delete(string tip)
        {
            var b = new Button
            {
                Content = "", FontFamily = TryFindResource("IconFont") as FontFamily ?? new FontFamily("Segoe Fluent Icons"),
                FontSize = 11, Width = 30, Height = 28, Padding = new Thickness(0), ToolTip = tip, VerticalAlignment = VerticalAlignment.Center
            };
            if (TryFindResource("DarkActionButtonStyle") is Style s) b.Style = s;
            b.FontFamily = TryFindResource("IconFont") as FontFamily ?? new FontFamily("Segoe Fluent Icons");
            System.Windows.Automation.AutomationProperties.SetName(b, tip);
            return b;
        }

        Border Row(string title, string meta, bool faded, params FrameworkElement[] right)
        {
            var dock = new DockPanel();
            foreach (var r in right.Reverse())
            {
                r.Margin = new Thickness(8, 0, 0, 0);
                DockPanel.SetDock(r, Dock.Right);
                dock.Children.Add(r);
            }
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var t = new TextBlock { Text = title, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12.5 };
            if (subStyle is not null) t.Style = subStyle;
            t.FontSize = 12.5;
            if (TryFindResource("TextPrimaryBrush") is Brush p) t.Foreground = p;
            var m = new TextBlock { Text = meta, FontSize = 11, Opacity = 0.65, TextTrimming = TextTrimming.CharacterEllipsis };
            if (subStyle is not null) { m.Style = subStyle; m.FontSize = 11; }
            text.Children.Add(t);
            text.Children.Add(m);
            dock.Children.Add(text);
            return new Border
            {
                Child = dock, Padding = new Thickness(10, 6, 6, 6), Margin = new Thickness(0, 2, 0, 2), CornerRadius = new CornerRadius(9),
                Background = new SolidColorBrush(Color.FromArgb(0x0D, 255, 255, 255)), Opacity = faded ? 0.55 : 1
            };
        }

        void Fail(Exception ex) => ShowInlineError(T("Kaydedilemedi: ", "Could not save: ") + ex.Message);

        lists.Children.Add(Header(T("Zamanlanmış görevler", "Scheduled tasks")));
        var tasks = (cfg.ScheduledVoiceTasks ?? new()).OrderBy(t => t.Done).ThenBy(t => t.RunAtLocal).ToList();
        if (tasks.Count == 0) lists.Children.Add(EmptyLine(T("Henüz zamanlanmış görev yok.", "No scheduled tasks yet.")));
        foreach (var task in tasks)
        {
            string id = task.Id;
            var del = Delete(T("Görevi sil", "Delete task"));
            del.Click += (_, _) =>
            {
                try { _orch.ConfigStore.Update(_orch.Config, c => c.ScheduledVoiceTasks = (c.ScheduledVoiceTasks ?? new()).Where(t => t.Id != id).ToList()); }
                catch (Exception ex) { Fail(ex); return; }
                FillVoiceLists(lists, T);
            };
            string when = task.RunAtLocal.ToString(_currentLang == "en" ? "ddd, MMM d · HH:mm" : "d MMMM ddd · HH:mm", culture);
            if (task.Done) when += T("  ·  tamamlandı", "  ·  done");
            lists.Children.Add(Row(TitleOrAction(task.Title, task.Actions, T), when, task.Done, del));
        }

        lists.Children.Add(Header(T("Rutinler", "Routines")));
        var routines = (cfg.VoiceRoutines ?? new()).OrderBy(r => r.TimeOfDay, StringComparer.Ordinal).ToList();
        if (routines.Count == 0) lists.Children.Add(EmptyLine(T("Henüz rutin yok.", "No routines yet.")));
        foreach (var routine in routines)
        {
            string id = routine.Id;
            var toggle = new CheckBox { IsChecked = routine.Enabled, VerticalAlignment = VerticalAlignment.Center, ToolTip = T("Rutini aç/kapat", "Turn the routine on/off") };
            if (TryFindResource("IosToggleSwitchStyle") is Style ts) toggle.Style = ts;
            System.Windows.Automation.AutomationProperties.SetName(toggle, T("Rutini aç/kapat", "Turn the routine on/off"));
            toggle.Click += (_, _) =>
            {
                bool on = toggle.IsChecked == true;
                try
                {
                    _orch.ConfigStore.Update(_orch.Config, c => c.VoiceRoutines = (c.VoiceRoutines ?? new())
                        .Select(r => r.Id == id ? CloneRoutine(r, on) : r).ToList());
                }
                catch (Exception ex) { Fail(ex); toggle.IsChecked = !on; return; }
                FillVoiceLists(lists, T);
            };
            var del = Delete(T("Rutini sil", "Delete routine"));
            del.Click += (_, _) =>
            {
                try { _orch.ConfigStore.Update(_orch.Config, c => c.VoiceRoutines = (c.VoiceRoutines ?? new()).Where(r => r.Id != id).ToList()); }
                catch (Exception ex) { Fail(ex); return; }
                FillVoiceLists(lists, T);
            };
            var next = DinamikAda.Core.Voice.ScheduleMath.NextRunLocal(routine, DateTime.Now);
            string meta = $"{routine.TimeOfDay} · {DinamikAda.Core.Voice.ScheduleMath.DescribeDays(routine.DaysMask, _currentLang)}"
                + (next is { } n ? (_currentLang == "en" ? $" · next {n:ddd HH:mm}" : $" · sonraki {n.ToString("ddd HH:mm", System.Globalization.CultureInfo.GetCultureInfo("tr-TR"))}") : string.Empty);
            lists.Children.Add(Row(TitleOrAction(routine.Name, routine.Actions, T), meta, !routine.Enabled, toggle, del));
        }

        TextBlock EmptyLine(string text)
        {
            var e = new TextBlock { Text = text, Opacity = 0.6, FontSize = 11.5, Margin = new Thickness(2, 0, 0, 4), TextWrapping = TextWrapping.Wrap };
            if (subStyle is not null) { e.Style = subStyle; e.FontSize = 11.5; }
            return e;
        }
    }

    private static VoiceRoutine CloneRoutine(VoiceRoutine r, bool enabled) => new()
    {
        Id = r.Id,
        Name = r.Name,
        TimeOfDay = r.TimeOfDay,
        DaysMask = r.DaysMask,
        Actions = (r.Actions ?? new()).Select(a => a.Clone()).ToList(),
        Enabled = enabled,
        LastRunDate = r.LastRunDate
    };

    private static string TitleOrAction(string title, List<VoiceAction>? actions, Func<string, string, string> T)
    {
        if (!string.IsNullOrWhiteSpace(title)) return title.Trim();
        var a = actions?.FirstOrDefault();
        if (a is null) return T("Adsız", "Untitled");
        string what = string.IsNullOrWhiteSpace(a.Target) ? a.Query : a.Target;
        return a.Kind switch
        {
            VoiceActionKind.OpenApp or VoiceActionKind.OpenUrl => T($"{what} aç", $"Open {what}"),
            VoiceActionKind.WebSearch => T($"{a.Target}: {a.Query} ara", $"Search {a.Target} for {a.Query}"),
            VoiceActionKind.AddTodo => T($"Yapılacak: {a.Query}", $"Todo: {a.Query}"),
            VoiceActionKind.StartTimer => T($"{a.Query} dk zamanlayıcı", $"{a.Query} min timer"),
            VoiceActionKind.Notify => T($"Hatırlatma: {a.Query}", $"Reminder: {a.Query}"),
            _ => T("Medya kontrolü", "Media control")
        };
    }

    /// <summary>Local days text (bit 0 = Monday); kept private so this file does not depend on schedule helpers.</summary>
    private static string DescribeDaysFallback(int mask, string lang)
    {
        bool en = lang == "en";
        mask &= 0x7F;
        if (mask is 0 or 0x7F) return en ? "Every day" : "Her gün";
        if (mask == 0x1F) return en ? "Weekdays" : "Hafta içi";
        if (mask == 0x60) return en ? "Weekends" : "Hafta sonu";
        string[] names = en ? ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"] : ["Pzt", "Sal", "Çar", "Per", "Cum", "Cmt", "Paz"];
        return string.Join(", ", Enumerable.Range(0, 7).Where(i => (mask & (1 << i)) != 0).Select(i => names[i]));
    }

    private static string LanguageLabel(string tag)
    {
        try
        {
            var c = CultureInfo.GetCultureInfo(tag);
            string native = c.NativeName;
            return string.IsNullOrWhiteSpace(native) ? tag : $"{tag} · {CultureInfo.CurrentCulture.TextInfo.ToTitleCase(native)}";
        }
        catch (CultureNotFoundException)
        {
            return tag;
        }
    }

    private static string? NormalizeHex(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string t = text.Trim();
        if (!HexColor.IsMatch(t)) return null;
        return "#" + t.TrimStart('#').ToUpperInvariant();
    }

    private static ControlTemplate SwatchTemplate()
    {
        var ring = new FrameworkElementFactory(typeof(Border));
        ring.SetValue(Border.CornerRadiusProperty, new CornerRadius(12));
        ring.SetValue(Border.BorderThicknessProperty, new Thickness(2));
        ring.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        ring.SetValue(Border.PaddingProperty, new Thickness(2));
        var dot = new FrameworkElementFactory(typeof(Border));
        dot.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        dot.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        ring.AppendChild(dot);
        return new ControlTemplate(typeof(Button)) { VisualTree = ring };
    }

    /// <summary>Writes only the voice settings changed in this window (baseline vs form).</summary>
    private void ApplyVoiceSettings(AppConfig cfg)
    {
        var v = ReadVoiceValuesFromForm();
        var b = _voiceBaseline;
        if (v is null || b is null) return;

        if (v.Enabled != b.Enabled) cfg.VoiceAssistantEnabled = v.Enabled;
        if (!string.Equals(v.Model, b.Model, StringComparison.Ordinal)) cfg.GeminiModel = v.Model;
        if (v.Engine != b.Engine) cfg.VoiceEngine = v.Engine;
        if (!string.Equals(v.Language, b.Language, StringComparison.OrdinalIgnoreCase)) cfg.VoiceLanguage = v.Language;
        if (v.Cleanup != b.Cleanup) cfg.DictationCleanup = v.Cleanup;
        if (v.DictationChord != b.DictationChord) cfg.DictationChord = v.DictationChord;
        if (v.AskChord != b.AskChord) cfg.AskChord = v.AskChord;
        if (v.CommandChord != b.CommandChord) cfg.CommandChord = v.CommandChord;

        bool mascotChanged = v.MascotEnabled != b.MascotEnabled || v.MascotName != b.MascotName || v.MascotColor != b.MascotColor
            || v.EyeStyle != b.EyeStyle || v.Accessory != b.Accessory || v.Personality != b.Personality;
        if (mascotChanged)
        {
            var m = (cfg.Mascot ?? new MascotSettings()).Clone(); // new object: CopyForUpdate is shallow
            if (v.MascotEnabled != b.MascotEnabled) m.Enabled = v.MascotEnabled;
            if (v.MascotName != b.MascotName) m.Name = v.MascotName;
            if (v.MascotColor != b.MascotColor) m.ColorHex = v.MascotColor;
            if (v.EyeStyle != b.EyeStyle) m.EyeStyle = v.EyeStyle;
            if (v.Accessory != b.Accessory) m.Accessory = v.Accessory;
            if (v.Personality != b.Personality) m.Personality = v.Personality;
            cfg.Mascot = m;
        }

        string typedKey = _pwdGeminiKey?.Password.Trim() ?? string.Empty;
        if (typedKey.Length > 0) cfg.GeminiApiKeyProtected = GeminiClient.ProtectKey(typedKey);
        else if (_clearGeminiKey) cfg.GeminiApiKeyProtected = string.Empty;
    }
}
