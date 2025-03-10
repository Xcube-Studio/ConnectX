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

            ArgumentException.ThrowIfNullOrEmpty(listenAddressStr);

            var serverAddress = IPAddress.Parse(listenAddressStr);

            return new DefaultServerSettingProvider
            {
                ServerAddress = serverAddress,
                ServerPort = port,
                JoinP2PNetwork = true,
                ServerId = serverId,
                EndPoint = new IPEndPoint(serverAddress, port)
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
