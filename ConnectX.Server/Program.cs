using ConnectX.Server.Interfaces;
using ConnectX.Server.Managers;
using ConnectX.Shared.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace ConnectX.Server;

public class Program
{
    private static void Main(string[] args)
    {
        var builder = Host
            .CreateDefaultBuilder(args)
            .UseSerilog((hostingContext, loggerConfiguration) => loggerConfiguration
                .ReadFrom.Configuration(hostingContext.Configuration)
                .Enrich.FromLogContext()
                .WriteTo.Console());

        builder.ConfigureServices((ctx, services) =>
        {
            var configuration = ctx.Configuration;

            services.AddHttpClient<IZeroTierApiService, ZeroTierApiService>(client =>
            {
                client.BaseAddress = new Uri(configuration["ZeroTier:EndPoint"]!);
                client.DefaultRequestHeaders.Add("X-ZT1-AUTH", configuration["ZeroTier:Token"]);
            });

            services.AddSingleton<IServerSettingProvider, ConfigSettingProvider>();
            services.AddConnectXEssentials();

            services.AddSingleton<ClientManager>();
            services.AddSingleton<GroupManager>();
            services.AddSingleton<P2PManager>();

            services.AddSingleton<IZeroTierNodeInfoService, ZeroTierNodeInfoService>();
            services.AddHostedService(sc => sc.GetRequiredService<IZeroTierNodeInfoService>());

            services.AddHostedService(sc => sc.GetRequiredService<ClientManager>());
            services.AddHostedService<Server>();
        });

        var app = builder.Build();

        app.Run();
    }
}