using System.Net;
using System.Net.Sockets;
using ConnectX.Client.Interfaces;
using ConnectX.Shared.Helpers;
using ConnectX.Shared.Messages.Group;
using Hive.Both.General.Dispatchers;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ConnectX.Client.Managers;

public class RoomInfoManager(
    IDispatcher dispatcher,
    IServerLinkHolder serverLinkHolder,
    IZeroTierNodeLinkHolder zeroTierNodeLinkHolder,
    ILogger<RoomInfoManager> logger) : BackgroundService, IRoomInfoManager
{
    public GroupInfo? CurrentGroupInfo { get; private set; }

    public void ClearRoomInfo()
    {
        CurrentGroupInfo = null;
    }

    public void UpdateRoomMemberInfo(UserInfo userInfo)
    {
        if (CurrentGroupInfo == null) return;

        for (var i = 0; i < CurrentGroupInfo.Users.Length; i++)
        {
            if (CurrentGroupInfo.Users[i].UserId != userInfo.UserId) continue;
            CurrentGroupInfo.Users[i] = userInfo;
            return;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var needToRefreshRoomInfo = false;
        var lastRefreshTime = DateTime.MinValue;
        var hashset = new HashSet<Guid>();

        while (!stoppingToken.IsCancellationRequested)
        {
            if (CurrentGroupInfo == null)
            {
                await Task.Delay(1000, stoppingToken);
                continue;
            }

            await TaskHelper.WaitUntilAsync(zeroTierNodeLinkHolder.IsNodeOnline, stoppingToken);
            await TaskHelper.WaitUntilAsync(zeroTierNodeLinkHolder.IsNetworkReady, stoppingToken);

            if (needToRefreshRoomInfo)
            {
                logger.LogUpdatingRoomInfo();
                await AcquireGroupInfoAsync(CurrentGroupInfo.RoomId);
                needToRefreshRoomInfo = false;
            }

            var self = CurrentGroupInfo.Users.FirstOrDefault(x => x.UserId == serverLinkHolder.UserId);

            if (self == null)
            {
                logger.LogSelfNotFound();
                await Task.Delay(1000, stoppingToken);
                continue;
            }

            if (self.NetworkIpAddresses == null ||
                self.NetworkIpAddresses.Length == 0 ||
                string.IsNullOrEmpty(self.NetworkNodeId))
            {
                needToRefreshRoomInfo = true;

                logger.LogUpdatingSelfInfo();

                var updateInfo = new UpdateRoomMemberNetworkInfo
                {
                    GroupId = CurrentGroupInfo.RoomId,
                    UserId = serverLinkHolder.UserId,
                    NetworkNodeId = zeroTierNodeLinkHolder.Node!.IdString,
                    NetworkIpAddresses = zeroTierNodeLinkHolder.GetIpAddresses()
                };

                var result = await dispatcher.SendAndListenOnce<UpdateRoomMemberNetworkInfo, GroupOpResult>(
                    serverLinkHolder.ServerSession!,
                    updateInfo, stoppingToken);

                if (result is not { Status: GroupCreationStatus.Succeeded })
                {
                    logger.LogFailedToUpdateMemberInfo();
                    await Task.Delay(1000, stoppingToken);
                    continue;
                }

                await Task.Delay(1000, stoppingToken);
                continue;
            }

            if (CurrentGroupInfo.RoomOwnerId != self.UserId &&
                DateTime.UtcNow - lastRefreshTime >= TimeSpan.FromMinutes(1) &&
                CurrentGroupInfo.Users.Length < 2)
            {
                needToRefreshRoomInfo = true;
                lastRefreshTime = DateTime.UtcNow;

                logger.LogUpdatingRoomInfo();

                continue;
            }

            var possibleUsers = new List<UserInfo>();

            foreach (var user in CurrentGroupInfo.Users)
            {
                if (user.UserId == serverLinkHolder.UserId)
                    continue;

                if (user.NetworkIpAddresses == null ||
                    user.NetworkIpAddresses.Length == 0)
                {
                    needToRefreshRoomInfo = true;
                    continue;
                }

                foreach (var address in user.NetworkIpAddresses)
                {
                    if (hashset.Contains(user.UserId)) continue;
                    if (address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (address.GetAddressBytes()[3] == 0)
                        continue;

                    logger.LogPeerDiscovered(address);

                    hashset.Add(user.UserId);
                    possibleUsers.Add(user);
                    break;
                }
            }

            if (possibleUsers.Count > 0)
                OnMemberAddressInfoUpdated?.Invoke(possibleUsers.ToArray());

            await Task.Delay(1000, stoppingToken);
        }
    }

    public async Task<GroupInfo?> AcquireGroupInfoAsync(Guid groupId)
    {
        if (!serverLinkHolder.IsConnected) return null;
        if (!serverLinkHolder.IsSignedIn) return null;

        var message = new AcquireGroupInfo
        {
            GroupId = groupId,
            UserId = serverLinkHolder.UserId
        };
        var groupInfo =
            await dispatcher.SendAndListenOnce<AcquireGroupInfo, GroupInfo>(
                serverLinkHolder.ServerSession!,
                message);

        if (groupInfo == null ||
        groupInfo == GroupInfo.Invalid ||
            groupInfo.Users.Length == 0)
        {
            logger.LogFailedToAcquireGroupInfo(groupId);

            return null;
        }

        CurrentGroupInfo = groupInfo;

        logger.LogRoomInfoAcquired();

        return groupInfo;
    }

    public event Action<UserInfo[]>? OnMemberAddressInfoUpdated;
}

internal static partial class RoomInfoManagerLoggers
{
    [LoggerMessage(LogLevel.Information, "[ROOM_INFO_MANAGER] Room info acquired.")]
    public static partial void LogRoomInfoAcquired(this ILogger logger);

    [LoggerMessage(LogLevel.Error, "[ROOM_INFO_MANAGER] Can not find self in the room info, possible internal error!")]
    public static partial void LogSelfNotFound(this ILogger logger);

    [LoggerMessage(LogLevel.Warning, "[ROOM_INFO_MANAGER] Failed to update current room member info, waiting for the next iteration...")]
    public static partial void LogFailedToUpdateMemberInfo(this ILogger logger);

    [LoggerMessage(LogLevel.Information, "[ROOM_INFO_MANAGER] Room info outdated, updating...")]
    public static partial void LogUpdatingRoomInfo(this ILogger logger);

    [LoggerMessage(LogLevel.Information, "[ROOM_INFO_MANAGER] Self info outdated, updating...")]
    public static partial void LogUpdatingSelfInfo(this ILogger logger);

    [LoggerMessage(LogLevel.Information, "[ROOM_INFO_MANAGER] New peer discovered, address: [{address}].")]
    public static partial void LogPeerDiscovered(this ILogger logger, IPAddress address);
}