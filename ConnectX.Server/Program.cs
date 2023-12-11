using ConnectX.Server.Interfaces;
using ConnectX.Shared.Helpers;
using Hive.Both.General.Dispatchers;
using Hive.Codec.Abstractions;
using Hive.Codec.MemoryPack;
using Hive.Codec.Shared;
using Hive.Network.Abstractions.Session;
using Hive.Network.Tcp;
using Hive.Network.Udp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ConnectX.Server;

public class Program
{
    static void Main(string[] args)
    {
        InitHelper.Init();
        
        var builder = Host.CreateApplicationBuilder(args);
        var services = builder.Services;
        
        services.AddSingleton<IServerSettingProvider, ConfigSettingProvider>();
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

        services.AddSingleton<ClientManager>();
        services.AddHostedService<NatTestService>();
        services.AddHostedService<Server>();

        var app = builder.Build();

        app.Run();
    }
}