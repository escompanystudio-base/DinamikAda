using System.Globalization;
using System.Text.Json;
using DinamikAda.Core.Accounts;
using Xunit;

namespace DinamikAda.Tests;

public class FinanceAccountParserTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 19, 15, 0, 0, TimeSpan.Zero);
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    private static long Unix(DateTimeOffset t) => t.ToUnixTimeSeconds();
    private static string Iso(DateTimeOffset t) => t.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    // ---- shared helpers -------------------------------------------------------------------------

    [Theory]
    [InlineData(1999, "usd", 19.99)]
    [InlineData(1999, "USD", 19.99)]
    [InlineData(1500, "jpy", 1500)]
    [InlineData(25000, "KRW", 25000)]
    [InlineData(0, "eur", 0)]
    public void Minor_units_convert_with_zero_decimal_currencies(long minor, string currency, double expected)
    {
        Assert.Equal(expected, FinanceParsers.MinorToMajor(minor, currency), 6);
    }

    [Fact]
    public void Local_midnight_days_back_is_converted_to_utc()
    {
        var istanbul = TimeZoneInfo.CreateCustomTimeZone("TR", TimeSpan.FromHours(3), "TR", "TR");
        var t = FinanceParsers.LocalMidnightUtc(Now, istanbul, 29);
        // Local now is 2026-11-19 18:00 (+03); 29 days back = 2026-10-21 00:00 local = 2026-10-20 21:00 UTC.
        Assert.Equal(new DateTimeOffset(2026, 10, 20, 21, 0, 0, TimeSpan.Zero), t);
    }

    [Fact]
    public void Masked_email_hides_local_part_and_domain()
    {
        Assert.Equal("j***@e***.com", FinanceParsers.MaskEmail("jane.doe@example.com"));
        Assert.Equal("a***@m***.co.uk", FinanceParsers.MaskEmail("ab@mail.co.uk"));
        Assert.Equal(string.Empty, FinanceParsers.MaskEmail(null));
        Assert.Equal("***", FinanceParsers.MaskEmail("not-an-email"));
        Assert.DoesNotContain("jane", FinanceParsers.MaskEmail("jane.doe@example.com"));
    }

    // ---- Stripe ---------------------------------------------------------------------------------

    private static string StripePage(bool hasMore, params (string Id, long Amount, string Cur, DateTimeOffset At)[] txns)
    {
        var items = txns.Select(t =>
            $$"""{"id":"{{t.Id}}","object":"balance_transaction","amount":{{t.Amount}},"available_on":{{Unix(t.At) + 86400 * 2}},"created":{{Unix(t.At)}},"currency":"{{t.Cur}}","description":"Subscription update","fee":{{t.Amount / 30}},"net":{{t.Amount - t.Amount / 30}},"reporting_category":"charge","source":"ch_3P{{t.Id}}","status":"available","type":"charge"}""");
        return $$"""{"object":"list","data":[{{string.Join(",", items)}}],"has_more":{{(hasMore ? "true" : "false")}},"url":"/v1/balance_transactions"}""";
    }

    [Fact]
    public void Stripe_page_reads_items_has_more_and_last_id()
    {
        var page = FinanceParsers.ParseStripeBalanceTransactions(StripePage(true,
            ("txn_1", 2500, "usd", Now.AddHours(-1)),
            ("txn_2", 4900, "usd", Now.AddDays(-2))));
        Assert.True(page.HasMore);
        Assert.Equal("txn_2", page.LastId);
        Assert.Equal(2, page.Items.Count);
        Assert.Equal(2500, page.Items[0].Amount);
        Assert.Equal("USD", page.Items[0].Currency);
        Assert.Equal(Unix(Now.AddHours(-1)), page.Items[0].Created.ToUnixTimeSeconds());
    }

    [Fact]
    public void Stripe_page_tolerates_missing_fields_and_string_numbers()
    {
        var page = FinanceParsers.ParseStripeBalanceTransactions(
            """{"object":"list","data":[{"id":"txn_a","amount":"1200","currency":"eur","created":"1763560800"},{"id":"txn_b"}]}""");
        Assert.False(page.HasMore);
        Assert.Equal(2, page.Items.Count);
        Assert.Equal(1200, page.Items[0].Amount);
        Assert.Equal(0, page.Items[1].Amount);
        Assert.Equal("txn_b", page.LastId);

        var empty = FinanceParsers.ParseStripeBalanceTransactions("""{"object":"list"}""");
        Assert.Empty(empty.Items);
        Assert.Null(empty.LastId);
    }

    [Fact]
    public void Stripe_invalid_json_throws()
    {
        Assert.ThrowsAny<JsonException>(() => FinanceParsers.ParseStripeBalanceTransactions("<html>oops</html>"));
        Assert.Throws<FormatException>(() => FinanceParsers.ParseStripeBalanceTransactions("[1,2]"));
    }

    [Fact]
    public void Stripe_revenue_sums_per_local_day_in_main_currency()
    {
        var page = FinanceParsers.ParseStripeBalanceTransactions(StripePage(false,
            ("t1", 2500, "usd", Now.AddHours(-2)),           // today
            ("t2", 1500, "usd", Now.AddHours(-3)),           // today
            ("t3", 9900, "usd", Now.AddDays(-1)),            // yesterday (best day)
            ("t4", 4000, "usd", Now.AddDays(-10)),           // earlier this month (Nov 9)
            ("t5", 3000, "usd", Now.AddDays(-25)),           // October 25
            ("t6", 99999, "eur", Now.AddHours(-1)),          // minority currency: ignored
            ("t7", 5000, "usd", Now.AddDays(-40))));         // outside the window
        var r = FinanceParsers.BuildStripeRevenue(page.Items, Now, Utc);

        Assert.Equal("USD", r.Currency);
        Assert.Equal(30, r.Daily.Count);
        Assert.Equal(40.00, r.Today, 6);
        Assert.Equal(40.00, r.Daily[29], 6);
        Assert.Equal(99.00, r.Daily[28], 6);
        Assert.Equal(40 + 99 + 40, r.ThisMonth, 6);
        Assert.Equal(40 + 99 + 40 + 30, r.Last30, 6);
        Assert.Equal(99.00, r.BestDay, 6);
        Assert.Equal(new DateTime(2026, 11, 18), r.BestDayDate);
    }

    [Fact]
    public void Stripe_revenue_change_compares_last_7_days_with_previous_7()
    {
        var page = FinanceParsers.ParseStripeBalanceTransactions(StripePage(false,
            ("a", 15000, "usd", Now.AddDays(-2)),    // last 7 days: 150
            ("b", 10000, "usd", Now.AddDays(-9))));  // previous 7 days: 100
        var r = FinanceParsers.BuildStripeRevenue(page.Items, Now, Utc);
        Assert.NotNull(r.ChangePct);
        Assert.Equal(50.0, r.ChangePct!.Value, 6);

        var none = FinanceParsers.BuildStripeRevenue(
            FinanceParsers.ParseStripeBalanceTransactions(StripePage(false, ("a", 1000, "usd", Now.AddDays(-1)))).Items, Now, Utc);
        Assert.Null(none.ChangePct);
    }

    [Fact]
    public void Stripe_revenue_handles_zero_decimal_and_empty_input()
    {
        var page = FinanceParsers.ParseStripeBalanceTransactions(StripePage(false, ("j", 12000, "jpy", Now.AddHours(-1))));
        var r = FinanceParsers.BuildStripeRevenue(page.Items, Now, Utc);
        Assert.Equal("JPY", r.Currency);
        Assert.Equal(12000, r.Today, 6);

        var empty = FinanceParsers.BuildStripeRevenue([], Now, Utc);
        Assert.Equal("USD", empty.Currency);
        Assert.Equal(30, empty.Daily.Count);
        Assert.Equal(0, empty.Last30);
        Assert.Null(empty.ChangePct);
    }

    // ---- Lemon Squeezy --------------------------------------------------------------------------

    private const string LemonStores = """
        {
          "meta": {"page": {"currentPage": 1, "from": 1, "lastPage": 1, "perPage": 10, "to": 2, "total": 2}},
          "jsonapi": {"version": "1.0"},
          "links": {"first": "https://api.lemonsqueezy.com/v1/stores?page%5Bnumber%5D=1", "last": "https://api.lemonsqueezy.com/v1/stores?page%5Bnumber%5D=1"},
          "data": [
            {
              "type": "stores", "id": "1",
              "attributes": {
                "name": "Pixel Forge", "slug": "pixel-forge", "domain": "pixel-forge.lemonsqueezy.com",
                "url": "https://pixel-forge.lemonsqueezy.com", "avatar_url": "https://example.com/a.png",
                "plan": "fresh", "country": "US", "country_nicename": "United States", "currency": "USD",
                "total_sales": 1287, "total_revenue": 4823450, "thirty_day_sales": 96, "thirty_day_revenue": 318900,
                "created_at": "2023-03-01T10:00:00.000000Z", "updated_at": "2026-11-19T09:00:00.000000Z"
              }
            },
            {
              "type": "stores", "id": "2",
              "attributes": {
                "name": "Side Project", "currency": "USD",
                "total_sales": "40", "total_revenue": "120000", "thirty_day_sales": 3, "thirty_day_revenue": 8700
              }
            }
          ]
        }
        """;

    [Fact]
    public void Lemon_squeezy_reads_every_store_in_major_units()
    {
        var r = FinanceParsers.ParseLemonSqueezyStores(LemonStores);
        Assert.Equal(2, r.Stores.Count);
        var s = r.Stores[0];
        Assert.Equal("Pixel Forge", s.Name);
        Assert.Equal("USD", s.Currency);
        Assert.Equal(48234.50, s.TotalRevenue, 6);
        Assert.Equal(3189.00, s.ThirtyDayRevenue, 6);
        Assert.Equal(1287, s.TotalSales);
        Assert.Equal(96, s.ThirtyDaySales);
        Assert.Equal(1200.00, r.Stores[1].TotalRevenue, 6);
        Assert.Equal(40, r.Stores[1].TotalSales);

        Assert.Equal("USD", r.Currency);
        Assert.Equal(3189.00 + 87.00, r.ThirtyDayRevenue, 6);
        Assert.Equal(99, r.ThirtyDaySales);
        Assert.Equal(48234.50 + 1200.00, r.TotalRevenue, 6);
    }

    [Fact]
    public void Lemon_squeezy_tolerates_missing_attributes_and_empty_list()
    {
        var r = FinanceParsers.ParseLemonSqueezyStores("""{"data":[{"type":"stores","id":"9"}]}""");
        Assert.Single(r.Stores);
        Assert.Equal("USD", r.Stores[0].Currency);
        Assert.Equal(0, r.Stores[0].ThirtyDayRevenue);
        Assert.Empty(FinanceParsers.ParseLemonSqueezyStores("""{"data":[]}""").Stores);
        Assert.Equal(0, FinanceParsers.ParseLemonSqueezyStores("""{"data":[]}""").ThirtyDayRevenue);
    }

    // ---- Gumroad --------------------------------------------------------------------------------

    private const string GumroadProducts = """
        {
          "success": true,
          "products": [
            {"custom_permalink": null, "custom_receipt": null, "customizable_price": false, "description": "Icons",
             "deleted": false, "max_purchase_count": null, "name": "Minimal Icon Pack", "preview_url": null,
             "require_shipping": false, "subscription_duration": null, "published": true,
             "url": "https://example.gumroad.com/l/icons", "id": "A-m3CDDC5dlrSdKZp0RFhA==", "price": 1900,
             "currency": "usd", "short_url": "https://example.gumroad.com/l/icons", "formatted_price": "$19",
             "file_info": {}, "sales_count": 412, "sales_usd_cents": 782800},
            {"name": "Notion Finance Template", "published": true, "price": 2900, "currency": "usd",
             "formatted_price": "$29", "sales_count": "1034", "sales_usd_cents": "2998600"},
            {"name": "Old Course (draft)", "published": false, "formatted_price": "$49", "sales_count": 0, "sales_usd_cents": 0}
          ]
        }
        """;

    [Fact]
    public void Gumroad_reads_products_sorted_by_revenue()
    {
        var r = FinanceParsers.ParseGumroadProducts(GumroadProducts);
        Assert.True(r.Success);
        Assert.Equal(3, r.Products.Count);
        Assert.Equal("Notion Finance Template", r.Products[0].Name);
        Assert.Equal(29986.00, r.Products[0].SalesUsd, 6);
        Assert.Equal(1034, r.Products[0].SalesCount);
        Assert.Equal("$19", r.Products[1].FormattedPrice);
        Assert.False(r.Products[2].Published);
        Assert.Equal(29986.00 + 7828.00, r.TotalUsd, 6);
        Assert.Equal(1446, r.TotalSales);
    }

    [Fact]
    public void Gumroad_reports_failure_with_message()
    {
        var r = FinanceParsers.ParseGumroadProducts("""{"success": false, "message": "The access token is invalid."}""");
        Assert.False(r.Success);
        Assert.Equal("The access token is invalid.", r.Message);
        Assert.Empty(r.Products);

        var missing = FinanceParsers.ParseGumroadProducts("""{"products": []}""");
        Assert.False(missing.Success);
    }

    // ---- Polar ----------------------------------------------------------------------------------

    private static string PolarOrders(params (double Net, double Amount, string Cur, DateTimeOffset At, string Product, string Email, string Status)[] orders)
    {
        var items = orders.Select((o, i) => $$"""
            {"id":"ord_{{i}}","created_at":"{{Iso(o.At)}}","modified_at":null,"status":"{{o.Status}}","paid":true,
             "subtotal_amount":{{o.Amount}},"discount_amount":0,"net_amount":{{o.Net}},"amount":{{o.Amount}},"tax_amount":{{o.Amount - o.Net}},
             "total_amount":{{o.Amount}},"refunded_amount":0,"currency":"{{o.Cur}}","billing_reason":"purchase",
             "customer":{"id":"cus_{{i}}","email":"{{o.Email}}","name":"Customer {{i}}"},
             "product":{"id":"prod_{{i}}","name":"{{o.Product}}","is_recurring":false} }
            """);
        return $$"""{"items":[{{string.Join(",", items)}}],"pagination":{"total_count":{{orders.Length}},"max_page":1} }""";
    }

    [Fact]
    public void Polar_orders_sum_last_30_days_and_keep_latest_five_masked()
    {
        var json = PolarOrders(
            (4500, 4900, "usd", Now.AddMinutes(-30), "Pro License", "maria.lopez@example.com", "paid"),
            (900, 900, "usd", Now.AddHours(-5), "Starter", "ken@studio.io", "paid"),
            (4500, 4900, "usd", Now.AddDays(-1), "Pro License", "a.b@x.dev", "paid"),
            (19900, 19900, "usd", Now.AddDays(-3), "Team Plan", "ops@company.com", "paid"),
            (4500, 4900, "usd", Now.AddDays(-6), "Pro License", "zoe@example.org", "paid"),
            (900, 900, "usd", Now.AddDays(-12), "Starter", "lee@example.net", "paid"),
            (4500, 4900, "usd", Now.AddDays(-14), "Pro License", "x@example.com", "refunded"),
            (900, 900, "usd", Now.AddDays(-45), "Starter", "old@example.com", "paid"));
        var r = FinanceParsers.ParsePolarOrders(json, Now, Utc);

        Assert.Equal("USD", r.Currency);
        Assert.Equal(45 + 9 + 45 + 199 + 45 + 9, r.Last30, 6);
        Assert.Equal(6, r.Count30);
        Assert.Equal(5, r.Latest.Count);
        Assert.Equal("Pro License", r.Latest[0].Product);
        Assert.Equal(45.00, r.Latest[0].Amount, 6);
        Assert.Equal("m***@e***.com", r.Latest[0].MaskedEmail);
        Assert.All(r.Latest, o => Assert.DoesNotContain("example", o.MaskedEmail));
        Assert.True(r.Latest[0].CreatedUtc > r.Latest[1].CreatedUtc);
        Assert.Equal(7, r.DailyCounts.Count);
        Assert.Equal(2, r.DailyCounts[6]); // today
        Assert.Equal(1, r.DailyCounts[5]); // yesterday
    }

    [Fact]
    public void Polar_falls_back_to_amount_and_tolerates_missing_fields()
    {
        var r = FinanceParsers.ParsePolarOrders(
            $$"""{"items":[{"id":"o1","amount":2500,"currency":"eur","created_at":"{{Iso(Now.AddHours(-1))}}"},{"id":"o2"}]}""", Now, Utc);
        Assert.Equal(25.00, r.Last30, 6);
        Assert.Equal("EUR", r.Currency);
        Assert.Equal(1, r.Count30);
        Assert.Equal("", r.Latest[0].MaskedEmail);
        Assert.Equal("", r.Latest[0].Product);

        var empty = FinanceParsers.ParsePolarOrders("""{"items":[]}""", Now, Utc);
        Assert.Empty(empty.Latest);
        Assert.Equal("USD", empty.Currency);
    }

    // ---- Paddle ---------------------------------------------------------------------------------

    private static string PaddlePage(string? next, params (string GrandTotal, string Cur, DateTimeOffset At)[] txns)
    {
        var items = txns.Select((t, i) => $$"""
            {"id":"txn_01h{{i}}","status":"completed","customer_id":"ctm_01","origin":"web","currency_code":"{{t.Cur}}",
             "created_at":"{{Iso(t.At)}}","updated_at":"{{Iso(t.At)}}","billed_at":"{{Iso(t.At)}}",
             "details":{"totals":{"subtotal":"{{t.GrandTotal}}","tax":"0","discount":"0","total":"{{t.GrandTotal}}",
               "grand_total":"{{t.GrandTotal}}","fee":null,"earnings":null,"currency_code":"{{t.Cur}}"} } }
            """);
        string pagination = next is null
            ? """{"per_page":200,"next":"https://api.paddle.com/transactions?after=txn_last","has_more":false,"estimated_total":3}"""
            : $$"""{"per_page":200,"next":"{{next}}","has_more":true,"estimated_total":500}""";
        return $$"""{"data":[{{string.Join(",", items)}}],"meta":{"request_id":"req_1","pagination":{{pagination}} } }""";
    }

    [Fact]
    public void Paddle_page_reads_items_and_next_only_when_more()
    {
        var page = FinanceParsers.ParsePaddleTransactions(PaddlePage("https://api.paddle.com/transactions?after=txn_01h1",
            ("2900", "USD", Now.AddHours(-1)), ("9900", "USD", Now.AddDays(-3))));
        Assert.Equal(2, page.Items.Count);
        Assert.Equal(29.00, page.Items[0].Amount, 6);
        Assert.Equal("USD", page.Items[0].Currency);
        Assert.Equal("https://api.paddle.com/transactions?after=txn_01h1", page.Next);

        var last = FinanceParsers.ParsePaddleTransactions(PaddlePage(null, ("100", "USD", Now)));
        Assert.Null(last.Next);
    }

    [Fact]
    public void Paddle_next_link_to_another_host_is_ignored()
    {
        var page = FinanceParsers.ParsePaddleTransactions(PaddlePage("https://evil.example.com/transactions?after=x", ("100", "USD", Now)));
        Assert.Null(page.Next);
    }

    [Fact]
    public void Paddle_revenue_builds_total_count_average_and_daily_series()
    {
        var items = FinanceParsers.ParsePaddleTransactions(PaddlePage(null,
            ("2900", "USD", Now.AddHours(-1)),
            ("2900", "USD", Now.AddHours(-2)),
            ("9900", "USD", Now.AddDays(-3)),
            ("4900", "USD", Now.AddDays(-29).AddHours(-1)),
            ("500000", "JPY", Now.AddHours(-1)))).Items;
        var r = FinanceParsers.BuildPaddleRevenue(items, Now, Utc);
        Assert.Equal("USD", r.Currency);
        Assert.Equal(4, r.Count);
        Assert.Equal(29 + 29 + 99 + 49, r.Total, 6);
        Assert.Equal((29 + 29 + 99 + 49) / 4.0, r.Average, 6);
        Assert.Equal(30, r.Daily.Count);
        Assert.Equal(58, r.Daily[29], 6);
        Assert.Equal(99, r.Daily[26], 6);
        Assert.Equal(49, r.Daily[0], 6);
    }

    [Fact]
    public void Paddle_tolerates_numbers_and_missing_totals()
    {
        var page = FinanceParsers.ParsePaddleTransactions(
            $$"""{"data":[{"id":"a","currency_code":"EUR","created_at":"{{Iso(Now)}}","details":{"totals":{"grand_total":1500} } },{"id":"b"}],"meta":{} }""");
        Assert.Equal(2, page.Items.Count);
        Assert.Equal(15.00, page.Items[0].Amount, 6);
        Assert.Equal(0, page.Items[1].Amount);
        Assert.Null(page.Next);
        var r = FinanceParsers.BuildPaddleRevenue([], Now, Utc);
        Assert.Equal(0, r.Average);
        Assert.Equal(30, r.Daily.Count);
    }
}
