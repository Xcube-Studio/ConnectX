using ConnectX.Shared.Helpers;
using ConnectX.Shared.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using STUN.StunResult;

namespace ConnectX.Server;

public class NatTestService(ILogger<NatTestService> logger) : BackgroundService
{
    private readonly ILogger _logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogStartingNatTestService();

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var testResult = await StunHelper.GetNatTypeAsync(cancellationToken: stoppingToken);

                if (testResult == null)
                {
                    _logger.LogFailedToAcquireNatType();
                    await Task.Delay(250, stoppingToken);
                    continue;
                }

                _logger.LogNatType(testResult);
                _logger.LogNatType(StunHelper.ToNatTypes(testResult));

                break;
            }
        }
        catch (Exception e)
        {
            _logger.LogFailedToGetNatType(e);
        }
    }
}

internal static partial class NatTestServiceLoggers
{
    [LoggerMessage(LogLevel.Warning, "[NAT] Failed to fetch NAT type, retrying...")]
    public static partial void LogFailedToAcquireNatType(this ILogger logger);

    [LoggerMessage(LogLevel.Information, "[NAT] Starting NAT test service...")]
    public static partial void LogStartingNatTestService(this ILogger logger);

    [LoggerMessage(LogLevel.Information, "[NAT] NAT type: {testResult}")]
    public static partial void LogNatType(this ILogger logger, StunResult5389? testResult);

    [LoggerMessage(LogLevel.Information, "[NAT] ConnectX NAT type: {natType}")]
    public static partial void LogNatType(this ILogger logger, NatTypes natType);

    [LoggerMessage(LogLevel.Error, "[NAT] Failed to get NAT type.")]
    public static partial void LogFailedToGetNatType(this ILogger logger, Exception ex);
}