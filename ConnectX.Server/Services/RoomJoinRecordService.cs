using System.Collections.Concurrent;
using System.Collections.Frozen;
using ConnectX.Server.Managers;
using ConnectX.Server.Models.Contexts;
using ConnectX.Server.Models.DataBase;
using ConnectX.Shared.Messages.Group;
using Hive.Both.General.Dispatchers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ConnectX.Server.Services;

public class RoomJoinRecordService : BackgroundService
{
    private readonly ConcurrentDictionary<Guid, DateTime> _lastRefreshTimes = new();
    private readonly ConcurrentQueue<UpdateRoomMemberNetworkInfo> _roomInfoUpdateQueue = [];

    private readonly PeerInfoService _peerInfoService;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly ILogger<RoomJoinRecordService> _logger;

    public RoomJoinRecordService(
        PeerInfoService peerInfoService,
        IDispatcher dispatcher,
        IServiceScopeFactory serviceScopeFactory,
        ILogger<RoomJoinRecordService> logger)
    {
        _peerInfoService = peerInfoService;
        _serviceScopeFactory = serviceScopeFactory;
        _logger = logger;

        dispatcher.AddHandler<UpdateRoomMemberNetworkInfo>(OnReceivedRoomInfoUpdate);
    }

    private void OnReceivedRoomInfoUpdate(MessageContext<UpdateRoomMemberNetworkInfo> ctx)
    {
        if (string.IsNullOrEmpty(ctx.Message.NetworkNodeId)) return;
        if (_lastRefreshTimes.TryGetValue(ctx.Message.UserId, out var time) &&
            (DateTime.UtcNow - time).TotalSeconds < 5)
            return;

        _lastRefreshTimes[ctx.Message.UserId] = DateTime.UtcNow;
        _roomInfoUpdateQueue.Enqueue(ctx.Message);
    }

    private void RefreshTimeCleanup()
    {
        var now = DateTime.UtcNow;
        var toRemove = new List<Guid>();

        foreach (var (userId, time) in _lastRefreshTimes)
        {
            if ((now - time).TotalSeconds < 5) continue;
            toRemove.Add(userId);
        }

        foreach (var userId in toRemove)
            _lastRefreshTimes.TryRemove(userId, out _);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using var scope = _serviceScopeFactory.CreateAsyncScope();

        var dbContext = scope.ServiceProvider.GetRequiredService<RoomOpsHistoryContext>();
        var groupManager = scope.ServiceProvider.GetRequiredService<GroupManager>();

        while (!stoppingToken.IsCancellationRequested)
        {
            if (_roomInfoUpdateQueue.IsEmpty)
            {
                await Task.Delay(500, stoppingToken);
                continue;
            }

            RefreshTimeCleanup();

            if (!_roomInfoUpdateQueue.TryDequeue(out var update))
            {
                await Task.Delay(10, stoppingToken);
                continue;
            }

            if (!groupManager.TryGetGroup(update.RoomId, out var group))
            {
                _logger.LogFailedToGetGroup(update.RoomId);
                continue;
            }

            var peerInfo = _peerInfoService.NetworkPeers
                .FirstOrDefault(x => x.Address.Equals(update.NetworkNodeId, StringComparison.OrdinalIgnoreCase));

            if (peerInfo?.Paths == null || peerInfo.Paths.Length == 0)
            {
                _logger.LogPeerInfoNotFound(update.NetworkNodeId);
                _roomInfoUpdateQueue.Enqueue(update);

                await Task.Delay(10, stoppingToken);
                continue;
            }

            var addresses = peerInfo.Paths
                .Where(p => p.Active)
                .Select(p => p.Address)
                .Where(a => !string.IsNullOrEmpty(a))
                .OfType<string>()
                .ToFrozenSet();

            if (addresses.Count == 0)
            {
                _logger.LogPeerAddressNotReady(update.NetworkNodeId);
                _roomInfoUpdateQueue.Enqueue(update);

                await Task.Delay(10, stoppingToken);
                continue;
            }

            var joinHistory = new RoomJoinHistory
            {
                UserId = update.UserId,
                RoomId = update.RoomId,
                LogTime = DateTime.UtcNow,
                NetworkNodeId = update.NetworkNodeId,
                RoomName = group.RoomName,
                UserPhysicalAddress = string.Join(',', addresses)
            };

            dbContext.RoomJoinHistories.Add(joinHistory);
            await dbContext.SaveChangesAsync(stoppingToken);

            _logger.LogRoomJoinRecordAdded(update.UserId, update.RoomId, update.NetworkNodeId, joinHistory.UserPhysicalAddress);
        }
    }
}

internal static partial class RoomOperationRecordServiceLoggers
{
    [LoggerMessage(LogLevel.Error, "[RoomOpsService] Failed to get group with ID [{groupId}]")]
    public static partial void LogFailedToGetGroup(this ILogger logger, Guid groupId);

    [LoggerMessage(LogLevel.Warning, "[RoomOpsService] Peer info not found for address [{address}], retrying...")]
    public static partial void LogPeerInfoNotFound(this ILogger logger, string address);

    [LoggerMessage(LogLevel.Warning, "[RoomOpsService] Peer address not ready for address [{address}], retrying...")]
    public static partial void LogPeerAddressNotReady(this ILogger logger, string address);

    [LoggerMessage(LogLevel.Information, "[RoomOpsService] Room join record added, User [{userId}] Group [{groupId}] Node [{nodeId}] Address [{address}]")]
    public static partial void LogRoomJoinRecordAdded(this ILogger logger, Guid userId, Guid groupId, string nodeId, string address);
}