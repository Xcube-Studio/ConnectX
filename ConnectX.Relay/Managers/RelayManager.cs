using System.Collections.Concurrent;
using System.Net;
using ConnectX.Relay.Helpers;
using ConnectX.Relay.Interfaces;
using ConnectX.Shared.Helpers;
using ConnectX.Shared.Messages;
using ConnectX.Shared.Messages.Relay;
using ConnectX.Shared.Models;
using Hive.Both.General.Dispatchers;
using Hive.Network.Abstractions;
using Hive.Network.Abstractions.Session;
using Microsoft.Extensions.Logging;

namespace ConnectX.Relay.Managers;

public class RelayManager
{
    private readonly ConcurrentDictionary<SessionId, Guid> _sessionUserIdMapping = new();
    private readonly ConcurrentDictionary<Guid, ISession> _userIdSessionMapping = new ();
    private readonly ConcurrentDictionary<Guid, Guid> _userIdToRoomMapping = new();

    private readonly ClientManager _clientManager;
    private readonly IServerLinkHolder _serverLinkHolder;
    private readonly IDispatcher _dispatcher;
    private readonly ILogger _logger;

    public RelayManager(
        ClientManager clientManager,
        IServerLinkHolder serverLinkHolder,
        IDispatcher dispatcher,
        ILogger<RelayManager> logger)
    {
        _clientManager = clientManager;
        _serverLinkHolder = serverLinkHolder;
        _dispatcher = dispatcher;
        _logger = logger;

        _clientManager.OnSessionDisconnected += ClientManagerOnSessionDisconnected;

        dispatcher.AddHandler<RelayDatagram>(OnRelayDatagramReceived);
        dispatcher.AddHandler<UpdateRelayUserRoomMappingMessage>(OnUpdateRelayUserRoomMappingMessageReceived);
    }

    public void AttachSession(
        ISession session,
        Guid userId,
        Guid roomId)
    {
        if (!_userIdToRoomMapping.TryGetValue(userId, out var registeredRoomId))
        {
            _logger.LogCanNotFindCorrespondingRoomForUser(session.Id, session.RemoteEndPoint?.Address ?? IPAddress.None);
            return;
        }

        if (registeredRoomId != roomId)
        {
            _logger.LogUserRoomDoesNotMatchTheRecord(session.Id, session.RemoteEndPoint?.Address ?? IPAddress.None);
            return;
        }

        if (session.RemoteEndPoint == null)
        {
            _logger.LogSessionRemoteEndPointIsNull(session.Id);
            return;
        }

        _sessionUserIdMapping.AddOrUpdate(session.Id, _ => userId, (_, _) => userId);
        _userIdSessionMapping.AddOrUpdate(userId, _ => session, (_, _) => session);

        _logger.LogRelayLinkAttached(session.Id, userId);
    }

    private void OnUpdateRelayUserRoomMappingMessageReceived(MessageContext<UpdateRelayUserRoomMappingMessage> ctx)
    {
        if (_serverLinkHolder.ServerSession == null)
        {
            _logger.LogSomeSessionIsFakingMainServer(ctx.FromSession.RemoteEndPoint);
            return;
        }

        if (!ctx.FromSession.IsSameSession(_serverLinkHolder.ServerSession))
        {
            _logger.LogRelayInfoUpdateUnauthorized(ctx.FromSession.Id, ctx.FromSession.RemoteEndPoint?.Address ?? IPAddress.None);
            return;
        }

        var message = ctx.Message;

        switch (message.State)
        {
            case GroupUserStates.Joined:
                _userIdToRoomMapping.AddOrUpdate(message.UserId, message.RoomId, (_, _) => message.RoomId);
                _logger.LogRelayInfoAdded(message.UserId, message.RoomId, _userIdToRoomMapping.Count);
                break;
            case GroupUserStates.Left:
            case GroupUserStates.Kicked:
            case GroupUserStates.Disconnected:
            case GroupUserStates.Dismissed:
                _userIdToRoomMapping.TryRemove(message.UserId, out _);
                _sessionUserIdMapping.TryRemove(ctx.FromSession.Id, out _);
                _userIdSessionMapping.TryRemove(message.UserId, out _);

                _logger.LogRelayDestroyed(message.RoomId, message.UserId, message.State);

                break;
        }
    }

    private void OnRelayDatagramReceived(MessageContext<RelayDatagram> ctx)
    {
        var message = ctx.Message;

        if (!_userIdSessionMapping.TryGetValue(message.To, out var session))
        {
            _logger.LogRelayDestinationNotFound(ctx.FromSession.Id, message.From, message.To);
            return;
        }

        _dispatcher.SendAsync(session, message).Forget();

        _logger.LogRelayDatagramSent(ctx.FromSession.Id, message.To);
    }

    private void ClientManagerOnSessionDisconnected(SessionId sessionId)
    {
        if (!_sessionUserIdMapping.TryRemove(sessionId, out var userId)) return;
        if (!_userIdToRoomMapping.TryRemove(userId, out var roomId)) return;
        if (!_userIdSessionMapping.TryRemove(userId, out var session)) return;

        // Because the session is already disconnected, we just recycle the link with it.
        // No need to ask the client's current ref count
        session.Close();

        _logger.LogRelayDestroyed(roomId, userId, GroupUserStates.Disconnected);
    }
}

internal static partial class RelayManagerLoggers
{
    [LoggerMessage(LogLevel.Warning, "[RELAY_MANAGER] RelayTo is empty from session [{sessionId}], possible bug or wrong sender!")]
    public static partial void LogRelayToEmpty(this ILogger logger, SessionId sessionId);

    [LoggerMessage(LogLevel.Information, "[RELAY_MANAGER] Relay destroyed, room [{roomId}], user [{userId}], state [{state}]")]
    public static partial void LogRelayDestroyed(this ILogger logger, Guid roomId, Guid userId, GroupUserStates state);

    [LoggerMessage(LogLevel.Information, "[RELAY_MANAGER] Relay info added, user [{userId}], room [{roomId}], registered mapping count: {mappingCount}")]
    public static partial void LogRelayInfoAdded(this ILogger logger, Guid userId, Guid roomId, int mappingCount);

    [LoggerMessage(LogLevel.Warning, "[RELAY_MANAGER] Relay not found [{sessionId}] {from} -> {to}, possible bug or wrong sender!")]
    public static partial void LogRelayDestinationNotFound(this ILogger logger, SessionId sessionId, Guid? from, Guid? to);

    [LoggerMessage(LogLevel.Debug, "[RELAY_MANAGER] Relay datagram sent from session [{fromSessionId}] to user [{toUserId}]")]
    public static partial void LogRelayDatagramSent(this ILogger logger, SessionId fromSessionId, Guid toUserId);

    [LoggerMessage(LogLevel.Information, "[RELAY_MANAGER] Relay link attached, session [{sessionId}] with user [{userId}]")]
    public static partial void LogRelayLinkAttached(this ILogger logger, SessionId sessionId, Guid userId);

    [LoggerMessage(LogLevel.Warning, "[RELAY_MANAGER] Relay info update unauthorized from session [{sessionId}] {address}")]
    public static partial void LogRelayInfoUpdateUnauthorized(this ILogger logger, SessionId sessionId, IPAddress address);

    [LoggerMessage(LogLevel.Warning, "[RELAY_MANAGER] Can not find corresponding room for user [{sessionId}] {address}")]
    public static partial void LogCanNotFindCorrespondingRoomForUser(this ILogger logger, SessionId sessionId, IPAddress address);

    [LoggerMessage(LogLevel.Warning, "[RELAY_MANAGER] User room does not match the record from session [{sessionId}] {address}")]
    public static partial void LogUserRoomDoesNotMatchTheRecord(this ILogger logger, SessionId sessionId, IPAddress address);
    
    [LoggerMessage(LogLevel.Warning, "[RELAY_MANAGER] Session remote end point is null [{sessionId}]")]
    public static partial void LogSessionRemoteEndPointIsNull(this ILogger logger, SessionId sessionId);

    [LoggerMessage(LogLevel.Critical, "[RELAY_MANAGER] Some session is faking main server [{address}]")]
    public static partial void LogSomeSessionIsFakingMainServer(this ILogger logger, IPEndPoint? address);

    [LoggerMessage(LogLevel.Warning, "[RELAY_MANAGER] Relay link recycle timeout [{sessionId}] {targetId}, link recycled")]
    public static partial void LogRelayLinkRecycleTimeout(this ILogger logger, SessionId sessionId, Guid targetId);
}