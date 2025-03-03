using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using ConnectX.Server.Interfaces;
using ConnectX.Server.Models;
using ConnectX.Server.Models.ZeroTier;
using ConnectX.Shared.Helpers;
using ConnectX.Shared.Messages.Group;
using ConnectX.Shared.Messages.Identity;
using ConnectX.Shared.Models;
using Hive.Both.General.Dispatchers;
using Hive.Network.Abstractions;
using Hive.Network.Abstractions.Session;
using Microsoft.Extensions.Logging;

namespace ConnectX.Server.Managers;

public class GroupManager
{
    private readonly IZeroTierApiService _zeroTierApiService;
    private readonly IZeroTierNodeInfoService _zeroTierNodeInfoService;
    private readonly ClientManager _clientManager;
    private readonly IDispatcher _dispatcher;
    private readonly ConcurrentDictionary<Guid, Group> _groupMappings = new();
    private readonly ILogger _logger;

    private readonly ConcurrentDictionary<SessionId, Guid> _sessionIdMapping = new();
    private readonly ConcurrentDictionary<string, Guid> _shortIdGroupMappings = new();
    private readonly ConcurrentDictionary<Guid, BasicUserInfo> _userMapping = new();

    public GroupManager(
        IZeroTierApiService zeroTierApiService,
        IZeroTierNodeInfoService zeroTierNodeInfoService,
        IDispatcher dispatcher,
        ClientManager clientManager,
        ILogger<GroupManager> logger)
    {
        _zeroTierApiService = zeroTierApiService;
        _zeroTierNodeInfoService = zeroTierNodeInfoService;
        _dispatcher = dispatcher;
        _clientManager = clientManager;
        _logger = logger;

        _clientManager.OnSessionDisconnected += ClientManagerOnSessionDisconnected;

        _dispatcher.AddHandler<CreateGroup>(OnCreateGroupReceived);
        _dispatcher.AddHandler<JoinGroup>(OnJoinGroupReceived);
        _dispatcher.AddHandler<LeaveGroup>(OnLeaveGroupReceived);
        _dispatcher.AddHandler<KickUser>(OnKickUserReceived);
        _dispatcher.AddHandler<AcquireGroupInfo>(OnAcquireGroupInfoReceived);
        _dispatcher.AddHandler<UpdateRoomMemberNetworkInfo>(OnUpdateRoomMemberNetworkInfoReceived);
    }

    /// <summary>
    ///     Attach the session to the manager
    /// </summary>
    /// <param name="id"></param>
    /// <param name="session"></param>
    /// <param name="signinMessage"></param>
    /// <returns>the assigned id for the session, if the return value is default, it means the add has failed</returns>
    public Guid AttachSession(
        SessionId id,
        ISession session,
        SigninMessage signinMessage)
    {
        if (!_clientManager.IsSessionAttached(id))
        {
            _logger.LogFailedToAttachSession(id);
            return Guid.Empty;
        }

        var assignedId = Guid.NewGuid();
        var user = new BasicUserInfo
        {
            UserId = assignedId,
            DisplayName = signinMessage.DisplayName,
            Session = session,
            JoinP2PNetwork = signinMessage.JoinP2PNetwork
        };

        if (!_userMapping.TryAdd(assignedId, user) ||
            !_sessionIdMapping.TryAdd(id, assignedId))
        {
            _logger.LogGroupManagerFailedToAddSessionToSessionMapping(id);
            return Guid.Empty;
        }

        _logger.LogSessionAttached(signinMessage.DisplayName, id, assignedId);

        return assignedId;
    }

    private bool IsSessionAttached(
        IDispatcher dispatcher,
        ISession session)
    {
        if (_clientManager.IsSessionAttached(session.Id)) return true;

        var err = new GroupOpResult(GroupCreationStatus.SessionDetached, "Session does not attached to CM.");
        dispatcher.SendAsync(session, err).Forget();

        _logger.LogReceivedGroupOpMessageFromUnattachedSession(session.Id);

        return true;
    }

    private bool IsGroupSessionAttached(
        IDispatcher dispatcher,
        ISession session)
    {
        if (_sessionIdMapping.ContainsKey(session.Id)) return true;

        var err = new GroupOpResult(GroupCreationStatus.SessionDetached, "Session does not attached to GM.");
        dispatcher.SendAsync(session, err).Forget();

        _logger.LogReceivedGroupOpMessageFromUnattachedSession(session.Id);

        return false;
    }

    private bool HasUserMapping(
        Guid userId,
        IDispatcher dispatcher,
        ISession session)
    {
        if (_userMapping.ContainsKey(userId)) return true;

        var err = new GroupOpResult(GroupCreationStatus.UserNotExists, "User does not exist.");
        dispatcher.SendAsync(session, err).Forget();

        _logger.LogUserDoesNotExist(session.Id);

        return false;
    }

    private bool IsAlreadyInGroup(
        Guid userId,
        IDispatcher dispatcher,
        ISession session,
        bool sendErr = true)
    {
        var isAlreadyInGroup = _groupMappings.Values
            .Select(g => g.Users)
            .SelectMany(u => u)
            .Any(u => u.UserId == userId);

        if (!isAlreadyInGroup) return false;
        if (!sendErr) return true;

        var err = new GroupOpResult(GroupCreationStatus.AlreadyInRoom, "User is already in a group.");
        dispatcher.SendAsync(session, err).Forget();

        _logger.LogUserAlreadyInGroupPerformingGroupOp(session.Id);

        return true;
    }

    private bool TryGetGroup(
        Guid groupId,
        IDispatcher? dispatcher,
        ISession? session,
        [NotNullWhen(true)] out Group? group)
    {
        if (!_groupMappings.TryGetValue(groupId, out group))
        {
            if (dispatcher != null &&
                session != null)
            {
                var err = new GroupOpResult(GroupCreationStatus.GroupNotExists, "Group does not exist.");
                dispatcher.SendAsync(session, err).Forget();

                _logger.LogGroupDoesNotExist(session.Id);
            }

            return false;
        }

        return true;
    }

    private async Task NotifyGroupMembersAsync<T>(
        Group group,
        T stateChange)
    {
        foreach (var member in group.Users)
            await _dispatcher.SendAsync(member.Session, stateChange);
    }

    private void ClientManagerOnSessionDisconnected(SessionId sessionId)
    {
        if (!_sessionIdMapping.TryRemove(sessionId, out var userId)) return;
        if (!_userMapping.TryRemove(userId, out var user)) return;

        var group = _groupMappings.Values.FirstOrDefault(g => g.Users.Any(u => u.UserId == user.UserId));

        if (group == null)
        {
            _logger.LogGroupNotFound(userId);
            return;
        }

        var isGroupOwner = group.RoomOwner.UserId == user.UserId;

        if (!isGroupOwner)
        {
            RemoveUser(group.RoomId, user.UserId, null, null, GroupUserStates.Disconnected);
            _logger.LogUserHasBeenRemovedFromGroupBecauseOfDisconnection(user.UserId, group.RoomId);
            return;
        }

        DeleteGroupNetworkAsync(group).Forget();
        NotifyGroupMembersAsync(group, new GroupUserStateChanged(GroupUserStates.Dismissed, group.RoomOwner)).Forget();

        _logger.LogGroupHasBeenDismissedBy(group.RoomId, sessionId);
    }

    private void OnUpdateRoomMemberNetworkInfoReceived(MessageContext<UpdateRoomMemberNetworkInfo> ctx)
    {
        if (!IsSessionAttached(ctx.Dispatcher, ctx.FromSession)) return;
        if (!IsGroupSessionAttached(ctx.Dispatcher, ctx.FromSession)) return;
        if (!HasUserMapping(ctx.Message.UserId, ctx.Dispatcher, ctx.FromSession)) return;

        var groupId = ctx.Message.GroupId;

        if (!TryGetGroup(groupId, ctx.Dispatcher, ctx.FromSession, out var group)) return;

        var user = group.Users.FirstOrDefault(u => u.UserId == ctx.Message.UserId);

        if (user == null)
        {
            var err = new GroupOpResult(GroupCreationStatus.UserNotExists, "User not found");
            ctx.Dispatcher.SendAsync(ctx.FromSession, err).Forget();
            return;
        }

        if (user.UserId == group.RoomOwner.UserId)
        {
            group.RoomOwner.NetworkNodeId = ctx.Message.NetworkNodeId;
            group.RoomOwner.NetworkAddresses = ctx.Message.NetworkIpAddresses;
        }

        user.NetworkNodeId = ctx.Message.NetworkNodeId;
        user.NetworkAddresses = ctx.Message.NetworkIpAddresses;

        var result = new GroupOpResult(GroupCreationStatus.Succeeded);
        ctx.Dispatcher.SendAsync(ctx.FromSession, result).Forget();

        _logger.LogMemberInfoUpdated(ctx.Message.UserId, ctx.Message);

        NotifyGroupMembersAsync(group, new RoomMemberInfoUpdated { UserInfo = user }).Forget();
    }

    private void OnCreateGroupReceived(MessageContext<CreateGroup> ctx)
    {
        if (!IsSessionAttached(ctx.Dispatcher, ctx.FromSession)) return;
        if (!IsGroupSessionAttached(ctx.Dispatcher, ctx.FromSession)) return;
        if (!HasUserMapping(ctx.Message.UserId, ctx.Dispatcher, ctx.FromSession)) return;

        var userId = _sessionIdMapping[ctx.FromSession.Id];

        if (IsAlreadyInGroup(userId, ctx.Dispatcher, ctx.FromSession)) return;

        CreateRoomAsync(userId, ctx).Forget();
    }

    private async Task CreateRoomAsync(Guid userId, MessageContext<CreateGroup> ctx)
    {
        if (_zeroTierNodeInfoService.NodeStatus == null)
        {
            var err = new GroupOpResult(GroupCreationStatus.NetworkControllerNotReady);
            ctx.Dispatcher.SendAsync(ctx.FromSession, err).Forget();
            return;
        }

        var networkId = $"{_zeroTierNodeInfoService.NodeStatus.Address}______";
        var networkCreationReq = new NetworkDetailsReqModel
        {
            Name = GuidHelper.Hash($"GROUP: {ctx.Message.RoomName}").ToString("N"),
            EnableBroadcast = true,
            IpAssignmentPools =
            [
                new IpAssignment
                {
                    IpRangeStart = "114.51.4.1",
                    IpRangeEnd = "114.51.4.254"
                }
            ],
            Mtu = 2800,
            Private = false,
            Routes =
            [
                new Route
                {
                    Target = "114.51.4.0/24"
                }
            ],
            V4AssignMode = new V4AssignMode { Zt = true },
            V6AssignMode = new V6AssignMode { Zt = false, Rfc4193 = false },
            MulticastLimit = 32
        };

        NetworkDetailsModel? networkDetail;

        try
        {
            using var ct = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            networkDetail = await _zeroTierApiService.CreateOrUpdateNetwork(networkId, networkCreationReq, ct.Token);

            ArgumentNullException.ThrowIfNull(networkDetail);

            _logger.LogNewNetworkCreated(networkDetail.Id);
        }
        catch (ArgumentNullException)
        {
            _logger.LogFailedToCreateNetwork("Network detail is null.");

            var err = new GroupOpResult(GroupCreationStatus.NetworkControllerError);
            ctx.Dispatcher.SendAsync(ctx.FromSession, err).Forget();
            return;
        }
        catch (HttpRequestException e)
        {
            _logger.LogFailedToCreateNetwork(e.ToString());

            var err = new GroupOpResult(GroupCreationStatus.NetworkControllerError, e.ToString());
            ctx.Dispatcher.SendAsync(ctx.FromSession, err).Forget();
            return;
        }

        var message = ctx.Message;
        var owner = _userMapping[userId];
        var ownerSession = new UserSessionInfo(owner);

        var group = new Group(message.RoomName, message.RoomPassword, ownerSession, [ownerSession])
        {
            IsPrivate = message.IsPrivate,
            MaxUserCount = message.MaxUserCount <= 0 ? 10 : message.MaxUserCount,
            RoomDescription = message.RoomDescription,
            NetworkId = Convert.ToUInt64(networkDetail.Id, 16)
        };

        if (!_groupMappings.TryAdd(group.RoomId, group) ||
            !_shortIdGroupMappings.TryAdd(group.RoomShortId, group.RoomId))
        {
            var err = new GroupOpResult(GroupCreationStatus.InternalError, "Failed to add group to the group mapping.");
            ctx.Dispatcher.SendAsync(ctx.FromSession, err).Forget();

            _logger.LogFailedToAddGroupToGroupMapping(ctx.FromSession.Id);

            return;
        }

        var success = new GroupOpResult(GroupCreationStatus.Succeeded) { GroupId = group.RoomId };
        ctx.Dispatcher.SendAsync(ctx.FromSession, success).Forget();

        _logger.LogGroupCreated(ctx.FromSession.Id, group.RoomName, group.RoomShortId);
    }

    private void OnJoinGroupReceived(MessageContext<JoinGroup> ctx)
    {
        if (!IsSessionAttached(ctx.Dispatcher, ctx.FromSession)) return;
        if (!IsGroupSessionAttached(ctx.Dispatcher, ctx.FromSession)) return;
        if (!HasUserMapping(ctx.Message.UserId, ctx.Dispatcher, ctx.FromSession)) return;
        if (IsAlreadyInGroup(_sessionIdMapping[ctx.FromSession.Id], ctx.Dispatcher, ctx.FromSession)) return;

        var message = ctx.Message;
        var groupId = string.IsNullOrEmpty(message.RoomShortId)
            ? message.GroupId
            : _shortIdGroupMappings.TryGetValue(message.RoomShortId, out var id)
                ? id
                : Guid.Empty;

        if (!TryGetGroup(groupId, ctx.Dispatcher, ctx.FromSession, out var group)) return;
        if (group.MaxUserCount != 0 &&
            group.MaxUserCount == group.Users.Count)
        {
            var err = new GroupOpResult(GroupCreationStatus.GroupIsFull, "Group is full.");
            ctx.Dispatcher.SendAsync(ctx.FromSession, err).Forget();

            _logger.LogGroupIsFull(ctx.FromSession.Id, groupId);

            return;
        }

        if (!string.IsNullOrEmpty(group.RoomPassword) &&
            group.RoomPassword != message.RoomPassword)
        {
            var err = new GroupOpResult(GroupCreationStatus.PasswordIncorrect, "Wrong password.");
            ctx.Dispatcher.SendAsync(ctx.FromSession, err).Forget();

            _logger.LogWrongPassword(ctx.FromSession.Id, groupId);

            return;
        }

        var user = _userMapping[message.UserId];
        var info = new UserSessionInfo(user);

        group.Users.Add(info);
        NotifyGroupMembersAsync(group, new GroupUserStateChanged(GroupUserStates.Joined, info)).Forget();

        var success = new GroupOpResult(GroupCreationStatus.Succeeded) { GroupId = group.RoomId };
        ctx.Dispatcher.SendAsync(ctx.FromSession, success).Forget();

        _logger.LogUserJoinedGroup(ctx.FromSession.Id, group.RoomName, group.RoomShortId);
    }

    private void RemoveUser(
        Guid groupId,
        Guid userId,
        IDispatcher? dispatcher,
        ISession? session,
        GroupUserStates state)
    {
        if (!TryGetGroup(groupId, dispatcher, session, out var group)) return;

        var user = group.Users.FirstOrDefault(u => u.UserId != userId);

        if (user == null) return;

        DeleteGroupNetworkMemberAsync(group, user).Forget();
        NotifyGroupMembersAsync(group, new GroupUserStateChanged(state, user)).Forget();
        
        group.Users.Remove(user);
    }

    private async Task DeleteGroupNetworkAsync(Group group)
    {
        try
        {
            var networkId = group.NetworkId.ToString("X").ToLowerInvariant();
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            await _zeroTierApiService.DeleteNetworkAsync(networkId, cts.Token);

            _logger.LogNetworkDeleted(networkId);
        }
        catch (HttpRequestException e)
        {
            if (e.StatusCode == HttpStatusCode.NotFound) return;

            _logger.LogFailedToDeleteNetwork(e.ToString());
        }
    }

    private async Task DeleteGroupNetworkMemberAsync(Group group, UserInfo user)
    {
        if (string.IsNullOrEmpty(user.NetworkNodeId))
            return;

        try
        {
            var networkId = group.NetworkId.ToString("X").ToLowerInvariant();
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            await _zeroTierApiService.DeleteNetworkMemberAsync(networkId, user.NetworkNodeId, cts.Token);

            _logger.LogNetworkMemberDeleted(networkId);
        }
        catch (HttpRequestException e)
        {
            if (e.StatusCode == HttpStatusCode.NotFound) return;

            _logger.LogFailedToDeleteNetworkMember(e.ToString());
        }
    }

    private void OnLeaveGroupReceived(MessageContext<LeaveGroup> ctx)
    {
        if (!IsSessionAttached(ctx.Dispatcher, ctx.FromSession)) return;
        if (!IsGroupSessionAttached(ctx.Dispatcher, ctx.FromSession)) return;
        if (!HasUserMapping(ctx.Message.UserId, ctx.Dispatcher, ctx.FromSession)) return;
        if (!IsAlreadyInGroup(_sessionIdMapping[ctx.FromSession.Id], ctx.Dispatcher, ctx.FromSession, false)) return;

        var message = ctx.Message;
        var group = _groupMappings.Values.First(g => g.Users.Any(u => u.UserId == message.UserId));

        var success = new GroupOpResult(GroupCreationStatus.Succeeded);
        ctx.Dispatcher.SendAsync(ctx.FromSession, success).Forget();

        if (group.RoomOwner.UserId == message.UserId)
        {
            _logger.LogGroupHasBeenDismissedBy(message.GroupId, ctx.FromSession.Id);

            _groupMappings.TryRemove(group.RoomId, out _);
            DeleteGroupNetworkAsync(group).Forget();
            NotifyGroupMembersAsync(group, new GroupUserStateChanged(GroupUserStates.Dismissed, null)).Forget();

            return;
        }

        RemoveUser(message.GroupId, message.UserId, ctx.Dispatcher, ctx.FromSession, GroupUserStates.Left);

        _logger.LogUserLeftGroup(ctx.FromSession.Id, group.RoomName, group.RoomShortId);
    }

    private void OnKickUserReceived(MessageContext<KickUser> ctx)
    {
        if (!IsSessionAttached(ctx.Dispatcher, ctx.FromSession)) return;
        if (!IsGroupSessionAttached(ctx.Dispatcher, ctx.FromSession)) return;
        if (!HasUserMapping(ctx.Message.UserId, ctx.Dispatcher, ctx.FromSession)) return;
        if (!IsAlreadyInGroup(_sessionIdMapping[ctx.FromSession.Id], ctx.Dispatcher, ctx.FromSession, false)) return;

        var message = ctx.Message;
        var group = _groupMappings[message.GroupId];

        if (group.RoomOwner.UserId != message.UserId)
        {
            _logger.LogCanNotKickWithoutPermission(ctx.FromSession.Id.Id, message.UserId, message.GroupId);

            return;
        }

        RemoveUser(message.GroupId, message.UserToKick, ctx.Dispatcher, ctx.FromSession, GroupUserStates.Kicked);

        var success = new GroupOpResult(GroupCreationStatus.Succeeded);
        ctx.Dispatcher.SendAsync(ctx.FromSession, success).Forget();

        _logger.LogUserHasBeenKickedFromGroup(ctx.FromSession.Id, group.RoomName, group.RoomShortId);
    }

    private void OnAcquireGroupInfoReceived(MessageContext<AcquireGroupInfo> ctx)
    {
        var session = ctx.FromSession;

        if (!_clientManager.IsSessionAttached(session.Id) ||
            !_sessionIdMapping.TryGetValue(session.Id, out var userId) ||
            !_userMapping.ContainsKey(userId) ||
            !_groupMappings.TryGetValue(ctx.Message.GroupId, out var group))
        {
            ctx.Dispatcher.SendAsync(session, GroupInfo.Invalid).Forget();
            return;
        }

        var isInGroup = group.Users.Any(u => u.UserId == userId);

        if (isInGroup)
        {
            ctx.Dispatcher.SendAsync(session, (GroupInfo)group).Forget();
            return;
        }

        // Not in group, send group info with empty user info

        var groupWithEmptyUserInfo = (GroupInfo)group with { Users = [] };
        ctx.Dispatcher.SendAsync(session, groupWithEmptyUserInfo).Forget();
    }
}

internal static partial class GroupManagerLoggers
{
    [LoggerMessage(LogLevel.Warning, "[GROUP_MANAGER] Can not find any group related {userId}!")]
    public static partial void LogGroupNotFound(this ILogger logger, Guid userId);

    [LoggerMessage(LogLevel.Information, "[GROUP_MANAGER] User [{id}] updated its member info: {@info}")]
    public static partial void LogMemberInfoUpdated(this ILogger logger, Guid id, UpdateRoomMemberNetworkInfo info);

    [LoggerMessage(LogLevel.Error,
        "[GROUP_MANAGER] Failed to attach session, session id: {sessionId}, invalid session.")]
    public static partial void LogFailedToAttachSession(this ILogger logger, SessionId sessionId);

    [LoggerMessage(LogLevel.Error,
        "[GROUP_MANAGER] Failed to attach session, session id: {sessionId}, failed to add user.")]
    public static partial void LogGroupManagerFailedToAddSessionToSessionMapping(this ILogger logger,
        SessionId sessionId);

    [LoggerMessage(LogLevel.Information,
        "[GROUP_MANAGER] Session attached, display name: {displayName}, session id: {sessionId}, assigned id: {assignedId}")]
    public static partial void LogSessionAttached(this ILogger logger, string displayName, SessionId sessionId, Guid assignedId);

    [LoggerMessage(LogLevel.Warning,
        "[GROUP_MANAGER] Received group op message from unattached session, session id: {sessionId}")]
    public static partial void LogReceivedGroupOpMessageFromUnattachedSession(this ILogger logger, SessionId sessionId);

    [LoggerMessage(LogLevel.Warning, "[GROUP_MANAGER] User does not exist, session id: {sessionId}")]
    public static partial void LogUserDoesNotExist(this ILogger logger, SessionId sessionId);

    [LoggerMessage(LogLevel.Warning,
        "[GROUP_MANAGER] A user who is already in a group are trying to perform group op, session id: {sessionId}")]
    public static partial void LogUserAlreadyInGroupPerformingGroupOp(this ILogger logger, SessionId sessionId);

    [LoggerMessage(LogLevel.Warning, "[GROUP_MANAGER] Group does not exist, session id: {sessionId}")]
    public static partial void LogGroupDoesNotExist(this ILogger logger, SessionId sessionId);

    [LoggerMessage(LogLevel.Information,
        "[GROUP_MANAGER] User [{userId}] has been removed from group [{groupId}] because of disconnection.")]
    public static partial void LogUserHasBeenRemovedFromGroupBecauseOfDisconnection(this ILogger logger, Guid userId,
        Guid groupId);

    [LoggerMessage(LogLevel.Information, "[GROUP_MANAGER] New network created, id: 0x{id}.")]
    public static partial void LogNewNetworkCreated(this ILogger logger, string id);

    [LoggerMessage(LogLevel.Error, "[GROUP_MANAGER] Failed to create new network, {message}.")]
    public static partial void LogFailedToCreateNetwork(this ILogger logger, string message);

    [LoggerMessage(LogLevel.Information, "[GROUP_MANAGER] Network deleted, id: {id}.")]
    public static partial void LogNetworkDeleted(this ILogger logger, string id);

    [LoggerMessage(LogLevel.Error, "[GROUP_MANAGER] Failed to delete network, {message}.")]
    public static partial void LogFailedToDeleteNetwork(this ILogger logger, string message);

    [LoggerMessage(LogLevel.Information, "[GROUP_MANAGER] Network member deleted, id: {id}.")]
    public static partial void LogNetworkMemberDeleted(this ILogger logger, string id);

    [LoggerMessage(LogLevel.Error, "[GROUP_MANAGER] Failed to delete network member, {message}.")]
    public static partial void LogFailedToDeleteNetworkMember(this ILogger logger, string message);

    [LoggerMessage(LogLevel.Warning,
        "[GROUP_MANAGER] Failed to add group to the group mapping, session id: 0x{sessionId}")]
    public static partial void LogFailedToAddGroupToGroupMapping(this ILogger logger, SessionId sessionId);

    [LoggerMessage(LogLevel.Information,
        "[GROUP_MANAGER] Group created by [{userId}] with info [{groupName}]({shortId})")]
    public static partial void LogGroupCreated(this ILogger logger, SessionId userId, string groupName, string shortId);

    [LoggerMessage(LogLevel.Warning,
        "[GROUP_MANAGER] User [{sessionId}] tried to join group [{groupId}], but it is full.")]
    public static partial void LogGroupIsFull(this ILogger logger, SessionId sessionId, Guid groupId);

    [LoggerMessage(LogLevel.Warning,
        "[GROUP_MANAGER] User [{sessionId}] tried to join group [{groupId}], but the password is wrong.")]
    public static partial void LogWrongPassword(this ILogger logger, SessionId sessionId, Guid groupId);

    [LoggerMessage(LogLevel.Information, "[GROUP_MANAGER] User [{SessionId}] joined group [{groupName}]({shortId})")]
    public static partial void LogUserJoinedGroup(this ILogger logger, SessionId sessionId, string groupName,
        string shortId);

    [LoggerMessage(LogLevel.Information, "[GROUP_MANAGER] Group [{groupId}] has been dismissed by [{SessionId}]")]
    public static partial void LogGroupHasBeenDismissedBy(this ILogger logger, Guid groupId, SessionId sessionId);

    [LoggerMessage(LogLevel.Information, "[GROUP_MANAGER] User [{SessionId}] left group [{groupName}]({shortId})")]
    public static partial void LogUserLeftGroup(this ILogger logger, SessionId sessionId, string groupName,
        string shortId);

    [LoggerMessage(LogLevel.Warning,
        "[GROUP_MANAGER] User [{SessionId}] tried to kick user [{userId}] from group [{groupId}], but the user is not the owner.")]
    public static partial void LogCanNotKickWithoutPermission(this ILogger logger, SessionId sessionId, Guid userId,
        Guid groupId);

    [LoggerMessage(LogLevel.Information,
        "[GROUP_MANAGER] User [{SessionId}] has been kicked from group [{groupName}]({shortId})")]
    public static partial void LogUserHasBeenKickedFromGroup(this ILogger logger, SessionId sessionId, string groupName,
        string shortId);
}