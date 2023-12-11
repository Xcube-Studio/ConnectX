using ConnectX.Client.Interfaces;
using ConnectX.Shared.Helpers;
using Microsoft.Extensions.DependencyInjection;

namespace ConnectX.Client.Helpers;

public static class ClientFactory
{
    public static void UseConnectX(
        this IServiceCollection services,
        Func<IClientSettingProvider> settingGetter)
    {
        services.AddSingleton(_ => settingGetter());
        services.AddConnectX();
        services.AddSingleton<IServerLinkHolder, ServerLinkHolder>();
        services.AddHostedService(sp => sp.GetRequiredService<IServerLinkHolder>());
    }
}