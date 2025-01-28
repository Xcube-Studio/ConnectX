using ConnectX.Server.Interfaces;
using ConnectX.Server.Models.ZeroTier;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ConnectX.Server;

public class ZeroTierNodeInfoService(
    IZeroTierApiService zeroTierApiService,
    ILogger<ZeroTierNodeInfoService> logger) : BackgroundService, IZeroTierNodeInfoService
{
    public NodeStatusModel? NodeStatus { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogFetchingZtServerNodeStatus();

        var status = await zeroTierApiService.GetNodeStatusAsync(stoppingToken);

        ArgumentNullException.ThrowIfNull(status);

        if (!status.Online)
        {
            logger.LogNodeNotOnline();
            ArgumentOutOfRangeException.ThrowIfEqual(status.Online, false);
        }

        NodeStatus = status;

        logger.LogNodeStatusReceived(status.Address, status.Version);
    }
}

static partial class ZeroTierNodeInfoServiceLoggers
{
    [LoggerMessage(LogLevel.Information, "[ZTNodeService] Fetching ZT server node status...")]
    public static partial void LogFetchingZtServerNodeStatus(this ILogger logger);

    [LoggerMessage(LogLevel.Error, "[ZTNodeService] Node is not online!")]
    public static partial void LogNodeNotOnline(this ILogger logger);

    [LoggerMessage(LogLevel.Information, "[ZTNodeService] Node status received, ID [{address}] Version [{version}]")]
    public static partial void LogNodeStatusReceived(this ILogger logger, string address, string version);
}