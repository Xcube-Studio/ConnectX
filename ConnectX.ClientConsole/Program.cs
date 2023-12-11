using System.Net;
using ConnectX.Client;
using ConnectX.Client.Helpers;
using ConnectX.Client.Interfaces;
using ConnectX.Shared.Helpers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace ConnectX.ClientConsole
{
    internal class Program
    {
        private static IClientSettingProvider GetSettings(IConfiguration configuration)
        {
            var listenAddressStr = configuration.GetValue<string>("Server:ListenAddress");
            var port = configuration.GetValue<ushort>("Server:ListenPort");
            
            ArgumentException.ThrowIfNullOrEmpty(listenAddressStr);

            return new DefaultClientSettingProvider
            {
                ServerAddress = IPAddress.Parse(listenAddressStr),
                ServerPort = port,
                JoinP2PNetwork = true
            };
        }
        
        static void Main(string[] args)
        {
            var builder = Host
                .CreateDefaultBuilder(args)
                .UseSerilog((hostingContext, loggerConfiguration) => loggerConfiguration
                    .ReadFrom.Configuration(hostingContext.Configuration)
                    .Enrich.FromLogContext()
                    .WriteTo.Console());

            builder.ConfigureServices((ctx, services) =>
            {
                services.UseConnectX(() => GetSettings(ctx.Configuration));
            });
            
            var app = builder.Build();
            
            app.Run();
        }
    }
}
