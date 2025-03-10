using ConnectX.Shared.Helpers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Serilog;
using System.Net;
using ConnectX.Relay.Interfaces;
using ConnectX.Relay.Managers;
using Microsoft.Extensions.DependencyInjection;

namespace ConnectX.Relay
{
    internal static class Program
    {
        private static IServerSettingProvider GetSettings(IConfiguration configuration)
        {
            var listenAddressStr = configuration.GetValue<string>("Server:ListenAddress");
            var port = configuration.GetValue<ushort>("Server:ListenPort");
            var serverId = configuration.GetValue<Guid>("Server:ServerId");

            var relayListenAddressStr = configuration.GetValue<string>("RelayServer:ListenAddress");
            var relayServerPort = configuration.GetValue<ushort>("RelayServer:ListenPort");

            ArgumentException.ThrowIfNullOrEmpty(listenAddressStr);
            ArgumentException.ThrowIfNullOrEmpty(relayListenAddressStr);

            var serverAddress = IPAddress.Parse(listenAddressStr);
            var relayServerAddress = IPAddress.Parse(relayListenAddressStr);

            return new DefaultServerSettingProvider
            {
                ServerAddress = serverAddress,
                ServerPort = port,
                RelayServerAddress = relayServerAddress,
                RelayServerPort = relayServerPort,
                JoinP2PNetwork = true,
                ServerId = serverId,
                EndPoint = new IPEndPoint(serverAddress, port),
                RelayEndPoint = new IPEndPoint(relayServerAddress, relayServerPort)
            };
        }

        private static void Main(string[] args)
        {
            var builder = Host
                .CreateDefaultBuilder(args)
                .UseSerilog((hostingContext, loggerConfiguration) => loggerConfiguration
                    .ReadFrom.Configuration(hostingContext.Configuration));

            builder.ConfigureServices((ctx, services) =>
            {
                services.AddConnectXEssentials();
                services.AddSingleton(_ => GetSettings(ctx.Configuration));

                services.AddSingleton<IServerLinkHolder, ServerLinkHolder>();
                services.AddHostedService(sP => sP.GetRequiredService<IServerLinkHolder>());

                services.AddSingleton<ClientManager>();
                services.AddSingleton<RelayManager>();

                services.AddHostedService(sc => sc.GetRequiredService<ClientManager>());

                services.AddHostedService<RelayServer>();
            });

            var app = builder.Build();

            app.Run();
        }
    }
}
