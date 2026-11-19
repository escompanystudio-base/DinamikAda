using DinamikAda.App.Services.DockWidgets;
using DinamikAda.Core.Widgets;

namespace DinamikAda.App.Views.DockWidgets.Accounts;

/// <summary>Developer and analytics account widgets: github-activity, github-notifications, linear-issues, resend-emails, plausible-visitors.</summary>
public static class DeveloperAccountTiles
{
    public static void RegisterFetchers(Action<string, string, AccountFetcher> add)
    {
    }

    public static void Register(Dictionary<string, Func<DockWidgetDescriptor, DockWidgetContext, IDockWidgetTile>> map)
    {
    }
}
