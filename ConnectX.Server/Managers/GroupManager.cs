using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using ConnectX.Server.Models;
using ConnectX.Shared;
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
    private readonly IDispatcher _dispatcher;
    private readonly ClientManager _clientManager;
    private readonly ILogger _logger;

    private readonly ConcurrentDictionary<SessionId, Guid> _sessionIdMapping = new ();
    private readonly ConcurrentDictionary<Guid, User> _userMapping = new ();
    private readonly ConcurrentDictionary<Guid, Group> _groupMappings = new();
    private readonly ConcurrentDictionary<string, Guid> _shortIdGroupMappings = new();
    
    public GroupManager(
        IDispatcher dispatcher,
        ClientManager clientManager,
        ILogger<GroupManager> logger)
    {
        _dispatcher = dispatcher;
        _clientManager = clientManager;
        _logger = logger;
        
        _clientManager.OnSessionDisconnected += ClientManagerOnSessionDisconnected;

        _dispatcher.AddHandler<CreateGroup>(OnCreateGroupReceived);
        _dispatcher.AddHandler<JoinGroup>(OnJoinGroupReceived);
        _dispatcher.AddHandler<LeaveGroup>(OnLeaveGroupReceived);
        _dispatcher.AddHandler<KickUser>(OnKickUserReceived);
        _dispatcher.AddHandler<AcquireGroupInfo>(OnAcquireGroupInfoReceived);
    }
    
    public IReadOnlyList<User> GetAllUsers()
    {
        return _userMapping.Values.ToList();
    }
    
    public bool TryGetUser(SessionId sessionId, out User? user)
    {
        if (!_sessionIdMapping.TryGetValue(sessionId, out var userId))
        {
            user = null;
            return false;
        }

        return _userMapping.TryGetValue(userId, out user);
    }
    
    public bool TryGetUser(Guid userId, [MaybeNullWhen(false)] out User user)
    {
        return _userMapping.TryGetValue(userId, out user);
    }

    /// <summary>
    /// Attach the session to the manager
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
            _logger.LogError(
                "[GROUP_MANAGER] Failed to attach session, session id: {sessionId}, invalid session.",
                id);
            return default;
        }

        var assignedId = Guid.NewGuid();
        var user = new User
        {
            UserId = assignedId,
            DisplayName = session.RemoteEndPoint?.ToString() ?? assignedId.ToString(),
            Session = session,
            BindingTestResult = signinMessage.BindingTestResult,
            MappingBehavior = signinMessage.MappingBehavior,
            FilteringBehavior = signinMessage.FilteringBehavior,
            JoinP2PNetwork = signinMessage.JoinP2PNetwork
        };

        if (!_userMapping.TryAdd(assignedId, user) ||
            !_sessionIdMapping.TryAdd(id, assignedId))
        {
            _logger.LogError(
                "[GROUP_MANAGER] Failed to attach session, session id: {sessionId}, failed to add user.",
                id);
            return default;
        }
        
        _logger.LogInformation(
            "[GROUP_MANAGER] Session attached, session id: {sessionId}, assigned id: {assignedId}",
            id.Id,
            assignedId);

        return assignedId;
    }

    private bool IsSessionAttached(
        IDispatcher dispatcher,
        ISession session)
    {
        if (_clientManager.IsSessionAttached(session.Id)) return true;
        
        var err = new GroupOpResult(false, "Session does not attached to SM.");
        dispatcher.SendAsync(session, err).Forget();

        _logger.LogWarning(
            "[GROUP_MANAGER] Received group op message from unattached session, session id: {sessionId}",
            session.Id.Id);

        return true;
    }

    private bool IsGroupSessionAttached(
        IDispatcher dispatcher,
        ISession session)
    {
        if (_sessionIdMapping.ContainsKey(session.Id)) return true;
        
        var err = new GroupOpResult(false, "Session does not attached to GM.");
        dispatcher.SendAsync(session, err).Forget();
            
        _logger.LogWarning(
            "[GROUP_MANAGER] Received group op message from unattached session, session id: {sessionId}",
            session.Id.Id);
            
        return false;
    }

    private bool HasUserMapping(
        Guid userId,
        IDispatcher dispatcher,
        ISession session)
    {
        if (_userMapping.ContainsKey(userId)) return true;
        
        var err = new GroupOpResult(false, "User does not exist.");
        dispatcher.SendAsync(session, err).Forget();
        
        _logger.LogWarning(
            "[GROUP_MANAGER] User does not exist, session id: {sessionId}",
            session.Id.Id);
        
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
        
        var err = new GroupOpResult(false, "User is already in a group.");
        dispatcher.SendAsync(session, err).Forget();

        _logger.LogWarning(
            "[GROUP_MANAGER] A user who is already in a group are trying to perform group op, session id: {sessionId}",
            session.Id.Id);

        return true;
    }

    private (bool, Group?) TryGetGroup(
        Guid groupId,
        IDispatcher? dispatcher,
        ISession? session)
    {
        if (!_groupMappings.TryGetValue(groupId, out var group))
        {
            if (dispatcher != null &&
                session != null)
            {
                var err = new GroupOpResult(false, "Group does not exist.");
                dispatcher.SendAsync(session, err).Forget();
            
                _logger.LogWarning(
                    "[GROUP_MANAGER] Group does not exist, session id: {sessionId}",
                    session.Id.Id);
            }
            
            return (false, null);
        }
        
        return (true, group);
    }
    
    private async Task NotifyGroupMembersAsync(
        Group group,
        GroupUserStateChanged stateChange)
    {
        foreach (var member in group.Users)
        {
            await _dispatcher.SendAsync(member.Session, stateChange);
        }
    }
    
    private void ClientManagerOnSessionDisconnected(SessionId sessionId)
    {
        if (!_sessionIdMapping.TryRemove(sessionId, out var userId)) return;
        if (!_userMapping.TryRemove(userId, out var user)) return;
        if (!_groupMappings.TryRemove(userId, out var group))
        {
            foreach (var tGroup in _groupMappings.Values)
            {
                if (tGroup.Users.All(u => u.UserId != user.UserId)) continue;
                RemoveUser(tGroup.RoomId, user.UserId, null, null, GroupUserStates.Disconnected);
                
                _logger.LogInformation(
                    "[GROUP_MANAGER] User [{userId}] has been removed from group [{groupId}] because of disconnection.",
                    user.UserId, tGroup.RoomId);
                break;
            }
            
            return;
        }
        
        _logger.LogInformation(
            "[GROUP_MANAGER] Group [{groupId}] has been dismissed by [{userId}] because of disconnection.",
            group.RoomId, user.UserId);
        
        NotifyGroupMembersAsync(group, new GroupUserStateChanged(GroupUserStates.Dismissed, user)).Forget();
    }

    private void OnCreateGroupReceived(MessageContext<CreateGroup> ctx)
    {
        if (!IsSessionAttached(ctx.Dispatcher, ctx.FromSession)) return;
        if (!IsGroupSessionAttached(ctx.Dispatcher, ctx.FromSession)) return;
        if (!HasUserMapping(ctx.Message.UserId, ctx.Dispatcher, ctx.FromSession)) return;

        var userId = _sessionIdMapping[ctx.FromSession.Id];
        
        if (IsAlreadyInGroup(userId, ctx.Dispatcher, ctx.FromSession)) return;

        var message = ctx.Message;
        var owner = _userMapping[userId];
        var group = new Group(message.RoomName, message.RoomPassword, owner, [owner])
        {
            IsPrivate = message.IsPrivate,
            MaxUserCount = message.MaxUserCount <= 0 ? 10 : message.MaxUserCount,
            RoomDescription = message.RoomDescription
        };
        
        if (!_groupMappings.TryAdd(group.RoomId, group) ||
            !_shortIdGroupMappings.TryAdd(group.RoomShortId, group.RoomId))
        {
            var err = new GroupOpResult(false, "Failed to add group to the group mapping.");
            ctx.Dispatcher.SendAsync(ctx.FromSession, err).Forget();
            
            _logger.LogWarning(
                "[GROUP_MANAGER] Failed to add group to the group mapping, session id: {sessionId}",
                ctx.FromSession.Id.Id);
            
            return;
        }
        
        var success = new GroupOpResult(true){GroupId = group.RoomId};
        ctx.Dispatcher.SendAsync(ctx.FromSession, success).Forget();
        
        _logger.LogInformation(
            "[GROUP_MANAGER] Group created by [{userId}] with info [{groupName}]({shortId})",
            ctx.FromSession.Id.Id, group.RoomName, group.RoomShortId);
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
        var (hasGroup, group) = TryGetGroup(groupId, ctx.Dispatcher, ctx.FromSession);
        
        if (!hasGroup) return;
        if (group!.MaxUserCount != 0 &&
            group.MaxUserCount == group.Users.Count)
        {
            var err = new GroupOpResult(false, "Group is full.");
            ctx.Dispatcher.SendAsync(ctx.FromSession, err).Forget();
            
            _logger.LogWarning(
                "[GROUP_MANAGER] User [{sessionId}] tried to join group [{groupId}], but it is full.",
                ctx.FromSession.Id.Id, groupId);
            
            return;
        }
        if (!string.IsNullOrEmpty(group.RoomPassword) &&
            group.RoomPassword != message.RoomPassword)
        {
            var err = new GroupOpResult(false, "Wrong password.");
            ctx.Dispatcher.SendAsync(ctx.FromSession, err).Forget();
            
            _logger.LogWarning(
                "[GROUP_MANAGER] User [{sessionId}] tried to join group [{groupId}], but the password is wrong.",
                ctx.FromSession.Id.Id, groupId);
            
            return;
        }

        var user = _userMapping[message.UserId];
        
        group.Users.Add(user);
        NotifyGroupMembersAsync(group, new GroupUserStateChanged(GroupUserStates.Joined, user)).Forget();
        
        var success = new GroupOpResult(true){GroupId = group.RoomId};
        ctx.Dispatcher.SendAsync(ctx.FromSession, success).Forget();
        
        _logger.LogInformation(
            "[GROUP_MANAGER] User [{userId}] joined group [{groupName}]({shortId})",
            ctx.FromSession.Id.Id, group.RoomName, group.RoomShortId);
    }

    private void RemoveUser(
        Guid groupId,
        Guid userId,
        IDispatcher? dispatcher,
        ISession? session,
        GroupUserStates state)
    {
        var (hasGroup, group) = TryGetGroup(groupId, dispatcher, session);
        
        if (!hasGroup) return;
        if (group!.Users.All(u => u.UserId != userId)) return;
        
        var user = _userMapping[userId];
        
        NotifyGroupMembersAsync(group, new GroupUserStateChanged(state, user)).Forget();
        group.Users.Remove(user);
    }
    
    private void OnLeaveGroupReceived(MessageContext<LeaveGroup> ctx)
    {
        if (!IsSessionAttached(ctx.Dispatcher, ctx.FromSession)) return;
        if (!IsGroupSessionAttached(ctx.Dispatcher, ctx.FromSession)) return;
        if (!HasUserMapping(ctx.Message.UserId, ctx.Dispatcher, ctx.FromSession)) return;
        if (!IsAlreadyInGroup(_sessionIdMapping[ctx.FromSession.Id], ctx.Dispatcher, ctx.FromSession, false)) return;
        
        var message = ctx.Message;
        var group = _groupMappings.Values.First(g => g.Users.Any(u => u.UserId == message.UserId));

        var success = new GroupOpResult(true);
        ctx.Dispatcher.SendAsync(ctx.FromSession, success).Forget();
        
        if (group.RoomOwner.UserId == message.UserId)
        {
            _logger.LogInformation(
                "[GROUP_MANAGER] Group [{groupId}] has been dismissed by [{userId}]",
                message.GroupId, ctx.FromSession.Id.Id);
            
            NotifyGroupMembersAsync(group, new GroupUserStateChanged(GroupUserStates.Dismissed, null)).Forget();
            return;
        }
        
        RemoveUser(message.GroupId, message.UserId, ctx.Dispatcher, ctx.FromSession, GroupUserStates.Left);
        
        _logger.LogInformation(
            "[GROUP_MANAGER] User [{userId}] left group [{groupName}]({shortId})",
            ctx.FromSession.Id.Id, group.RoomName, group.RoomShortId);
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
            _logger.LogWarning(
                "[GROUP_MANAGER] User [{sessionId}] tried to kick user [{userId}] from group [{groupId}], but the user is not the owner.",
                ctx.FromSession.Id.Id, message.UserId, message.GroupId);

            return;
        }
        
        RemoveUser(message.GroupId, message.UserToKick, ctx.Dispatcher, ctx.FromSession, GroupUserStates.Kicked);
        
        var success = new GroupOpResult(true);
        ctx.Dispatcher.SendAsync(ctx.FromSession, success).Forget();
        
        _logger.LogInformation(
            "[GROUP_MANAGER] User [{userId}] has been kicked from group [{groupName}]({shortId})",
            ctx.FromSession.Id.Id, group.RoomName, group.RoomShortId);
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
            ctx.Dispatcher.SendAsync(session, (GroupInfo) group).Forget();
            return;
        }

        var groupWithEmptyUserInfo = (GroupInfo)group with { Users = [] };
        ctx.Dispatcher.SendAsync(session, groupWithEmptyUserInfo).Forget();
    }
}