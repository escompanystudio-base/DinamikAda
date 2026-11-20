using DinamikAda.Core.Accounts;
using DinamikAda.Core.Config;
using DinamikAda.Core.Widgets;
using Xunit;

namespace DinamikAda.Tests;

public sealed class AccountCoreTests
{
    [Fact]
    public void EveryAccountWidget_PointsToAKnownServiceWithSteps()
    {
        var accountWidgets = DockWidgetCatalog.All.Where(d => d.Account is not null).ToList();
        Assert.NotEmpty(accountWidgets);
        foreach (var w in accountWidgets)
        {
            var svc = AccountCatalog.Find(w.Account);
            Assert.True(svc is not null, $"{w.Id} → unknown service {w.Account}");
            Assert.NotEmpty(svc!.Fields);
            Assert.NotEmpty(svc.StepsTr);
            Assert.Equal(svc.StepsTr.Count, svc.StepsEn.Count);
            Assert.StartsWith("https://", svc.DashboardUrl);
        }
    }

    [Fact]
    public void ServiceIds_AreUniqueAndFieldsHaveKeys()
    {
        Assert.Equal(AccountCatalog.All.Count, AccountCatalog.All.Select(s => s.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (var s in AccountCatalog.All)
        {
            Assert.All(s.Fields, f => Assert.False(string.IsNullOrWhiteSpace(f.Key)));
            Assert.Contains(s.Fields, f => !f.Optional);
        }
    }

    [Fact]
    public void IsComplete_IgnoresOptionalFields()
    {
        var github = AccountCatalog.Find("github")!;
        Assert.True(AccountCatalog.IsComplete(github, k => k == "user" ? "octocat" : null));
        Assert.False(AccountCatalog.IsComplete(github, _ => null));
        var plausible = AccountCatalog.Find("plausible")!;
        Assert.False(AccountCatalog.IsComplete(plausible, k => k == "key" ? "x" : null));
        Assert.True(AccountCatalog.IsComplete(plausible, k => k is "key" or "site" ? "x" : null));
    }

    [Fact]
    public void Sanitize_DropsUnknownServicesAndDuplicates()
    {
        var cfg = new AppConfig
        {
            DockAccounts =
            [
                new DockAccount { Service = "github", Values = new() { ["user"] = "a" } },
                new DockAccount { Service = "GitHub", Values = new() { ["user"] = " b " } },
                new DockAccount { Service = "nope", Values = new() { ["key"] = "x" } }
            ]
        };
        cfg.Sanitize();
        var acc = Assert.Single(cfg.DockAccounts);
        Assert.Equal("github", acc.Service);
        Assert.Equal("b", acc.Values["user"]);
    }

    [Fact]
    public void Parsers_DeepSeekBalance()
    {
        var b = SocialMailParsers.ParseDeepSeekBalance(
            """{"is_available":true,"balance_infos":[{"currency":"CNY","total_balance":"110.00","granted_balance":"10.00","topped_up_balance":"100.00"},{"currency":"USD","total_balance":"5.5","granted_balance":"0","topped_up_balance":"5.5"}]}""");
        Assert.True(b.IsAvailable);
        Assert.Equal(2, b.Items.Count);
        Assert.Equal("USD", b.Primary!.Currency);
        Assert.Equal(110.0, b.Items[0].Total, 3);
    }
}
