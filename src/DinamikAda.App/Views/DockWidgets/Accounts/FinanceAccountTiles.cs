using DinamikAda.App.Services.DockWidgets;
using DinamikAda.Core.Widgets;

namespace DinamikAda.App.Views.DockWidgets.Accounts;

/// <summary>Finance account widgets: stripe-revenue, lemonsqueezy-revenue, gumroad-sales, polar-revenue, paddle-revenue.</summary>
public static class FinanceAccountTiles
{
    public static void RegisterFetchers(Action<string, string, AccountFetcher> add)
    {
    }

    public static void Register(Dictionary<string, Func<DockWidgetDescriptor, DockWidgetContext, IDockWidgetTile>> map)
    {
    }
}
