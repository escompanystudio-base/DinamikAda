using System.Net;
using System.Net.Http;
using DinamikAda.App.Views.DockWidgets.Accounts;
using DinamikAda.Core.Accounts;
using DinamikAda.Core.Config;
using DinamikAda.Core.Widgets;
using DinamikAda.Modules.Productivity;

namespace DinamikAda.App.Services.DockWidgets;

public enum AccountState
{
    /// <summary>The service has no (complete) credentials yet: tiles show sample data.</summary>
    NotConnected,
    /// <summary>Credentials exist, the first fetch is in flight.</summary>
    Loading,
    Ready,
    /// <summary>The last fetch failed; <see cref="AccountResult.Data"/> may still hold the previous value.</summary>
    Error
}

public sealed record AccountResult(AccountState State, object? Data, string? Error, DateTime? UpdatedLocal)
{
    public static readonly AccountResult NotConnected = new(AccountState.NotConnected, null, null, null);
}

/// <summary>Reads, saves and removes a service's credentials (secret fields are DPAPI-protected at rest).</summary>
public static class AccountStore
{
    public static DockAccount? Find(AppConfig cfg, string service) =>
        cfg.DockAccounts?.FirstOrDefault(a => string.Equals(a.Service, service, StringComparison.OrdinalIgnoreCase));

    /// <summary>Plain value of one field, or null.</summary>
    public static string? Get(AppConfig cfg, string service, string key)
    {
        var acc = Find(cfg, service);
        if (acc is null || !acc.Values.TryGetValue(key, out var stored) || string.IsNullOrWhiteSpace(stored)) return null;
        var field = AccountCatalog.Find(service)?.Fields.FirstOrDefault(f => f.Key == key);
        if (field is { Secret: true })
        {
            string? plain = LocalAiCliUsageReader.UnprotectSecret(stored);
            return string.IsNullOrWhiteSpace(plain) ? null : plain;
        }
        return stored;
    }

    public static bool IsConnected(AppConfig cfg, string service)
    {
        var d = AccountCatalog.Find(service);
        return d is not null && Find(cfg, service) is not null && AccountCatalog.IsComplete(d, k => Get(cfg, service, k));
    }

    /// <summary>Change token: differs whenever the stored values of the service change.</summary>
    public static string Fingerprint(AppConfig cfg, string service)
    {
        var acc = Find(cfg, service);
        return acc is null ? string.Empty : string.Join("|", acc.Values.OrderBy(kv => kv.Key).Select(kv => kv.Key + "=" + kv.Value));
    }

    /// <summary>Saves the given plain values (blank values remove the field). Always assigns new collections.</summary>
    public static void Save(AppConfigStore store, AppConfig cfg, string service, IReadOnlyDictionary<string, string> plainValues)
    {
        var d = AccountCatalog.Find(service) ?? throw new ArgumentException("Unknown service", nameof(service));
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in d.Fields)
        {
            if (!plainValues.TryGetValue(f.Key, out var v) || string.IsNullOrWhiteSpace(v)) continue;
            v = v.Trim();
            values[f.Key] = f.Secret ? LocalAiCliUsageReader.ProtectSecret(v) : v;
        }
        store.Update(cfg, c =>
        {
            var list = (c.DockAccounts ?? new()).Where(a => !string.Equals(a.Service, d.Id, StringComparison.OrdinalIgnoreCase)).ToList();
            list.Add(new DockAccount { Service = d.Id, Values = values });
            c.DockAccounts = list;
        });
    }

    public static void Remove(AppConfigStore store, AppConfig cfg, string service)
    {
        store.Update(cfg, c => c.DockAccounts = (c.DockAccounts ?? new())
            .Where(a => !string.Equals(a.Service, service, StringComparison.OrdinalIgnoreCase)).ToList());
    }
}

/// <summary>What a fetcher gets: an HTTP client and the service's plain credentials.</summary>
public sealed class AccountFetchContext
{
    private readonly AppConfig _cfg;

    public AccountFetchContext(HttpClient http, AppConfig cfg, string service)
    {
        Http = http;
        _cfg = cfg;
        Service = service;
    }

    public HttpClient Http { get; }
    public string Service { get; }
    public string Lang => _cfg.Language;
    public string T(string tr, string en) => _cfg.Language == "en" ? en : tr;

    /// <summary>Plain value of a credential field ("key", "user", "site"...), or null.</summary>
    public string? Get(string key) => AccountStore.Get(_cfg, Service, key);
    /// <summary>Plain value of a required field; throws a friendly error when it is missing.</summary>
    public string Require(string key) => Get(key) ?? throw new AccountFetchException(T("Eksik bilgi: ", "Missing value: ") + key);
}

/// <summary>A fetch failure with a message that can be shown on the card as-is.</summary>
public sealed class AccountFetchException(string userMessage) : Exception(userMessage);

/// <summary>Loads one widget's data; returns any object the widget's tile understands.</summary>
public delegate Task<object> AccountFetcher(AccountFetchContext ctx, CancellationToken ct);

/// <summary>Widget id → (service, fetcher). Each account widget group registers its fetchers here.</summary>
public static class AccountFetcherRegistry
{
    private static readonly Dictionary<string, (string Service, AccountFetcher Fetch)> Map = Build();

    private static Dictionary<string, (string Service, AccountFetcher Fetch)> Build()
    {
        var map = new Dictionary<string, (string, AccountFetcher)>(StringComparer.OrdinalIgnoreCase);
        void Add(string widgetId, string service, AccountFetcher fetch) => map[widgetId] = (service, fetch);
        FinanceAccountTiles.RegisterFetchers(Add);
        DeveloperAccountTiles.RegisterFetchers(Add);
        SocialMailAccountTiles.RegisterFetchers(Add);
        return map;
    }

    public static bool TryGet(string widgetId, out string service, out AccountFetcher fetch)
    {
        if (Map.TryGetValue(widgetId, out var e))
        {
            service = e.Service;
            fetch = e.Fetch;
            return true;
        }
        service = string.Empty;
        fetch = null!;
        return false;
    }
}

/// <summary>
/// Polls the account widgets on the dock: only active widgets with complete credentials, each at its service's
/// refresh interval, with back-off after errors. Every request goes straight to that service.
/// </summary>
public sealed class AccountHub
{
    private sealed class Entry
    {
        public AccountResult Result = AccountResult.NotConnected;
        public string Fingerprint = string.Empty;
        public DateTime NextFetch = DateTime.MinValue;
        public bool Busy;
        public int Failures;
    }

    private static readonly HttpClient Http = CreateHttp();
    private readonly AppOrchestrator _orch;
    private readonly Action _changed;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _active = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource _cts = new();

    public AccountHub(AppOrchestrator orch, Action changed)
    {
        _orch = orch;
        _changed = changed;
    }

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10)
        })
        { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("DinamikAda/1.0 (+desktop widgets)");
        return http;
    }

    public bool HasActiveAccountWidgets => _active.Count > 0;

    public void SetActiveWidgets(IEnumerable<string> widgetIds)
    {
        _active = new HashSet<string>(widgetIds.Where(id => AccountFetcherRegistry.TryGet(id, out _, out _)),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Current state for a widget (cheap; safe to call every second).</summary>
    public AccountResult Get(string widgetId)
    {
        if (!AccountFetcherRegistry.TryGet(widgetId, out string service, out _)) return AccountResult.NotConnected;
        if (!AccountStore.IsConnected(_orch.Config, service)) return AccountResult.NotConnected;
        return _entries.TryGetValue(widgetId, out var e) ? e.Result : new AccountResult(AccountState.Loading, null, null, null);
    }

    /// <summary>Fetches the widget's data now (after connecting, or from the card's refresh button).</summary>
    public void Refresh(string widgetId)
    {
        if (_entries.TryGetValue(widgetId, out var e)) { e.NextFetch = DateTime.MinValue; e.Failures = 0; }
        Tick(DateTime.Now, onlyWidget: widgetId);
    }

    /// <summary>Called by the widget data hub every second.</summary>
    public void Tick(DateTime now, string? onlyWidget = null)
    {
        var cfg = _orch.Config;
        foreach (string id in _active)
        {
            if (onlyWidget is not null && !string.Equals(id, onlyWidget, StringComparison.OrdinalIgnoreCase)) continue;
            if (!AccountFetcherRegistry.TryGet(id, out string service, out var fetch)) continue;
            if (!_entries.TryGetValue(id, out var e)) _entries[id] = e = new Entry();

            string fp = AccountStore.Fingerprint(cfg, service);
            if (fp != e.Fingerprint)
            {
                // Credentials changed (connected, edited or removed): drop old data and fetch again.
                e.Fingerprint = fp;
                e.NextFetch = DateTime.MinValue;
                e.Failures = 0;
                e.Result = AccountStore.IsConnected(cfg, service)
                    ? new AccountResult(AccountState.Loading, null, null, null)
                    : AccountResult.NotConnected;
                _changed();
            }
            if (e.Busy || now < e.NextFetch || !AccountStore.IsConnected(cfg, service)) continue;
            _ = FetchAsync(id, service, fetch, e, cfg);
        }
    }

    private async Task FetchAsync(string widgetId, string service, AccountFetcher fetch, Entry e, AppConfig cfg)
    {
        e.Busy = true;
        string fp = e.Fingerprint;
        int minutes = AccountCatalog.Find(service)?.RefreshMinutes ?? 15;
        try
        {
            object data = await fetch(new AccountFetchContext(Http, cfg, service), _cts.Token);
            if (fp != e.Fingerprint) return; // credentials changed while loading
            e.Result = new AccountResult(AccountState.Ready, data, null, DateTime.Now);
            e.Failures = 0;
            e.NextFetch = DateTime.Now.AddMinutes(minutes);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            if (fp != e.Fingerprint) return;
            e.Failures++;
            e.Result = new AccountResult(AccountState.Error, e.Result.Data, FriendlyError(ex, cfg.Language), e.Result.UpdatedLocal);
            // 2, 4, 8 … minutes, capped by the normal interval (at least 2 minutes).
            double backoff = Math.Min(Math.Max(minutes, 2), Math.Pow(2, Math.Min(e.Failures, 6)));
            e.NextFetch = DateTime.Now.AddMinutes(backoff);
            if (ex is not AccountFetchException and not HttpRequestException and not TaskCanceledException)
                CrashLogger.Log("AccountWidget", ex, $"Account widget '{widgetId}' fetch failed.");
        }
        finally
        {
            e.Busy = false;
        }
        _changed();
    }

    private static string FriendlyError(Exception ex, string lang)
    {
        bool en = lang == "en";
        return ex switch
        {
            AccountFetchException a => a.Message,
            HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } =>
                en ? "The key was rejected. Check it and its permissions." : "Anahtar reddedildi. Anahtarı ve izinlerini kontrol et.",
            HttpRequestException { StatusCode: HttpStatusCode.NotFound } =>
                en ? "Not found. Check the username, site or ID." : "Bulunamadı. Kullanıcı adını, siteyi veya kimliği kontrol et.",
            HttpRequestException { StatusCode: (HttpStatusCode)429 } =>
                en ? "Too many requests; will retry later." : "Çok fazla istek; sonra tekrar denenecek.",
            HttpRequestException { StatusCode: not null } h =>
                (en ? "Service error " : "Servis hatası ") + (int)h.StatusCode!.Value,
            HttpRequestException or TaskCanceledException =>
                en ? "No connection to the service." : "Servise bağlanılamadı.",
            _ => en ? "Could not read the data." : "Veri okunamadı."
        };
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        _cts = new CancellationTokenSource();
    }

    /// <summary>The widgets on the dock that read an account (for the data hub's tick gate).</summary>
    public static bool IsAccountWidget(string widgetId) => DockWidgetCatalog.Find(widgetId)?.Account is not null;
}
