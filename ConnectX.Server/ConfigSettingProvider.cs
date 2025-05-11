using System.Net;
using ConnectX.Server.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

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
            logger.CanNotParseListenAddressToIpAddress();
            throw new Exception("Can not parse the Server:ListenAddress to IPAddress");
        }

        ServerName = configuration.GetValue<string>("Server:ServerName") ?? listenAddressStr;
        ServerMotd = configuration.GetValue<string>("Server:ServerMotd") ?? "SERVER_DOSE_NOT_PROVIDE_MOTD";

        ServerId = configuration.GetValue<Guid>("Server:ServerId");
        ListenPort = configuration.GetValue<ushort>("Server:ListenPort");
        EndPoint = new IPEndPoint(ListenAddress, ListenPort);

        var publicServerAddress = configuration.GetValue<string>("Server:PublicListenAddress");
        var publicServerPort = configuration.GetValue<ushort>("Server:PublicListenPort");

        if (IPEndPoint.TryParse($"{publicServerAddress}:{publicServerPort}", out var publicEndPoint))
        {
            ServerPublicEndPoint = publicEndPoint;
        }
        else
        {
            logger.CanNotParseListenAddressToIpAddress();
            throw new Exception("Can not parse the server public listen address!");
        }

        logger.PreparingToStartServerOnEndpoint(EndPoint);
    }

    public string ServerName { get; }
    public string ServerMotd { get; }

    public IPEndPoint ServerPublicEndPoint { get; }

    public IPEndPoint EndPoint { get; }
    public IPAddress ListenAddress { get; }
    public ushort ListenPort { get; }
    public Guid ServerId { get; }
}

internal static partial class ConfigSettingProviderLoggers
{
    [LoggerMessage(LogLevel.Critical, "Can not parse the Server:ListenAddress to IPAddress")]
    public static partial void CanNotParseListenAddressToIpAddress(this ILogger<ConfigSettingProvider> logger);

    [LoggerMessage(LogLevel.Information, "Preparing to start server on endpoint [{endPoint}]")]
    public static partial void PreparingToStartServerOnEndpoint(this ILogger<ConfigSettingProvider> logger,
        IPEndPoint endPoint);
}