using System.Globalization;
using System.Text.Json;

namespace DinamikAda.Core.Accounts;

// ---- Stripe -------------------------------------------------------------------------------------

/// <summary>One Stripe balance transaction (amount in minor units of <see cref="Currency"/>).</summary>
public sealed record StripeTxn(string Id, long Amount, string Currency, DateTimeOffset Created);

/// <summary>One page of GET /v1/balance_transactions.</summary>
public sealed record StripeTxnPage(IReadOnlyList<StripeTxn> Items, bool HasMore, string? LastId);

/// <summary>Stripe charge revenue in major units of <see cref="Currency"/>.</summary>
/// <param name="Daily">30 local days, oldest first; the last value is today.</param>
/// <param name="ChangePct">Last 7 days vs the 7 days before, in percent; null when the earlier week had no revenue.</param>
public sealed record StripeRevenue(
    string Currency, double Today, double ThisMonth, double Last30, IReadOnlyList<double> Daily,
    double? ChangePct, double BestDay, DateTime BestDayDate);

// ---- Lemon Squeezy ------------------------------------------------------------------------------

public sealed record LemonStore(string Name, string Currency, double TotalRevenue, double ThirtyDayRevenue, long TotalSales, long ThirtyDaySales);

/// <summary>All stores; the totals cover the stores in the most common currency.</summary>
public sealed record LemonSqueezyRevenue(IReadOnlyList<LemonStore> Stores)
{
    public string Currency => FinanceParsers.MostCommon(Stores.Select(s => s.Currency));
    public double ThirtyDayRevenue => Stores.Where(s => s.Currency == Currency).Sum(s => s.ThirtyDayRevenue);
    public double TotalRevenue => Stores.Where(s => s.Currency == Currency).Sum(s => s.TotalRevenue);
    public long ThirtyDaySales => Stores.Sum(s => s.ThirtyDaySales);
    public long TotalSales => Stores.Sum(s => s.TotalSales);
}

// ---- Gumroad ------------------------------------------------------------------------------------

public sealed record GumroadProduct(string Name, long SalesCount, double SalesUsd, string FormattedPrice, bool Published);

/// <summary>Gumroad products sorted by revenue (USD). <see cref="Success"/> is false when the API refused the call.</summary>
public sealed record GumroadSales(bool Success, string? Message, IReadOnlyList<GumroadProduct> Products)
{
    public double TotalUsd => Products.Sum(p => p.SalesUsd);
    public long TotalSales => Products.Sum(p => p.SalesCount);
}

// ---- Polar --------------------------------------------------------------------------------------

/// <summary>One Polar order; the customer email is already masked.</summary>
public sealed record PolarOrder(double Amount, string Currency, DateTime CreatedUtc, string Product, string MaskedEmail);

/// <param name="DailyCounts">Order counts for the last 7 local days, oldest first.</param>
public sealed record PolarRevenue(string Currency, double Last30, int Count30, IReadOnlyList<PolarOrder> Latest, IReadOnlyList<int> DailyCounts);

// ---- Paddle -------------------------------------------------------------------------------------

public sealed record PaddleTxn(string Currency, double Amount, DateTimeOffset Created);

/// <summary>One page of GET /transactions; <see cref="Next"/> is set only when more pages exist on a Paddle host.</summary>
public sealed record PaddleTxnPage(IReadOnlyList<PaddleTxn> Items, string? Next);

public sealed record PaddleRevenue(string Currency, double Total, int Count, IReadOnlyList<double> Daily)
{
    public double Average => Count == 0 ? 0 : Total / Count;
}

/// <summary>Pure JSON parsers for the finance account widgets (Stripe, Lemon Squeezy, Gumroad, Polar, Paddle).</summary>
public static class FinanceParsers
{
    public const int Days = 30;

    private static readonly HashSet<string> ZeroDecimal = new(StringComparer.OrdinalIgnoreCase)
    {
        "BIF", "CLP", "DJF", "GNF", "JPY", "KMF", "KRW", "MGA", "PYG", "RWF", "UGX", "VND", "VUV", "XAF", "XOF", "XPF"
    };

    // ---- Shared helpers -------------------------------------------------------------------------

    /// <summary>Minor units (cents) to major units; zero-decimal currencies (JPY, KRW…) are already major.</summary>
    public static double MinorToMajor(double minor, string? currency) =>
        currency is not null && ZeroDecimal.Contains(currency) ? minor : minor / 100.0;

    /// <summary>UTC instant of local midnight <paramref name="daysBack"/> days before today in <paramref name="tz"/>.</summary>
    public static DateTimeOffset LocalMidnightUtc(DateTimeOffset nowUtc, TimeZoneInfo tz, int daysBack)
    {
        var local = TimeZoneInfo.ConvertTime(nowUtc, tz).Date.AddDays(-daysBack);
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (tz.IsInvalidTime(local)) local = local.AddHours(1); // DST gap at midnight
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, tz), TimeSpan.Zero);
    }

    /// <summary>"jane@example.com" → "j***@e***.com". Never returns the full address.</summary>
    public static string MaskEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return string.Empty;
        email = email.Trim();
        int at = email.IndexOf('@');
        if (at <= 0) return "***";
        string domain = email[(at + 1)..];
        if (domain.Length == 0) return email[0] + "***@***";
        int dot = domain.IndexOf('.');
        string tail = dot > 0 ? domain[dot..] : string.Empty;
        return email[0] + "***@" + domain[0] + "***" + tail;
    }

    /// <summary>The most frequent value (ties: first seen); "USD" when empty.</summary>
    public static string MostCommon(IEnumerable<string> values)
    {
        string? best = null;
        int bestCount = 0;
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var v in values)
        {
            int c = counts[v] = counts.GetValueOrDefault(v) + 1;
            if (c > bestCount) { best = v; bestCount = c; }
        }
        return best ?? "USD";
    }

    private static JsonElement Root(JsonDocument doc)
    {
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new FormatException("Unexpected response shape.");
        return root;
    }

    private static string Str(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? string.Empty
            : string.Empty;

    private static double Num(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object ? SocialMailParsers.Num(obj, name) : 0;

    private static JsonElement Child(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) ? v : default;

    private static IEnumerable<JsonElement> Array(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray()
            : [];

    private static string Currency(string raw) => string.IsNullOrWhiteSpace(raw) ? "USD" : raw.Trim().ToUpperInvariant();

    private static DateTimeOffset? Iso(string raw) =>
        DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t)
            ? t
            : null;

    /// <summary>Sums amounts per local day: <paramref name="days"/> values, oldest first, the last is today.</summary>
    private static double[] DailySeries(IEnumerable<(DateTimeOffset At, double Amount)> items, DateTimeOffset nowUtc, TimeZoneInfo tz, int days)
    {
        var series = new double[days];
        var today = TimeZoneInfo.ConvertTime(nowUtc, tz).Date;
        foreach (var (at, amount) in items)
        {
            int idx = days - 1 - (int)(today - TimeZoneInfo.ConvertTime(at, tz).Date).TotalDays;
            if (idx >= 0 && idx < days) series[idx] += amount;
        }
        return series;
    }

    // ---- Stripe ---------------------------------------------------------------------------------

    /// <summary>Parses one page of GET https://api.stripe.com/v1/balance_transactions.</summary>
    public static StripeTxnPage ParseStripeBalanceTransactions(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = Root(doc);
        var items = new List<StripeTxn>();
        foreach (var t in Array(root, "data"))
        {
            items.Add(new StripeTxn(
                Str(t, "id"),
                (long)Math.Round(Num(t, "amount")),
                Currency(Str(t, "currency")),
                DateTimeOffset.FromUnixTimeSeconds((long)Num(t, "created"))));
        }
        bool hasMore = root.TryGetProperty("has_more", out var hm) && hm.ValueKind == JsonValueKind.True;
        string? lastId = items.Count > 0 && items[^1].Id.Length > 0 ? items[^1].Id : null;
        return new StripeTxnPage(items, hasMore, lastId);
    }

    /// <summary>Builds the 30-day revenue view from charges, in the most common currency.</summary>
    public static StripeRevenue BuildStripeRevenue(IEnumerable<StripeTxn> txns, DateTimeOffset nowUtc, TimeZoneInfo tz)
    {
        var list = txns.ToList();
        string currency = MostCommon(list.Select(t => t.Currency));
        var daily = DailySeries(
            list.Where(t => t.Currency == currency).Select(t => (t.Created, MinorToMajor(t.Amount, currency))),
            nowUtc, tz, Days);

        var today = TimeZoneInfo.ConvertTime(nowUtc, tz).Date;
        double month = 0;
        int best = Days - 1;
        for (int i = 0; i < Days; i++)
        {
            var day = today.AddDays(i - (Days - 1));
            if (day.Year == today.Year && day.Month == today.Month) month += daily[i];
            if (daily[i] > daily[best]) best = i;
        }
        double last7 = daily.Skip(Days - 7).Sum();
        double prev7 = daily.Skip(Days - 14).Take(7).Sum();
        double? change = prev7 > 0 ? (last7 - prev7) / prev7 * 100 : null;
        return new StripeRevenue(currency, daily[^1], month, daily.Sum(), daily, change, daily[best],
            today.AddDays(best - (Days - 1)));
    }

    // ---- Lemon Squeezy --------------------------------------------------------------------------

    /// <summary>Parses GET https://api.lemonsqueezy.com/v1/stores (JSON:API).</summary>
    public static LemonSqueezyRevenue ParseLemonSqueezyStores(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = Root(doc);
        var stores = new List<LemonStore>();
        foreach (var s in Array(root, "data"))
        {
            var a = Child(s, "attributes");
            string cur = Currency(Str(a, "currency"));
            string name = Str(a, "name");
            stores.Add(new LemonStore(
                name.Length > 0 ? name : Str(s, "id"),
                cur,
                MinorToMajor(Num(a, "total_revenue"), cur),
                MinorToMajor(Num(a, "thirty_day_revenue"), cur),
                (long)Num(a, "total_sales"),
                (long)Num(a, "thirty_day_sales")));
        }
        return new LemonSqueezyRevenue(stores);
    }

    // ---- Gumroad --------------------------------------------------------------------------------

    /// <summary>Parses GET https://api.gumroad.com/v2/products.</summary>
    public static GumroadSales ParseGumroadProducts(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = Root(doc);
        bool ok = root.TryGetProperty("success", out var s) && s.ValueKind == JsonValueKind.True;
        string message = Str(root, "message");
        if (!ok) return new GumroadSales(false, message.Length > 0 ? message : null, []);
        var products = new List<GumroadProduct>();
        foreach (var p in Array(root, "products"))
        {
            bool published = !p.TryGetProperty("published", out var pub) || pub.ValueKind != JsonValueKind.False;
            products.Add(new GumroadProduct(
                Str(p, "name"),
                (long)Num(p, "sales_count"),
                Num(p, "sales_usd_cents") / 100.0,
                Str(p, "formatted_price"),
                published));
        }
        products.Sort((a, b) => b.SalesUsd.CompareTo(a.SalesUsd));
        return new GumroadSales(true, null, products);
    }

    // ---- Polar ----------------------------------------------------------------------------------

    /// <summary>Parses GET https://api.polar.sh/v1/orders/ (newest first); pending and refunded orders are skipped.</summary>
    public static PolarRevenue ParsePolarOrders(string json, DateTimeOffset nowUtc, TimeZoneInfo tz)
    {
        using var doc = JsonDocument.Parse(json);
        var root = Root(doc);
        var orders = new List<PolarOrder>();
        foreach (var o in Array(root, "items"))
        {
            string status = Str(o, "status");
            if (status is "pending" or "refunded") continue;
            string cur = Currency(Str(o, "currency"));
            double minor = o.TryGetProperty("net_amount", out _) ? Num(o, "net_amount") : Num(o, "amount");
            var created = Iso(Str(o, "created_at"));
            orders.Add(new PolarOrder(
                MinorToMajor(minor, cur),
                cur,
                created?.UtcDateTime ?? DateTime.MinValue,
                Str(Child(o, "product"), "name"),
                MaskEmail(Str(Child(o, "customer"), "email"))));
        }

        var since = nowUtc.UtcDateTime.AddDays(-Days);
        var recent = orders.Where(o => o.CreatedUtc >= since).ToList();
        string currency = MostCommon((recent.Count > 0 ? recent : orders).Select(o => o.Currency));
        var inCurrency = recent.Where(o => o.Currency == currency).ToList();
        var counts = DailySeries(
            recent.Select(o => (new DateTimeOffset(DateTime.SpecifyKind(o.CreatedUtc, DateTimeKind.Utc)), 1.0)), nowUtc, tz, 7);
        return new PolarRevenue(
            currency,
            inCurrency.Sum(o => o.Amount),
            recent.Count,
            orders.OrderByDescending(o => o.CreatedUtc).Take(5).ToList(),
            counts.Select(c => (int)c).ToList());
    }

    // ---- Paddle ---------------------------------------------------------------------------------

    /// <summary>Parses one page of GET https://api.paddle.com/transactions.</summary>
    public static PaddleTxnPage ParsePaddleTransactions(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = Root(doc);
        var items = new List<PaddleTxn>();
        foreach (var t in Array(root, "data"))
        {
            string cur = Currency(Str(t, "currency_code"));
            var totals = Child(Child(t, "details"), "totals");
            items.Add(new PaddleTxn(cur, MinorToMajor(Num(totals, "grand_total"), cur), Iso(Str(t, "created_at")) ?? DateTimeOffset.MinValue));
        }
        var pagination = Child(Child(root, "meta"), "pagination");
        bool hasMore = pagination.ValueKind == JsonValueKind.Object
            && pagination.TryGetProperty("has_more", out var hm) && hm.ValueKind == JsonValueKind.True;
        string next = Str(pagination, "next");
        bool safe = next.StartsWith("https://api.paddle.com/", StringComparison.OrdinalIgnoreCase)
            || next.StartsWith("https://sandbox-api.paddle.com/", StringComparison.OrdinalIgnoreCase);
        return new PaddleTxnPage(items, hasMore && safe ? next : null);
    }

    /// <summary>Builds the 30-day view (local days, the last is today) in the most common currency.</summary>
    public static PaddleRevenue BuildPaddleRevenue(IEnumerable<PaddleTxn> txns, DateTimeOffset nowUtc, TimeZoneInfo tz)
    {
        var since = LocalMidnightUtc(nowUtc, tz, Days - 1);
        var recent = txns.Where(t => t.Created >= since && t.Created <= nowUtc.AddMinutes(5)).ToList();
        string currency = MostCommon(recent.Select(t => t.Currency));
        var mine = recent.Where(t => t.Currency == currency).ToList();
        var daily = DailySeries(mine.Select(t => (t.Created, t.Amount)), nowUtc, tz, Days);
        return new PaddleRevenue(currency, mine.Sum(t => t.Amount), mine.Count, daily);
    }
}
