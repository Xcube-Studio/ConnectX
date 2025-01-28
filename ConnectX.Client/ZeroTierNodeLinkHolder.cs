using System.Net;
using ConnectX.Client.Interfaces;
using Microsoft.Extensions.Hosting;
using Hive.Common.Shared.Helpers;
using Microsoft.Extensions.Logging;
using ZeroTier.Core;

namespace ConnectX.Client;

public class ZeroTierNodeLinkHolder(ILogger<ZeroTierNodeLinkHolder> logger) : BackgroundService, IZeroTierNodeLinkHolder
{
    private const ulong NetworkId = 0x8011c71f8e;

    public Node Node { get; } = new ();

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogStartingZeroTierNodeLinkHolder();

        Node.Start();

        Node.InitAllowNetworkCaching(false);
        Node.InitAllowPeerCaching(true);
        Node.InitSetEventHandler(OnReceivedZeroTierEvent);

        await TaskHelper.WaitUtil(() => Node.Online, cancellationToken);

        logger.LogZeroTierConnected(
            Node.IdString,
            Node.Version,
            Node.PrimaryPort,
            Node.SecondaryPort,
            Node.TertiaryPort);

        Node.Join(NetworkId);

        await TaskHelper.WaitUtil(() => Node.Networks.Count == 0, cancellationToken);

        logger.LogNetworkJoined();

        var addresses = Node.GetNetworkAddresses(NetworkId);

        ArgumentNullException.ThrowIfNull(addresses);
        ArgumentOutOfRangeException.ThrowIfEqual(addresses.Count, 0);

        logger.LogNumOfAssignedAddresses(addresses.Count);

        foreach (var (index, address) in addresses.Index())
        {
            logger.LogAssignedAddress(index, address);
        }

        await base.StartAsync(cancellationToken);
    }

    private void OnReceivedZeroTierEvent(Event nodeEvent)
    {
        logger.LogEventReceived(nodeEvent.Code, nodeEvent.Name);
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        Node.Free();
        Node.Stop();

        logger.LogZeroTierNodeLinkHolderStopped();

        return base.StopAsync(cancellationToken);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    { 
        // Update peers
        throw new NotImplementedException();
    }
}

internal static partial class ZeroTierNodeLinkHolderLogger
{
    [LoggerMessage(LogLevel.Debug, "[ZeroTier] Event.Code = {eventId} ({eventName})")]
    public static partial void LogEventReceived(this ILogger logger, int eventId, string eventName);

    [LoggerMessage(LogLevel.Information, "[ZeroTier] Starting ZeroTier Node Link Holder")]
    public static partial void LogStartingZeroTierNodeLinkHolder(this ILogger logger);

    [LoggerMessage(LogLevel.Information, "[ZeroTier] ZeroTier connected, ID [{id}] Version [{version}]. Ports: I[{port1}] II[{port2}] III[{port3}]")]
    public static partial void LogZeroTierConnected(this ILogger logger, string id, string version, ushort port1, ushort port2, ushort port3);

    [LoggerMessage(LogLevel.Information, "[ZeroTier] Network joined")]
    public static partial void LogNetworkJoined(this ILogger logger);

    [LoggerMessage(LogLevel.Information, "[ZeroTier] Num of assigned addresses: {count}")]
    public static partial void LogNumOfAssignedAddresses(this ILogger logger, int count);

    [LoggerMessage(LogLevel.Information, "[ZeroTier] Assigned address {index}: {address}")]
    public static partial void LogAssignedAddress(this ILogger logger, int index, IPAddress address);

    [LoggerMessage(LogLevel.Information, "[ZeroTier] ZeroTier Node Link Holder stopped")]
    public static partial void LogZeroTierNodeLinkHolderStopped(this ILogger logger);
}