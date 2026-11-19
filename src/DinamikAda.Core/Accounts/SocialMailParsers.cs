using System.Globalization;
using System.Text.Json;

namespace DinamikAda.Core.Accounts;

/// <summary>DeepSeek balance (one entry per currency).</summary>
public sealed record DeepSeekBalance(bool IsAvailable, IReadOnlyList<DeepSeekBalanceItem> Items)
{
    public DeepSeekBalanceItem? Primary => Items.FirstOrDefault(i => i.Currency == "USD") ?? Items.FirstOrDefault();
}

public sealed record DeepSeekBalanceItem(string Currency, double Total, double Granted, double ToppedUp);

/// <summary>Pure JSON/text parsers for the social, mail, calendar, AI and app account widgets (unit tested).</summary>
public static class SocialMailParsers
{
    /// <summary>Parses GET https://api.deepseek.com/user/balance.</summary>
    public static DeepSeekBalance ParseDeepSeekBalance(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        bool available = root.TryGetProperty("is_available", out var a) && a.ValueKind == JsonValueKind.True;
        var items = new List<DeepSeekBalanceItem>();
        if (root.TryGetProperty("balance_infos", out var infos) && infos.ValueKind == JsonValueKind.Array)
        {
            foreach (var i in infos.EnumerateArray())
            {
                items.Add(new DeepSeekBalanceItem(
                    i.TryGetProperty("currency", out var c) ? c.GetString() ?? "USD" : "USD",
                    Num(i, "total_balance"),
                    Num(i, "granted_balance"),
                    Num(i, "topped_up_balance")));
            }
        }
        return new DeepSeekBalance(available, items);
    }

    /// <summary>Reads a number that may be sent as a JSON number or a numeric string.</summary>
    public static double Num(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var v)) return 0;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDouble(),
            JsonValueKind.String when double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
            _ => 0
        };
    }
}
