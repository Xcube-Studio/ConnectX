using System.Net;
using ConnectX.Client;
using ConnectX.Client.Helpers;
using ConnectX.Client.Interfaces;
using ConnectX.Shared.Helpers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace ConnectX.ClientConsole
{
    internal class Program
    {
        private static IClientSettingProvider GetSettings(IConfiguration configuration)
        {
            var listenAddressStr = configuration.GetValue<string>("Server:ListenAddress");
            var port = configuration.GetValue<ushort>("Server:ListenPort");

            return new DefaultClientSettingProvider
            {
                ServerAddress = IPAddress.Parse(listenAddressStr),
                ServerPort = port
            };
        }
        
        static void Main(string[] args)
        {
            InitHelper.Init();

            var builder = Host.CreateApplicationBuilder(args);
            var services = builder.Services;
            
            services.UseConnectX(() => GetSettings(builder.Configuration));
            
            var app = builder.Build();
            
            app.Run();
        }
    }
}
