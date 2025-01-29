using ConnectX.Client.Interfaces;
using ConnectX.Client.Managers;
using ConnectX.Client.Route;
using ConnectX.Shared.Interfaces;
using ConnectX.Shared.Messages.Group;
using ConnectX.Shared.Models;
using Hive.Both.General.Dispatchers;
using Microsoft.Extensions.Logging;

namespace ConnectX.Client;

public delegate void GroupStateChangedHandler(GroupUserStates state, UserInfo? userInfo);

public class Client
{
    private readonly IDispatcher _dispatcher;
    private readonly ILogger _logger;

    private readonly Router _router;
    private readonly IRoomInfoManager _roomInfoManager;
    private readonly IServerLinkHolder _serverLinkHolder;
    private readonly IZeroTierNodeLinkHolder _zeroTierNodeLinkHolder;
    private bool _isInGroup;

    public Client(
        Router router,
        PartnerManager _,
        IRoomInfoManager roomInfoManager,
        IDispatcher dispatcher,
        IServerLinkHolder serverLinkHolder,
        IZeroTierNodeLinkHolder zeroTierNodeLinkHolder,
        ILogger<Client> logger)
    {
        _router = router;
        _roomInfoManager = roomInfoManager;
        _dispatcher = dispatcher;
        _serverLinkHolder = serverLinkHolder;
        _zeroTierNodeLinkHolder = zeroTierNodeLinkHolder;
        _logger = logger;

        _dispatcher.AddHandler<GroupUserStateChanged>(OnGroupUserStateChanged);
    }

    public event GroupStateChangedHandler? OnGroupStateChanged;

    private void OnGroupUserStateChanged(MessageContext<GroupUserStateChanged> ctx)
    {
        if (!_serverLinkHolder.IsConnected) return;
        if (!_serverLinkHolder.IsSignedIn) return;
        if (!_isInGroup) return;

        var state = ctx.Message.State;
        var userInfo = ctx.Message.UserInfo;

        if (state == GroupUserStates.Dismissed)
            _isInGroup = false;

        OnGroupStateChanged?.Invoke(state, userInfo);
    }

    private async Task<GroupOpResult?> PerformGroupOpAsync<T>(T message) where T : IRequireAssignedUserId
    {
        var result =
            await _dispatcher.SendAndListenOnce<T, GroupOpResult>(
                _serverLinkHolder.ServerSession!,
                message);

        if (result is { Status: GroupCreationStatus.Succeeded })
            return result;

        _logger.LogFailedToCreateGroup(result?.ErrorMessage ?? "-");
        return result;
    }

    private async Task<(GroupInfo?, GroupCreationStatus, string?)> PerformOpAndGetRoomInfoAsync<T>(T message, CancellationToken ct)
        where T : IRequireAssignedUserId
    {
        var createResult = await PerformGroupOpAsync(message);

        if (createResult == null)
            return (null, GroupCreationStatus.Other, null);

        var groupInfo = await _roomInfoManager.AcquireGroupInfoAsync(createResult.GroupId);

        if (groupInfo == null)
            return (null, GroupCreationStatus.Other, "Failed to acquire group info");

        if (message is JoinGroup)
        {
            if (createResult.Status != GroupCreationStatus.Succeeded)
                return (null, createResult.Status, createResult.ErrorMessage);

            var networkResult = await _zeroTierNodeLinkHolder.JoinNetworkAsync(groupInfo.RoomNetworkId, ct);

            if (!networkResult)
            {
                return (null, GroupCreationStatus.Other, "Failed to join the network");
            }
        }

        if (message is LeaveGroup)
        {
            await _zeroTierNodeLinkHolder.LeaveNetworkAsync(ct);
            _roomInfoManager.ClearRoomInfo();
        }

        _isInGroup = true;

        return (groupInfo, GroupCreationStatus.Succeeded, null);
    }

    public async Task<(GroupInfo? Info, GroupCreationStatus Status, string? Error)> CreateGroupAsync(CreateGroup createGroup, CancellationToken ct)
    {
        if (!_serverLinkHolder.IsConnected) return (null, GroupCreationStatus.Other, "Not connected to the server");
        if (!_serverLinkHolder.IsSignedIn) return (null, GroupCreationStatus.Other, "Not signed in");

        return await PerformOpAndGetRoomInfoAsync(createGroup, ct);
    }

    public async Task<(GroupInfo?, GroupCreationStatus, string?)> JoinGroupAsync(JoinGroup joinGroup, CancellationToken ct)
    {
        if (!_serverLinkHolder.IsConnected)
            return (null, GroupCreationStatus.Other, "Not connected to the server");
        if (!_serverLinkHolder.IsSignedIn)
            return (null, GroupCreationStatus.Other, "Not signed in");

        return await PerformOpAndGetRoomInfoAsync(joinGroup, ct);
    }

    public async Task<(bool, string?)> LeaveGroupAsync(LeaveGroup leaveGroup)
    {
        if (!_serverLinkHolder.IsConnected) return (false, "Not connected to the server");
        if (!_serverLinkHolder.IsSignedIn) return (false, "Not signed in");
        if (!_isInGroup) return (false, "Not in a group");

        var result = await PerformGroupOpAsync(leaveGroup);

        if (result == null)
            return (false, "Failed to leave the group");

        return (result.Status == GroupCreationStatus.Succeeded, result.ErrorMessage);
    }

    public async Task<(bool, string?)> KickUserAsync(KickUser kickUser)
    {
        if (!_serverLinkHolder.IsConnected) return (false, "Not connected to the server");
        if (!_serverLinkHolder.IsSignedIn) return (false, "Not signed in");
        if (!_isInGroup) return (false, "Not in a group");

        var result = await PerformGroupOpAsync(kickUser);

        if (result == null)
            return (false, "Failed to kick user");

        return (result.Status == GroupCreationStatus.Succeeded, result.ErrorMessage);
    }
}

internal static partial class ClientLoggers
{
    [LoggerMessage(LogLevel.Error, "[CLIENT] Failed to acquire group info, group id: {groupId}")]
    public static partial void LogFailedToAcquireGroupInfo(this ILogger logger, Guid groupId);

    [LoggerMessage(LogLevel.Error, "[CLIENT] Failed to create group, error message: {errorMessage}")]
    public static partial void LogFailedToCreateGroup(this ILogger logger, string errorMessage);
}