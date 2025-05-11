using ConnectX.Client.Interfaces;
using ConnectX.Client.Managers;
using ConnectX.Client.Route;
using ConnectX.Shared.Helpers;
using ConnectX.Shared.Messages.Group;
using ConnectX.Shared.Messages.Identity;
using ConnectX.Shared.Models;
using Hive.Both.General.Dispatchers;
using Microsoft.Extensions.Logging;

namespace ConnectX.Client;

public delegate void GroupStateChangedHandler(GroupUserStates state, UserInfo? userInfo);

public record PartnerConnectionState(bool IsConnected, bool IsDirectConnection, int Latency);

public class Client
{
    private readonly IDispatcher _dispatcher;
    private readonly ILogger _logger;

    private readonly Router _router;
    private readonly PartnerManager _partnerManager;
    private readonly ProxyManager _proxyManager;
    private readonly IRoomInfoManager _roomInfoManager;
    private readonly IServerLinkHolder _serverLinkHolder;
    private readonly IZeroTierNodeLinkHolder _zeroTierNodeLinkHolder;
    private bool _isInGroup;

    public Client(
        Router router,
        PartnerManager partnerManager,
        ProxyManager proxyManager,
        IRoomInfoManager roomInfoManager,
        IDispatcher dispatcher,
        IServerLinkHolder serverLinkHolder,
        IZeroTierNodeLinkHolder zeroTierNodeLinkHolder,
        ILogger<Client> logger)
    {
        _router = router;
        _partnerManager = partnerManager;
        _proxyManager = proxyManager;
        _roomInfoManager = roomInfoManager;
        _dispatcher = dispatcher;
        _serverLinkHolder = serverLinkHolder;
        _zeroTierNodeLinkHolder = zeroTierNodeLinkHolder;
        _logger = logger;

        _dispatcher.AddHandler<GroupUserStateChanged>(OnGroupUserStateChanged);
        _dispatcher.AddHandler<RoomMemberInfoUpdated>(OnRoomMemberInfoUpdated);

        serverLinkHolder.OnServerLinkDisconnected += OnServerLinkDisconnected;
    }

    public event GroupStateChangedHandler? OnGroupStateChanged;

    private void OnServerLinkDisconnected()
    {
        ResetRoomState().Forget();
    }

    private void OnRoomMemberInfoUpdated(MessageContext<RoomMemberInfoUpdated> obj)
    {
        _roomInfoManager.UpdateRoomMemberInfo(obj.Message.UserInfo);

        if (!_isInGroup || _roomInfoManager.CurrentGroupInfo?.RoomId == null)
            return;

        _roomInfoManager.AcquireGroupInfoAsync(_roomInfoManager.CurrentGroupInfo.RoomId).Forget();
    }

    private async Task<GroupOpResult?> PerformGroupOpAsync<T>(T message)
    {
        var result =
            await _dispatcher.SendAndListenOnce<T, GroupOpResult>(
                _serverLinkHolder.ServerSession!,
                message);

        if (result is { Status: GroupCreationStatus.Succeeded })
            return result;

        _logger.LogFailedToPerformGroupOp(
            typeof(T).Name,
            result?.ErrorMessage ?? "-");
        return result;
    }

    private async Task ResetRoomState(CancellationToken ct = default)
    {
        _isInGroup = false;

        // Clear and reset all manager
        if (OperatingSystem.IsWindows())
        {
            await _zeroTierNodeLinkHolder.LeaveNetworkAsync(ct);
        }

        _router.RemoveAllPeers();
        _roomInfoManager.ClearRoomInfo();
        _partnerManager.RemoveAllPartners();
        _proxyManager.RemoveAllProxies();
    }

    private void OnGroupUserStateChanged(MessageContext<GroupUserStateChanged> ctx)
    {
        if (!_serverLinkHolder.IsConnected) return;
        if (!_serverLinkHolder.IsSignedIn) return;
        if (!_isInGroup) return;

        var state = ctx.Message.State;
        var userInfo = ctx.Message.UserInfo;

        switch (state)
        {
            case GroupUserStates.Left:
            case GroupUserStates.Disconnected:
            case GroupUserStates.Kicked:
                if (userInfo?.UserId == _serverLinkHolder.UserId)
                    ResetRoomState().Forget();
                else _roomInfoManager.AcquireGroupInfoAsync(_roomInfoManager.CurrentGroupInfo!.RoomId).Forget();
                break;
            case GroupUserStates.Dismissed:
                ResetRoomState().Forget();
                break;
        }

        OnGroupStateChanged?.Invoke(state, userInfo);
    }

    private async Task<(GroupInfo?, GroupCreationStatus, string?)> PerformOpAndGetRoomInfoAsync<T>(T message, CancellationToken ct)
    {
        var createResult = await PerformGroupOpAsync(message);

        if (createResult == null)
            return (null, GroupCreationStatus.Other, null);
        if (createResult.Status != GroupCreationStatus.Succeeded)
            return (null, createResult.Status, createResult.ErrorMessage);

        var groupInfo = await _roomInfoManager.AcquireGroupInfoAsync(createResult.RoomId);

        if (groupInfo == null)
            return (null, GroupCreationStatus.Other, "Failed to acquire group info");

        if (OperatingSystem.IsWindows() &&
            message is JoinGroup or CreateGroup)
        {
            if (createResult.Status != GroupCreationStatus.Succeeded)
                return (null, createResult.Status, createResult.ErrorMessage);

            _logger.LogJoiningNetwork(groupInfo.RoomNetworkId);

            var networkResult = await _zeroTierNodeLinkHolder.JoinNetworkAsync(groupInfo.RoomNetworkId, ct);

            if (!networkResult)
                return (null, GroupCreationStatus.Other, "Failed to join the network");

            await TaskHelper.WaitUntilAsync(_zeroTierNodeLinkHolder.IsNodeOnline, ct);

            var nodeId = _zeroTierNodeLinkHolder.Node!.IdString;
            var updateInfo = new UpdateRoomMemberNetworkInfo
            {
                NetworkNodeId = nodeId,
                NetworkIpAddresses = _zeroTierNodeLinkHolder.GetIpAddresses()
            };

            var result = await _dispatcher.SendAndListenOnce<UpdateRoomMemberNetworkInfo, GroupOpResult>(
                _serverLinkHolder.ServerSession!,
                updateInfo, ct);

            if (result is not { Status: GroupCreationStatus.Succeeded })
                return (null, result?.Status ?? GroupCreationStatus.Other, "Failed to update room member network info");
        }

        _isInGroup = true;
        return (groupInfo, GroupCreationStatus.Succeeded, null);
    }

    public async Task<(GroupInfo? Info, GroupCreationStatus Status, string? Error)> CreateGroupAsync(CreateGroup createGroup, CancellationToken ct)
    {
        if (!_serverLinkHolder.IsConnected)
            return (null, GroupCreationStatus.Other, "Not connected to the server");
        if (!_serverLinkHolder.IsSignedIn)
            return (null, GroupCreationStatus.Other, "Not signed in");
        if (!OperatingSystem.IsWindows() && !createGroup.UseRelayServer)
            return (null, GroupCreationStatus.Other, "Only Windows platform supports direct connection");

        return await PerformOpAndGetRoomInfoAsync(createGroup, ct);
    }

    public async Task<(GroupInfo?, GroupCreationStatus, string?)> JoinGroupAsync(JoinGroup joinGroup, CancellationToken ct)
    {
        if (!_serverLinkHolder.IsConnected)
            return (null, GroupCreationStatus.Other, "Not connected to the server");
        if (!_serverLinkHolder.IsSignedIn)
            return (null, GroupCreationStatus.Other, "Not signed in");
        if (!OperatingSystem.IsWindows() && !joinGroup.UseRelayServer)
            return (null, GroupCreationStatus.Other, "Only Windows platform supports direct connection");

        return await PerformOpAndGetRoomInfoAsync(joinGroup, ct);
    }

    public async Task<(bool, string?)> LeaveGroupAsync(LeaveGroup leaveGroup)
    {
        if (!_serverLinkHolder.IsConnected) return (false, "Not connected to the server");
        if (!_serverLinkHolder.IsSignedIn) return (false, "Not signed in");
        if (!_isInGroup) return (false, "Not in a group");

        // Reset the room state
        await ResetRoomState(CancellationToken.None);

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

    public async Task UpdateDisplayNameAsync(string newName, CancellationToken ct)
    {
        if (!_serverLinkHolder.IsConnected) return;
        if (!_serverLinkHolder.IsSignedIn) return;

        var msg = new UpdateDisplayNameMessage { DisplayName = newName };

        await _dispatcher.SendAsync(_serverLinkHolder.ServerSession!, msg, ct);
    }

    /// <summary>
    ///     获取和目标用户的连接情况
    /// </summary>
    /// <param name="partnerId">目标用户的ID</param>
    /// <returns>(是否可以连通，是否直连，ping)</returns>
    public PartnerConnectionState GetPartnerConState(Guid partnerId)
    {
        if (_partnerManager.Partners.TryGetValue(partnerId, out var partner))
        {
            return new PartnerConnectionState(
                partner.Connection.IsConnected,
                false,
                partner.Latency);
        }

        var forwardInterface = _router.RouteTable.GetForwardInterface(partnerId);

        if (forwardInterface == Guid.Empty)
            return new PartnerConnectionState(false, false, -1);

        var linkState = _router.RouteTable.GetSelfLinkState();
        var ping = -1;

        if (linkState == null)
            return new PartnerConnectionState(
                true,
                forwardInterface == partnerId,
                ping);

        var guidList = linkState.Interfaces;
        for (var index = 0; index < guidList.Length; index++)
        {
            var guid = guidList[index];

            if (guid != partnerId) continue;

            ping = linkState.Costs[index];

            break;
        }

        return new PartnerConnectionState(
            true,
            forwardInterface == partnerId,
            ping);
    }
}

internal static partial class ClientLoggers
{
    [LoggerMessage(LogLevel.Information, "[CLIENT] Network info received [0x{roomId:X}], trying to join the network...")]
    public static partial void LogJoiningNetwork(this ILogger logger, ulong roomId);

    [LoggerMessage(LogLevel.Error, "[CLIENT] Failed to acquire group info, group id: {groupId}")]
    public static partial void LogFailedToAcquireGroupInfo(this ILogger logger, Guid groupId);

    [LoggerMessage(LogLevel.Error, "[CLIENT] Failed to perform group op <{opType}>, error message: {errorMessage}")]
    public static partial void LogFailedToPerformGroupOp(this ILogger logger, string opType, string errorMessage);
}