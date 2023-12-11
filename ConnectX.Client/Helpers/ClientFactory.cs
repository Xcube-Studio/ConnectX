using ConnectX.Client.Interfaces;
using Hive.Both.General.Dispatchers;
using Hive.Codec.Abstractions;
using Hive.Codec.MemoryPack;
using Hive.Codec.Shared;
using Hive.Network.Abstractions.Session;
using Hive.Network.Tcp;
using Hive.Network.Udp;
using Microsoft.Extensions.DependencyInjection;

namespace ConnectX.Client.Helpers;

public static class ClientFactory
{
    public static void UseConnectX(
        this IServiceCollection services,
        Func<IClientSettingProvider> settingGetter)
    {
        services.AddSingleton(_ => settingGetter());
        
        services.AddSingleton<ICustomCodecProvider, DefaultCustomCodecProvider>();
        services.AddSingleton<IPacketIdMapper, DefaultPacketIdMapper>();
        services.AddSingleton<IPacketCodec, MemoryPackPacketCodec>();
        services.AddSingleton<IDispatcher, DefaultDispatcher>();
        
        // TCP
        services.AddTransient<TcpSession>();
        services.AddSingleton<IAcceptor<TcpSession>, TcpAcceptor>();
        services.AddSingleton<IConnector<TcpSession>, TcpConnector>();

        // UDP
        services.AddTransient<TcpSession>();
        services.AddSingleton<IAcceptor<UdpSession>, UdpAcceptor>();
        services.AddSingleton<IConnector<UdpSession>, UdpConnector>();

        services.AddSingleton<IServerLinkHolder, ServerLinkHolder>();
        services.AddHostedService(sp => sp.GetRequiredService<IServerLinkHolder>());
    }
}