using ConnectX.Server.Interfaces;
using ConnectX.Server.Managers;
using ConnectX.Shared.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace ConnectX.Server;

public class Program
{
    static void Main(string[] args)
    {
        var builder = Host
            .CreateDefaultBuilder(args)
            .UseSerilog((hostingContext, loggerConfiguration) => loggerConfiguration
                .ReadFrom.Configuration(hostingContext.Configuration)
                .Enrich.FromLogContext()
                .WriteTo.Console());

        builder.ConfigureServices(services =>
        {
            services.AddSingleton<IServerSettingProvider, ConfigSettingProvider>();
            services.AddConnectX();

            services.AddSingleton<QueryManager>();
            services.AddSingleton<ClientManager>();
            services.AddSingleton<GroupManager>();
            services.AddSingleton<P2PManager>();
            services.AddHostedService<NatTestService>();
            services.AddHostedService<Server>();
        });

        var app = builder.Build();

        app.Run();
    }
}