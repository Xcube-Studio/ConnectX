using ConnectX.Server.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Net;

namespace ConnectX.Server;

public class ConfigSettingProvider : IServerSettingProvider
{
    public ConfigSettingProvider(
        IConfiguration configuration,
        ILogger<ConfigSettingProvider> logger)
    {
        var listenAddressStr = configuration.GetValue<string>("Server:ListenAddress");
        if (IPAddress.TryParse(listenAddressStr, out var listenAddress))
        {
            ListenAddress = listenAddress;
        }
        else
        {
            logger.LogCritical("Can not parse the Server:ListenAddress to IPAddress");
            throw new Exception("Can not parse the Server:ListenAddress to IPAddress");
        }

        ListenPort = configuration.GetValue<ushort>("Server:ListenPort");
        EndPoint = new IPEndPoint(ListenAddress, ListenPort);

        logger.LogInformation(
            "Preparing to start server on endpoint [{endPoint}]",
            EndPoint);
    }

    public IPEndPoint EndPoint { get; }
    public IPAddress ListenAddress { get; }
    public ushort ListenPort { get; }
}