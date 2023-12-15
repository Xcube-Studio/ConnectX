using ConnectX.Client.Interfaces;
using ConnectX.Client.Managers;
using ConnectX.Client.Route;
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
        
        // Router
        services.AddSingleton<RouterPacketDispatcher>();
        services.AddSingleton<RouteTable>();
        services.AddSingleton<Router>();
        services.AddHostedService(sp => sp.GetRequiredService<Router>());
        
        services.AddSingleton<IServerLinkHolder, ServerLinkHolder>();
        services.AddHostedService(sp => sp.GetRequiredService<IServerLinkHolder>());
        
        services.AddSingleton<PeerManager>();
        services.AddHostedService(sp => sp.GetRequiredService<PeerManager>());
        
        services.AddSingleton<UpnpManager>();
        services.AddHostedService(sp => sp.GetRequiredService<UpnpManager>());

        services.AddSingleton<PartnerManager>();
        services.AddSingleton<Client>();
    }
}