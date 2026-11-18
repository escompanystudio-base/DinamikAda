using System.Windows;
using System.Windows.Controls;
using DinamikAda.App.Services.DockWidgets;
using DinamikAda.Core.Config;
using DinamikAda.Core.Localization;
using DinamikAda.Modules.Productivity;

namespace DinamikAda.App.Views;

/// <summary>Settings → Bottom Dock → widgets and appearance (built in code, saved through AssignFormValues).</summary>
public partial class SettingsWindow
{
    private sealed record WidgetSettingsValues(
        DockVisualStyle Style, DockScreenEdge Edge, int SizePreset, bool Clock24h, string City, bool Fahrenheit,
        string WorldZones, string WorldTime, string CurrencyFrom, string CurrencyTo, string Crypto, string Stocks,
        bool ScreenTime, int PomodoroFocus, int PomodoroBreak);

    private bool _widgetSettingsLoaded;
    private WidgetSettingsValues? _widgetBaseline;
    private ComboBox? _cmbDockStyle, _cmbDockEdge, _cmbDockSize;
    private CheckBox? _chkClock24h, _chkFahrenheit, _chkScreenTime;
    private TextBox? _txtCity, _txtWorldZones, _txtWorldTime, _txtCurFrom, _txtCurTo, _txtCrypto, _txtStocks, _txtPomFocus, _txtPomBreak;
    private PasswordBox? _pwdStockKey;
    private bool _clearStockKey;

    private static int SizePresetFor(int iconSize) => iconSize <= 30 ? 0 : iconSize <= 38 ? 1 : 2;
    private static int IconSizeForPreset(int preset) => preset switch { 0 => 28, 1 => 36, _ => 44 };

    private WidgetSettingsValues CurrentWidgetValuesFromConfig(AppConfig c) => new(
        c.DockStyle, c.DockEdge, SizePresetFor(c.DockIconSizeDip), c.DockClock24h, c.WeatherCity, c.WeatherFahrenheit,
        string.Join(", ", c.WorldClockZones), c.WorldTimeZone, c.CurrencyFrom, c.CurrencyTo, c.CryptoSymbol,
        string.Join(", ", c.StockSymbols), c.ScreenTimeEnabled, c.PomodoroFocusMinutes, c.PomodoroBreakMinutes);

    private WidgetSettingsValues? ReadWidgetValuesFromForm()
    {
        if (!_widgetSettingsLoaded || _cmbDockStyle is null) return null;
        return new WidgetSettingsValues(
            (DockVisualStyle)(_cmbDockStyle.SelectedIndex < 0 ? 0 : _cmbDockStyle.SelectedIndex),
            (DockScreenEdge)(_cmbDockEdge!.SelectedIndex < 0 ? 0 : _cmbDockEdge.SelectedIndex),
            _cmbDockSize!.SelectedIndex < 0 ? 1 : _cmbDockSize.SelectedIndex,
            _chkClock24h!.IsChecked == true,
            _txtCity!.Text.Trim(),
            _chkFahrenheit!.IsChecked == true,
            _txtWorldZones!.Text.Trim(),
            _txtWorldTime!.Text.Trim(),
            _txtCurFrom!.Text.Trim(),
            _txtCurTo!.Text.Trim(),
            _txtCrypto!.Text.Trim(),
            _txtStocks!.Text.Trim(),
            _chkScreenTime!.IsChecked == true,
            int.TryParse(_txtPomFocus!.Text, out var f) ? f : 25,
            int.TryParse(_txtPomBreak!.Text, out var b) ? b : 5);
    }

    private void LoadDockWidgetSettings()
    {
        _widgetBaseline = CurrentWidgetValuesFromConfig(_orch.Config);
        BuildDockWidgetSettings(_widgetBaseline);
        _widgetSettingsLoaded = true;
    }

    private void RebuildDockWidgetSettingsForLanguage()
    {
        var values = ReadWidgetValuesFromForm();
        if (values is null) return;
        BuildDockWidgetSettings(values);
    }

    private void BuildDockWidgetSettings(WidgetSettingsValues v)
    {
        string lang = _currentLang;
        string T(string tr, string en) => Strings.Pick(lang, tr, en);
        var host = DockWidgetSettingsHost;
        host.Children.Clear();

        var header = new TextBlock
        {
            Text = T("Dock Widget'ları ve Görünüm", "Dock Widgets & Appearance"),
            Margin = new Thickness(0, 18, 0, 6)
        };
        if (TryFindResource("NotchSubtitleTextStyle") is Style sub) header.Style = sub;
        header.FontWeight = FontWeights.SemiBold;
        host.Children.Add(header);

        var open = new Button { Content = T("Widget Seçiciyi Aç…", "Open Widget Picker…"), Height = 30, Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 0, 8), HorizontalAlignment = HorizontalAlignment.Left };
        if (TryFindResource("DarkActionButtonStyle") is Style btn) open.Style = btn;
        open.Click += (_, _) => _orch.RequestOpenWidgetPicker();
        host.Children.Add(open);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star), MaxWidth = 210, MinWidth = 120 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        int row = 0;

        void AddRow(string label, FrameworkElement control)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var lbl = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 8, 4) };
            if (TryFindResource("NotchSubtitleTextStyle") is Style s) lbl.Style = s;
            Grid.SetRow(lbl, row);
            Grid.SetRow(control, row);
            Grid.SetColumn(control, 1);
            control.Margin = new Thickness(0, 4, 0, 4);
            System.Windows.Automation.AutomationProperties.SetName(control, label);
            grid.Children.Add(lbl);
            grid.Children.Add(control);
            row++;
        }

        ComboBox Combo(IEnumerable<string> items, int selected)
        {
            var c = new ComboBox { Height = 31 };
            if (TryFindResource("ObsidianComboBoxStyle") is Style s) c.Style = s;
            foreach (var i in items) c.Items.Add(i);
            c.SelectedIndex = Math.Clamp(selected, 0, c.Items.Count - 1);
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

        _cmbDockStyle = Combo(Enum.GetValues<DockVisualStyle>().Select(s => DockWindow.DockStyleName(s, lang)), (int)v.Style);
        AddRow(T("Dock Stili", "Dock Style"), _cmbDockStyle);
        _cmbDockEdge = Combo([T("Alt kenar", "Bottom edge"), T("Üst kenar", "Top edge")], (int)v.Edge);
        AddRow(T("Dock Konumu", "Dock Position"), _cmbDockEdge);
        _cmbDockSize = Combo([T("Küçük (S)", "Small (S)"), T("Orta (M)", "Medium (M)"), T("Büyük (L)", "Large (L)")], v.SizePreset);
        _cmbDockSize.SelectionChanged += (_, _) =>
        {
            // Keep the existing fine-grained icon-size slider in sync with the S/M/L preset.
            if (_cmbDockSize.SelectedIndex >= 0) SliderDockIconSize.Value = IconSizeForPreset(_cmbDockSize.SelectedIndex);
        };
        AddRow(T("Dock Boyutu", "Dock Size"), _cmbDockSize);
        _txtCity = Box(v.City, T("Örn. İstanbul", "e.g. Istanbul"));
        AddRow(T("Hava Durumu Şehri", "Weather City"), _txtCity);
        _txtWorldZones = Box(v.WorldZones, "Europe/London, America/New_York, Asia/Tokyo");
        AddRow(T("Dünya Saatleri (en fazla 3)", "World Clocks (up to 3)"), _txtWorldZones);
        _txtWorldTime = Box(v.WorldTime, "America/Los_Angeles");
        AddRow(T("Dünya Zamanı Şehri", "World Time City"), _txtWorldTime);
        _txtCurFrom = Box(v.CurrencyFrom, "USD");
        AddRow(T("Döviz: Kaynak", "Currency: From"), _txtCurFrom);
        _txtCurTo = Box(v.CurrencyTo, "TRY");
        AddRow(T("Döviz: Hedef", "Currency: To"), _txtCurTo);
        _txtCrypto = Box(v.Crypto, "BTCUSDT");
        AddRow(T("Kripto Sembolü (Binance)", "Crypto Symbol (Binance)"), _txtCrypto);
        _txtStocks = Box(v.Stocks, "AAPL, MSFT, NVDA");
        AddRow(T("Hisseler (en fazla 3)", "Stocks (up to 3)"), _txtStocks);
        _pwdStockKey = new PasswordBox { MinHeight = 32, ToolTip = T("finnhub.io üzerinden ücretsiz anahtar", "Free key from finnhub.io") };
        if (TryFindResource("ObsidianPasswordBoxStyle") is Style pw) _pwdStockKey.Style = pw;
        AddRow(string.IsNullOrWhiteSpace(_orch.Config.StockApiKeyProtected) || _clearStockKey
            ? T("Finnhub API Anahtarı", "Finnhub API Key")
            : T("Finnhub Anahtarı (kayıtlı, değiştirmek için yaz)", "Finnhub Key (saved, type to replace)"), _pwdStockKey);
        _txtPomFocus = Box(v.PomodoroFocus.ToString());
        AddRow(T("Pomodoro Odak (dk)", "Pomodoro Focus (min)"), _txtPomFocus);
        _txtPomBreak = Box(v.PomodoroBreak.ToString());
        AddRow(T("Pomodoro Mola (dk)", "Pomodoro Break (min)"), _txtPomBreak);
        host.Children.Add(grid);

        _chkClock24h = Toggle(T("24 Saat Biçimi", "24-Hour Clock"), v.Clock24h);
        _chkFahrenheit = Toggle(T("Sıcaklığı °F Göster", "Show Temperature in °F"), v.Fahrenheit);
        _chkScreenTime = Toggle(T("Ekran Süresini Kaydet (yalnızca bu bilgisayarda)", "Record Screen Time (on this PC only)"), v.ScreenTime);
        host.Children.Add(_chkClock24h);
        host.Children.Add(_chkFahrenheit);
        host.Children.Add(_chkScreenTime);

        if (!string.IsNullOrWhiteSpace(_orch.Config.StockApiKeyProtected) && !_clearStockKey)
        {
            var clear = new Button { Content = T("Kayıtlı Hisse Anahtarını Sil", "Remove Saved Stock Key"), Height = 28, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
            if (TryFindResource("DarkActionButtonStyle") is Style b2) clear.Style = b2;
            clear.Click += (_, _) =>
            {
                _clearStockKey = true;
                RebuildDockWidgetSettingsForLanguage();
            };
            host.Children.Add(clear);
        }

        var hint = new TextBlock
        {
            Text = T("Hava durumu Open-Meteo, döviz Frankfurter, kripto Binance, hisse Finnhub üzerinden doğrudan bu bilgisayardan alınır. Anahtarlar yalnızca bu bilgisayarda şifreli saklanır.",
                     "Weather comes from Open-Meteo, currency from Frankfurter, crypto from Binance and stocks from Finnhub, straight from this PC. Keys are stored encrypted on this PC only."),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11,
            Opacity = 0.7,
            Margin = new Thickness(0, 6, 0, 0)
        };
        if (TryFindResource("NotchSubtitleTextStyle") is Style hs) hint.Style = hs;
        hint.FontSize = 11;
        host.Children.Add(hint);
    }

    /// <summary>Writes only the widget settings changed in this window, so dock-side edits made meanwhile survive.</summary>
    private void ApplyDockWidgetSettings(AppConfig cfg)
    {
        var v = ReadWidgetValuesFromForm();
        var b = _widgetBaseline;
        if (v is null || b is null) return;

        if (v.Style != b.Style) cfg.DockStyle = v.Style;
        if (v.Edge != b.Edge) cfg.DockEdge = v.Edge;
        if (v.Clock24h != b.Clock24h) cfg.DockClock24h = v.Clock24h;
        if (v.Fahrenheit != b.Fahrenheit) cfg.WeatherFahrenheit = v.Fahrenheit;
        if (v.ScreenTime != b.ScreenTime) cfg.ScreenTimeEnabled = v.ScreenTime;
        if (!string.Equals(v.City, b.City, StringComparison.Ordinal))
        {
            cfg.WeatherCity = v.City;
            cfg.WeatherLatitude = null;
            cfg.WeatherLongitude = null;
            cfg.WeatherResolvedName = string.Empty;
        }
        if (v.WorldZones != b.WorldZones) cfg.WorldClockZones = SplitList(v.WorldZones);
        if (v.WorldTime != b.WorldTime) cfg.WorldTimeZone = v.WorldTime;
        if (v.CurrencyFrom != b.CurrencyFrom) cfg.CurrencyFrom = v.CurrencyFrom;
        if (v.CurrencyTo != b.CurrencyTo) cfg.CurrencyTo = v.CurrencyTo;
        if (v.Crypto != b.Crypto) cfg.CryptoSymbol = v.Crypto;
        if (v.Stocks != b.Stocks) cfg.StockSymbols = SplitList(v.Stocks);
        if (v.PomodoroFocus != b.PomodoroFocus) cfg.PomodoroFocusMinutes = v.PomodoroFocus;
        if (v.PomodoroBreak != b.PomodoroBreak) cfg.PomodoroBreakMinutes = v.PomodoroBreak;

        string typedKey = _pwdStockKey?.Password.Trim() ?? string.Empty;
        if (typedKey.Length > 0) cfg.StockApiKeyProtected = LocalAiCliUsageReader.ProtectSecret(typedKey);
        else if (_clearStockKey) cfg.StockApiKeyProtected = string.Empty;
    }

    private static List<string> SplitList(string text) =>
        text.Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}
