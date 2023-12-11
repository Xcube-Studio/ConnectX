using Hive.Both.General.Dispatchers;
using Hive.Codec.Abstractions;
using Hive.Codec.MemoryPack;
using Hive.Codec.Shared;
using Hive.Network.Abstractions.Session;
using Hive.Network.Tcp;
using Hive.Network.Udp;
using Microsoft.Extensions.DependencyInjection;

namespace ConnectX.Shared.Helpers;

public static class InitHelper
{
    public static void Init()
    {
    }

    public static void AddConnectX(this IServiceCollection services)
    {
        services.AddSingleton<ICustomCodecProvider, DefaultCustomCodecProvider>();
        services.AddSingleton<IPacketIdMapper, DefaultPacketIdMapper>();
        services.AddSingleton<IPacketCodec, MemoryPackPacketCodec>();
        services.AddSingleton<IDispatcher, DefaultDispatcher>();
        
        // TCP
        services.AddTransient<TcpSession>();
        services.AddSingleton<IAcceptor<TcpSession>, TcpAcceptor>();
        services.AddSingleton<IConnector<TcpSession>, TcpConnector>();

        // UDP
        services.AddTransient<UdpSession>();
        services.AddSingleton<IAcceptor<UdpSession>, UdpAcceptor>();
        services.AddSingleton<IConnector<UdpSession>, UdpConnector>();
    }
}