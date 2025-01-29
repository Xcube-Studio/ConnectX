using ConnectX.Client.Interfaces;
using ConnectX.Shared.Messages.Group;
using Hive.Both.General.Dispatchers;
using Microsoft.Extensions.Logging;

namespace ConnectX.Client.Managers;

public class RoomInfoManager(
    IDispatcher dispatcher,
    IServerLinkHolder serverLinkHolder,
    ILogger<RoomInfoManager> logger) : IRoomInfoManager
{
    public GroupInfo? CurrentGroupInfo { get; private set; }

    public void ClearRoomInfo()
    {
        CurrentGroupInfo = null;
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

        return groupInfo;
    }
}