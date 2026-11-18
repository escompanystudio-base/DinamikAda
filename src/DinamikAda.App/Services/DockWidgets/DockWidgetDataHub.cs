using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Threading;
using DinamikAda.Core.Widgets;
using DinamikAda.Modules.Productivity;
using DinamikAda.Platform.Win32.Interop;

namespace DinamikAda.App.Services.DockWidgets;

public sealed record WeatherDay(DateTime Date, int Code, double HighC, double LowC, int PrecipProbability);
public sealed record WeatherHour(DateTime Time, int Code, double TempC);

public sealed record WeatherSnapshot(
    string City,
    double TempC,
    double FeelsLikeC,
    int Code,
    bool IsDay,
    double WindKmh,
    double PrecipMm,
    int Humidity,
    double HighC,
    double LowC,
    int PrecipProbability,
    DateTime Sunrise,
    DateTime Sunset,
    ImmutableArray<WeatherDay> Days,
    ImmutableArray<WeatherHour> Hours,
    DateTime FetchedLocal);

public sealed record CurrencySnapshot(string From, string To, double Rate, DateTime FetchedLocal);

public sealed record CryptoSnapshot(string Symbol, double Price, double ChangePercent, ImmutableArray<double> Line, DateTime FetchedLocal);

public sealed record StockQuote(string Symbol, double Price, double ChangePercent, double PreviousClose, ImmutableArray<double> Line);

/// <summary>
/// Feeds the Dock widgets: network data (weather, currency, crypto, stocks), short live histories
/// (network, CPU) and the local screen-time ledger. Runs on the UI dispatcher; only the sources
/// needed by widgets currently on the dock are polled, so removing a widget stops its requests.
/// </summary>
public sealed class DockWidgetDataHub : IDisposable
{
    public const int HistoryLength = 60;

    private static readonly HttpClient Http = CreateHttp();
    private static readonly HashSet<string> WeatherWidgets = new(StringComparer.OrdinalIgnoreCase)
    {
        "weather", "weather-details", "forecast", "weather-status", "hourly", "sun", "seven-days", "today",
        "clock-weather", "day-timeline"
    };
    private static readonly HashSet<string> TelemetryWidgets = new(StringComparer.OrdinalIgnoreCase)
    {
        "cpu", "ram", "network", "temperature", "top-cpu", "top-ram", "battery", "battery-ring", "battery-core",
        "battery-dots", "battery-gauge", "storage"
    };
    private static readonly HashSet<string> ExtendedTelemetryWidgets = new(StringComparer.OrdinalIgnoreCase)
    {
        "top-cpu", "top-ram", "temperature", "storage"
    };
    private static readonly HashSet<string> ScreenTimeWidgets = new(StringComparer.OrdinalIgnoreCase)
    {
        "screen-time", "day-timeline"
    };

    private readonly AppOrchestrator _orch;
    private readonly DispatcherTimer _tick;
    private readonly string _screenTimePath;
    private readonly double[] _netDown = new double[HistoryLength];
    private readonly double[] _netUp = new double[HistoryLength];
    private readonly double[] _cpu = new double[HistoryLength];
    private readonly double[] _mem = new double[HistoryLength];
    private readonly Dictionary<string, List<TimeSpan>> _laps = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<double>> _stockLines = new(StringComparer.Ordinal);
    private HashSet<string> _active = new(StringComparer.OrdinalIgnoreCase);
    private ScreenTimeLedger _screenTime;
    private DateTime _lastScreenSampleLocal = DateTime.MinValue;
    private DateTime _lastScreenSaveLocal = DateTime.Now;
    private bool _screenTimeDirty;
    private DateTime _nextWeather = DateTime.MinValue;
    private DateTime _nextCurrency = DateTime.MinValue;
    private DateTime _nextCrypto = DateTime.MinValue;
    private DateTime _nextStocks = DateTime.MinValue;
    private string _weatherKey = string.Empty;
    private string _currencyKey = string.Empty;
    private string _cryptoKey = string.Empty;
    private string _stocksKey = string.Empty;
    private bool _weatherBusy, _currencyBusy, _cryptoBusy, _stocksBusy;
    private int _historyCount;
    private bool _disposed;

    public event EventHandler? Changed;

    public WeatherSnapshot? Weather { get; private set; }
    public string? WeatherStatus { get; private set; }
    public CurrencySnapshot? Currency { get; private set; }
    public string? CurrencyStatus { get; private set; }
    public CryptoSnapshot? Crypto { get; private set; }
    public string? CryptoStatus { get; private set; }
    public IReadOnlyList<StockQuote> Stocks { get; private set; } = [];
    public string? StocksStatus { get; private set; }
    public ScreenTimeLedger ScreenTime => _screenTime;
    public IReadOnlySet<string> ActiveWidgets => _active;

    public DockWidgetDataHub(AppOrchestrator orch, string? screenTimePath = null)
    {
        _orch = orch;
        _screenTimePath = screenTimePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DinamikAda", "screentime.json");
        _screenTime = LoadScreenTime(_screenTimePath);
        _tick = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _tick.Tick += (_, _) => OnTick();
    }

    /// <summary>Oldest-first live histories (MB/s and percent), padded with zeros until filled.</summary>
    public double[] NetDownHistory => Ordered(_netDown);
    public double[] NetUpHistory => Ordered(_netUp);
    public double[] CpuHistory => Ordered(_cpu);
    public double[] MemHistory => Ordered(_mem);

    public IReadOnlyList<TimeSpan> GetLaps(string stopwatchId) =>
        _laps.TryGetValue(stopwatchId, out var l) ? l : [];

    public void AddLap(string stopwatchId, TimeSpan elapsed)
    {
        if (!_laps.TryGetValue(stopwatchId, out var l))
        {
            l = new List<TimeSpan>();
            _laps[stopwatchId] = l;
        }
        l.Insert(0, elapsed);
        if (l.Count > 50) l.RemoveAt(l.Count - 1);
        RaiseChanged();
    }

    public void ClearLaps(string stopwatchId)
    {
        if (_laps.Remove(stopwatchId)) RaiseChanged();
    }

    /// <summary>Starts or stops polling to match the widgets currently placed on the dock.</summary>
    public void SetActiveWidgets(IEnumerable<string> widgetIds, bool dockVisible)
    {
        if (_disposed) return;
        _active = dockVisible
            ? new HashSet<string>(widgetIds, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        bool telemetry = _active.Overlaps(TelemetryWidgets);
        bool extended = _active.Overlaps(ExtendedTelemetryWidgets);
        _orch.SetDockWidgetTelemetryDemand(telemetry, extended);

        if (_active.Count > 0 || NeedsScreenTime())
        {
            if (!_tick.IsEnabled) _tick.Start();
            OnTick();
        }
        else
        {
            _tick.Stop();
        }
    }

    /// <summary>Forces the next tick to refetch everything whose settings may have changed.</summary>
    public void InvalidateNetworkData()
    {
        _nextWeather = _nextCurrency = _nextCrypto = _nextStocks = DateTime.MinValue;
        if (_tick.IsEnabled) OnTick();
    }

    private bool NeedsScreenTime() => _orch.Config.ScreenTimeEnabled;

    private void OnTick()
    {
        if (_disposed) return;
        var now = DateTime.Now;
        SampleHistories();

        if (NeedsScreenTime() && (now - _lastScreenSampleLocal).TotalSeconds >= 5)
        {
            SampleScreenTime(now);
        }
        if (_screenTimeDirty && (now - _lastScreenSaveLocal).TotalMinutes >= 5)
        {
            SaveScreenTime();
        }

        var cfg = _orch.Config;
        if (_active.Overlaps(WeatherWidgets))
        {
            string key = $"{cfg.WeatherCity}|{cfg.WeatherLatitude}|{cfg.WeatherLongitude}";
            if (key != _weatherKey) { _weatherKey = key; _nextWeather = DateTime.MinValue; }
            if (now >= _nextWeather && !_weatherBusy) _ = FetchWeatherAsync();
        }
        if (_active.Contains("currency"))
        {
            string key = $"{cfg.CurrencyFrom}|{cfg.CurrencyTo}";
            if (key != _currencyKey) { _currencyKey = key; _nextCurrency = DateTime.MinValue; Currency = null; }
            if (now >= _nextCurrency && !_currencyBusy) _ = FetchCurrencyAsync();
        }
        if (_active.Contains("crypto"))
        {
            if (cfg.CryptoSymbol != _cryptoKey) { _cryptoKey = cfg.CryptoSymbol; _nextCrypto = DateTime.MinValue; Crypto = null; }
            if (now >= _nextCrypto && !_cryptoBusy) _ = FetchCryptoAsync();
        }
        if (_active.Contains("stock-chart") || _active.Contains("stock-watchlist"))
        {
            string key = string.Join(",", cfg.StockSymbols) + "|" + cfg.StockApiKeyProtected.Length;
            if (key != _stocksKey) { _stocksKey = key; _nextStocks = DateTime.MinValue; _stockLines.Clear(); }
            if (now >= _nextStocks && !_stocksBusy) _ = FetchStocksAsync();
        }

        RaiseChanged();
    }

    private void SampleHistories()
    {
        var t = _orch.Telemetry.Current;
        int idx = _historyCount % HistoryLength;
        _netDown[idx] = double.IsFinite(t.NetDownMbps) ? Math.Max(0, t.NetDownMbps) : 0;
        _netUp[idx] = double.IsFinite(t.NetUpMbps) ? Math.Max(0, t.NetUpMbps) : 0;
        _cpu[idx] = double.IsFinite(t.CpuPercent) ? Math.Clamp(t.CpuPercent, 0, 100) : 0;
        _mem[idx] = double.IsFinite(t.MemoryPercent) ? Math.Clamp(t.MemoryPercent, 0, 100) : 0;
        _historyCount++;
    }

    private double[] Ordered(double[] ring)
    {
        var result = new double[HistoryLength];
        int filled = Math.Min(_historyCount, HistoryLength);
        int start = _historyCount - filled;
        for (int i = 0; i < filled; i++)
        {
            result[HistoryLength - filled + i] = ring[(start + i) % HistoryLength];
        }
        return result;
    }

    // ---- Screen time ---------------------------------------------------------------------------

    private void SampleScreenTime(DateTime now)
    {
        double elapsed = _lastScreenSampleLocal == DateTime.MinValue ? 0 : (now - _lastScreenSampleLocal).TotalSeconds;
        _lastScreenSampleLocal = now;
        if (elapsed <= 0 || elapsed > ScreenTimeLedger.MaxSampleGap.TotalSeconds) return;
        if (IdleFor() > TimeSpan.FromMinutes(5) && !_orch.Media.Current.IsPlaying) return;

        string? app = ResolveForegroundAppName();
        if (string.IsNullOrWhiteSpace(app)) return;
        _screenTime.AddSample(app, now, elapsed, AppKindClassifier.Classify(app));
        _screenTimeDirty = true;
    }

    private static string? ResolveForegroundAppName()
    {
        nint fg = NativeMethods.GetForegroundWindow();
        if (fg == 0) return null;
        NativeMethods.GetWindowThreadProcessId(fg, out uint pid);
        if (pid == 0 || pid == Environment.ProcessId) return null;
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName;
        }
        catch
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LastInputInfo plii);

    private static TimeSpan IdleFor()
    {
        var info = new LastInputInfo { cbSize = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info)) return TimeSpan.Zero;
        uint idleMs = unchecked((uint)Environment.TickCount - info.dwTime);
        return TimeSpan.FromMilliseconds(idleMs);
    }

    private static ScreenTimeLedger LoadScreenTime(string path)
    {
        try
        {
            return File.Exists(path)
                ? ScreenTimeLedger.Deserialize(File.ReadAllText(path), DateTime.Now)
                : new ScreenTimeLedger();
        }
        catch
        {
            return new ScreenTimeLedger();
        }
    }

    private void SaveScreenTime()
    {
        _lastScreenSaveLocal = DateTime.Now;
        if (!_screenTimeDirty) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_screenTimePath)!);
            string tmp = _screenTimePath + ".tmp";
            File.WriteAllText(tmp, _screenTime.Serialize());
            File.Move(tmp, _screenTimePath, overwrite: true);
            _screenTimeDirty = false;
        }
        catch
        {
            // Retried at the next save interval.
        }
    }

    // ---- Network sources -----------------------------------------------------------------------

    private async Task FetchWeatherAsync()
    {
        _weatherBusy = true;
        try
        {
            var cfg = _orch.Config;
            double? lat = cfg.WeatherLatitude, lon = cfg.WeatherLongitude;
            string city = cfg.WeatherResolvedName;
            if ((lat is null || lon is null) && !string.IsNullOrWhiteSpace(cfg.WeatherCity))
            {
                var geo = await GeocodeAsync(cfg.WeatherCity, cfg.Language);
                if (geo is null)
                {
                    WeatherStatus = cfg.Language == "en" ? "City not found" : "Şehir bulunamadı";
                    _nextWeather = DateTime.Now.AddMinutes(10);
                    return;
                }
                (lat, lon, city) = geo.Value;
                _orch.ConfigStore.Update(cfg, c =>
                {
                    c.WeatherLatitude = lat;
                    c.WeatherLongitude = lon;
                    c.WeatherResolvedName = city;
                });
                _weatherKey = $"{cfg.WeatherCity}|{cfg.WeatherLatitude}|{cfg.WeatherLongitude}";
            }
            if (lat is null || lon is null)
            {
                WeatherStatus = cfg.Language == "en" ? "Pick a city in Settings" : "Ayarlardan şehir seç";
                Weather = null;
                _nextWeather = DateTime.Now.AddMinutes(1);
                return;
            }

            string coords = string.Create(CultureInfo.InvariantCulture, $"latitude={lat:0.####}&longitude={lon:0.####}");
            string url = "https://api.open-meteo.com/v1/forecast?" + coords +
                "&current=temperature_2m,apparent_temperature,relative_humidity_2m,is_day,precipitation,weather_code,wind_speed_10m" +
                "&hourly=temperature_2m,weather_code&daily=weather_code,temperature_2m_max,temperature_2m_min,sunrise,sunset,precipitation_probability_max" +
                "&timezone=auto&forecast_days=7";
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
            Weather = ParseWeather(doc.RootElement, string.IsNullOrWhiteSpace(city) ? cfg.WeatherCity : city);
            WeatherStatus = null;
            _nextWeather = DateTime.Now.AddMinutes(20);
        }
        catch (Exception)
        {
            WeatherStatus ??= _orch.Config.Language == "en" ? "Weather unavailable" : "Hava durumu alınamadı";
            _nextWeather = DateTime.Now.AddMinutes(5);
        }
        finally
        {
            _weatherBusy = false;
            RaiseChanged();
        }
    }

    public static async Task<(double Lat, double Lon, string Name)?> GeocodeAsync(string city, string lang)
    {
        string url = "https://geocoding-api.open-meteo.com/v1/search?count=1&format=json&language=" +
                     (lang == "en" ? "en" : "tr") + "&name=" + Uri.EscapeDataString(city.Trim());
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
        if (!doc.RootElement.TryGetProperty("results", out var results) || results.GetArrayLength() == 0) return null;
        var r = results[0];
        string name = r.TryGetProperty("name", out var n) ? n.GetString() ?? city : city;
        return (r.GetProperty("latitude").GetDouble(), r.GetProperty("longitude").GetDouble(), name);
    }

    public static WeatherSnapshot ParseWeather(JsonElement root, string city)
    {
        var cur = root.GetProperty("current");
        var daily = root.GetProperty("daily");
        var hourly = root.GetProperty("hourly");

        var dates = daily.GetProperty("time");
        var codes = daily.GetProperty("weather_code");
        var highs = daily.GetProperty("temperature_2m_max");
        var lows = daily.GetProperty("temperature_2m_min");
        var probs = daily.TryGetProperty("precipitation_probability_max", out var pp) ? pp : default;
        var days = ImmutableArray.CreateBuilder<WeatherDay>();
        for (int i = 0; i < dates.GetArrayLength(); i++)
        {
            days.Add(new WeatherDay(
                DateTime.Parse(dates[i].GetString()!, CultureInfo.InvariantCulture),
                ReadInt(codes[i]), ReadDouble(highs[i]), ReadDouble(lows[i]),
                probs.ValueKind == JsonValueKind.Array ? ReadInt(probs[i]) : 0));
        }

        DateTime curTime = DateTime.Parse(cur.GetProperty("time").GetString()!, CultureInfo.InvariantCulture);
        var hTimes = hourly.GetProperty("time");
        var hTemps = hourly.GetProperty("temperature_2m");
        var hCodes = hourly.GetProperty("weather_code");
        var hours = ImmutableArray.CreateBuilder<WeatherHour>();
        for (int i = 0; i < hTimes.GetArrayLength() && hours.Count < 6; i++)
        {
            var t = DateTime.Parse(hTimes[i].GetString()!, CultureInfo.InvariantCulture);
            if (t <= curTime) continue;
            hours.Add(new WeatherHour(t, ReadInt(hCodes[i]), ReadDouble(hTemps[i])));
        }

        var today = days.Count > 0 ? days[0] : new WeatherDay(DateTime.Today, 0, 0, 0, 0);
        DateTime sunrise = DateTime.Parse(daily.GetProperty("sunrise")[0].GetString()!, CultureInfo.InvariantCulture);
        DateTime sunset = DateTime.Parse(daily.GetProperty("sunset")[0].GetString()!, CultureInfo.InvariantCulture);

        return new WeatherSnapshot(
            City: city,
            TempC: ReadDouble(cur.GetProperty("temperature_2m")),
            FeelsLikeC: cur.TryGetProperty("apparent_temperature", out var fl) ? ReadDouble(fl) : ReadDouble(cur.GetProperty("temperature_2m")),
            Code: ReadInt(cur.GetProperty("weather_code")),
            IsDay: !cur.TryGetProperty("is_day", out var d) || ReadInt(d) == 1,
            WindKmh: cur.TryGetProperty("wind_speed_10m", out var w) ? ReadDouble(w) : 0,
            PrecipMm: cur.TryGetProperty("precipitation", out var pr) ? ReadDouble(pr) : 0,
            Humidity: cur.TryGetProperty("relative_humidity_2m", out var hu) ? ReadInt(hu) : 0,
            HighC: today.HighC,
            LowC: today.LowC,
            PrecipProbability: today.PrecipProbability,
            Sunrise: sunrise,
            Sunset: sunset,
            Days: days.ToImmutable(),
            Hours: hours.ToImmutable(),
            FetchedLocal: DateTime.Now);
    }

    private async Task FetchCurrencyAsync()
    {
        _currencyBusy = true;
        var cfg = _orch.Config;
        try
        {
            if (string.Equals(cfg.CurrencyFrom, cfg.CurrencyTo, StringComparison.OrdinalIgnoreCase))
            {
                Currency = new CurrencySnapshot(cfg.CurrencyFrom, cfg.CurrencyTo, 1.0, DateTime.Now);
            }
            else
            {
                string url = $"https://api.frankfurter.app/latest?from={cfg.CurrencyFrom}&to={cfg.CurrencyTo}";
                using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
                double rate = doc.RootElement.GetProperty("rates").GetProperty(cfg.CurrencyTo).GetDouble();
                Currency = new CurrencySnapshot(cfg.CurrencyFrom, cfg.CurrencyTo, rate, DateTime.Now);
            }
            CurrencyStatus = null;
            _nextCurrency = DateTime.Now.AddHours(1);
        }
        catch (Exception)
        {
            CurrencyStatus = cfg.Language == "en" ? "Rate unavailable" : "Kur alınamadı";
            _nextCurrency = DateTime.Now.AddMinutes(10);
        }
        finally
        {
            _currencyBusy = false;
            RaiseChanged();
        }
    }

    private async Task FetchCryptoAsync()
    {
        _cryptoBusy = true;
        var cfg = _orch.Config;
        try
        {
            string sym = cfg.CryptoSymbol;
            using var ticker = JsonDocument.Parse(await Http.GetStringAsync($"https://api.binance.com/api/v3/ticker/24hr?symbol={sym}"));
            double price = double.Parse(ticker.RootElement.GetProperty("lastPrice").GetString()!, CultureInfo.InvariantCulture);
            double change = double.Parse(ticker.RootElement.GetProperty("priceChangePercent").GetString()!, CultureInfo.InvariantCulture);
            using var klines = JsonDocument.Parse(await Http.GetStringAsync($"https://api.binance.com/api/v3/klines?symbol={sym}&interval=15m&limit=96"));
            var line = ImmutableArray.CreateBuilder<double>();
            foreach (var k in klines.RootElement.EnumerateArray())
            {
                line.Add(double.Parse(k[4].GetString()!, CultureInfo.InvariantCulture));
            }
            Crypto = new CryptoSnapshot(sym, price, change, line.ToImmutable(), DateTime.Now);
            CryptoStatus = null;
            _nextCrypto = DateTime.Now.AddSeconds(60);
        }
        catch (Exception)
        {
            CryptoStatus = cfg.Language == "en" ? "Price unavailable" : "Fiyat alınamadı";
            _nextCrypto = DateTime.Now.AddMinutes(2);
        }
        finally
        {
            _cryptoBusy = false;
            RaiseChanged();
        }
    }

    public static string? GetStockApiKey(Core.Config.AppConfig cfg) =>
        string.IsNullOrWhiteSpace(cfg.StockApiKeyProtected) ? null : LocalAiCliUsageReader.UnprotectSecret(cfg.StockApiKeyProtected);

    private async Task FetchStocksAsync()
    {
        _stocksBusy = true;
        var cfg = _orch.Config;
        try
        {
            string? key = GetStockApiKey(cfg);
            if (string.IsNullOrWhiteSpace(key))
            {
                StocksStatus = cfg.Language == "en" ? "Add a Finnhub key in Settings" : "Ayarlardan Finnhub anahtarı ekle";
                Stocks = [];
                _nextStocks = DateTime.Now.AddMinutes(1);
                return;
            }

            var quotes = new List<StockQuote>();
            foreach (var sym in cfg.StockSymbols)
            {
                using var doc = JsonDocument.Parse(await Http.GetStringAsync(
                    $"https://finnhub.io/api/v1/quote?symbol={Uri.EscapeDataString(sym)}&token={Uri.EscapeDataString(key)}"));
                var r = doc.RootElement;
                double price = r.GetProperty("c").GetDouble();
                double prev = r.GetProperty("pc").GetDouble();
                double pct = r.TryGetProperty("dp", out var dp) && dp.ValueKind == JsonValueKind.Number ? dp.GetDouble() : DockWidgetMath.PercentChange(prev, price);
                if (!_stockLines.TryGetValue(sym, out var line))
                {
                    line = new List<double> { prev };
                    if (r.TryGetProperty("o", out var o) && o.GetDouble() > 0) line.Add(o.GetDouble());
                    _stockLines[sym] = line;
                }
                if (line.Count == 0 || Math.Abs(line[^1] - price) > 1e-9) line.Add(price);
                if (line.Count > 120) line.RemoveAt(0);
                quotes.Add(new StockQuote(sym, price, pct, prev, line.ToImmutableArray()));
            }
            Stocks = quotes;
            StocksStatus = null;
            _nextStocks = DateTime.Now.AddMinutes(2);
        }
        catch (Exception)
        {
            StocksStatus = cfg.Language == "en" ? "Quotes unavailable" : "Fiyatlar alınamadı";
            _nextStocks = DateTime.Now.AddMinutes(5);
        }
        finally
        {
            _stocksBusy = false;
            RaiseChanged();
        }
    }

    private static double ReadDouble(JsonElement e) => e.ValueKind == JsonValueKind.Number ? e.GetDouble() : 0.0;
    private static int ReadInt(JsonElement e) => e.ValueKind == JsonValueKind.Number ? (int)Math.Round(e.GetDouble()) : 0;

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("DinamikAda/1.0");
        return http;
    }

    private void RaiseChanged()
    {
        if (!_disposed) Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _tick.Stop();
        SaveScreenTime();
        _disposed = true;
        _orch.SetDockWidgetTelemetryDemand(false, false);
    }
}
