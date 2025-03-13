using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ConnectX.Server.Models.ZeroTier;
using ConnectX.Server.Interfaces;

namespace ConnectX.Server.Services;

public class PeerInfoService : BackgroundService
{
    private DateTime _lastRefreshTime = DateTime.MinValue;

    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly ILogger _logger;

    public IReadOnlyList<NetworkPeerModel> NetworkPeers { get; private set; } = [];

    public PeerInfoService(
        IServiceScopeFactory serviceScopeFactory,
        ILogger<PeerInfoService> logger)
    {
        _serviceScopeFactory = serviceScopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if ((DateTime.Now - _lastRefreshTime).TotalSeconds < 5)
            {
                await Task.Delay(1000, stoppingToken);
                continue;
            }

            await using var scope = IZeroTierNodeInfoService.CreateZtApi(_serviceScopeFactory, out var zeroTierApiService);

            ImmutableList<NetworkPeerModel>? peers;

            try
            {
                var status = await zeroTierApiService.GetNetworkPeersAsync(stoppingToken);
                peers = status?.ToImmutableList();
            }
            catch (HttpRequestException e)
            {
                _logger.LogFailedToGetNetworkPeers(e);

                await Task.Delay(1000, stoppingToken);
                continue;
            }

            if (peers == null || peers.Count == 0)
            {
                _logger.LogPeerInfoNotReady();

                await Task.Delay(1000, stoppingToken);
                continue;
            }

            NetworkPeers = peers;
            _lastRefreshTime = DateTime.Now;
        }
    }
}

internal static partial class PeerInfoServiceLoggers
{
    [LoggerMessage(LogLevel.Error, "Failed to get network peers")]
    public static partial void LogFailedToGetNetworkPeers(this ILogger logger, Exception ex);

    [LoggerMessage(LogLevel.Warning, "Peer info not ready")]
    public static partial void LogPeerInfoNotReady(this ILogger logger);
}