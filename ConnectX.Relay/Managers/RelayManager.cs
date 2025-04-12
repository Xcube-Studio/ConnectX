using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using CommunityToolkit.HighPerformance;
using ConnectX.Relay.Helpers;
using ConnectX.Relay.Interfaces;
using ConnectX.Shared.Helpers;
using ConnectX.Shared.Messages.Relay;
using ConnectX.Shared.Messages.Relay.Datagram;
using ConnectX.Shared.Models;
using Hive.Both.General.Dispatchers;
using Hive.Network.Abstractions;
using Hive.Network.Abstractions.Session;
using Microsoft.Extensions.Logging;

namespace ConnectX.Relay.Managers;

public class RelayManager
{
    private readonly ConcurrentDictionary<SessionId, Guid> _sessionUserIdMapping = new();
    private readonly ConcurrentDictionary<Guid, Guid> _userIdToRoomMapping = new();
    private readonly ConcurrentDictionary<Guid, Guid> _roomOwnerRecords = new();

    private readonly ConcurrentDictionary<Guid, ISession> _userIdDataSessionMapping = new();

    private readonly ConcurrentDictionary<SessionId, (Guid From, Guid To)> _workerSessionRouteMapping = new();
    private readonly ConcurrentDictionary<(Guid From, Guid To), ISession> _workerSessionMapping = new();

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

    public void AttachDataSession(
        ISession session,
        Guid userId,
        Guid roomId)
    {
        if (!_userIdToRoomMapping.TryGetValue(userId, out var registeredRoomId))
        {
            _logger.LogCanNotFindCorrespondingRoomForUser(session.Id, userId, session.RemoteEndPoint?.Address ?? IPAddress.None);
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

        _userIdDataSessionMapping.AddOrUpdate(userId, _ => session, (_, _) => session);
        _sessionUserIdMapping.AddOrUpdate(session.Id, _ => userId, (_, _) => userId);
    }

    public void AttachWorkerSession(
        ISession session,
        Guid userId,
        Guid relayTo,
        Guid roomId)
    {
        if (!_userIdToRoomMapping.TryGetValue(userId, out var registeredRoomId))
        {
            _logger.LogCanNotFindCorrespondingRoomForUser(session.Id, userId, session.RemoteEndPoint?.Address ?? IPAddress.None);
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

        _workerSessionRouteMapping.AddOrUpdate(session.Id, _ => (userId, relayTo), (_, _) => (userId, relayTo));
        _workerSessionMapping.AddOrUpdate((userId, relayTo), _ => session, (_, _) => session);

        session.OnMessageReceived -= _dispatcher.Dispatch;
        session.OnMessageReceived += SessionOnOnRawStreamReceived;

        _logger.LogRelayWorkerLinkAttached(session.Id, userId);
    }

    private void SessionOnOnRawStreamReceived(ISession session, ReadOnlySequence<byte> buffer)
    {
        _logger.LogCritical(buffer.Length.ToString());

        if (!_workerSessionRouteMapping.TryGetValue(session.Id, out var routingInfo) ||
            !_workerSessionMapping.TryGetValue((routingInfo.To, routingInfo.From), out var toSession))
        {
            _logger.LogRelayWorkerDestinationNotFound(session.Id, routingInfo.From, routingInfo.To);
            return;
        }

        using var ms = buffer.AsStream();
        toSession.TrySendAsync(ms).Forget();

        _logger.LogRelayDatagramSent(session.Id, routingInfo.To);
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
            case GroupUserStates.Joined when message.IsGroupOwner:
                _roomOwnerRecords.AddOrUpdate(message.UserId, message.UserId, (_, _) => message.UserId);
                _userIdToRoomMapping.AddOrUpdate(message.UserId, message.RoomId, (_, _) => message.RoomId);
                _logger.LogRelayInfoAdded(message.UserId, message.RoomId, _userIdToRoomMapping.Count, message.IsGroupOwner);
                break;
            case GroupUserStates.Joined:
                _userIdToRoomMapping.AddOrUpdate(message.UserId, message.RoomId, (_, _) => message.RoomId);
                _logger.LogRelayInfoAdded(message.UserId, message.RoomId, _userIdToRoomMapping.Count, message.IsGroupOwner);
                break;
            case GroupUserStates.Dismissed:
                _roomOwnerRecords.TryRemove(message.UserId, out _);
                _userIdToRoomMapping.TryRemove(message.UserId, out _);
                break;
            case GroupUserStates.Left:
            case GroupUserStates.Kicked:
            case GroupUserStates.Disconnected:
                if (!_roomOwnerRecords.TryGetValue(message.UserId, out _))
                {
                    _userIdToRoomMapping.TryRemove(message.UserId, out _);
                    _logger.LogRelayDestroyed(message.RoomId, message.UserId, message.State);
                }
                
                break;
        }
    }

    private void OnRelayDatagramReceived(MessageContext<RelayDatagram> ctx)
    {
        var message = ctx.Message;

        if (!_userIdDataSessionMapping.TryGetValue(message.To, out var session))
        {
            _logger.LogRelayDestinationNotFound(ctx.FromSession.Id, message.From, message.To);
            return;
        }

        var unwrappedMessage = new UnwrappedRelayDatagram(message.From, message.Payload);

        _dispatcher.SendAsync(session, unwrappedMessage).Forget();

        _logger.LogRelayDatagramSent(ctx.FromSession.Id, message.To);
    }

    private void ClientManagerOnSessionDisconnected(SessionId sessionId)
    {
        if (!_sessionUserIdMapping.TryRemove(sessionId, out var userId)) return;
        if (!_userIdDataSessionMapping.TryRemove(userId, out var session)) return;
        if (!_roomOwnerRecords.TryGetValue(userId, out _))
        {
            if (!_userIdToRoomMapping.TryRemove(userId, out var roomId)) return;

            _logger.LogRelayDestroyed(roomId, userId, GroupUserStates.Disconnected);
        }

        // Because the session is already disconnected, we just recycle the link with it.
        // No need to ask the client's current ref count
        if (_userIdDataSessionMapping.Any(p => p.Value == session)) return;

        session.Close();

        if (!_workerSessionRouteMapping.TryRemove(session.Id, out var routingInfo))
            return;
        if (!_workerSessionMapping.TryRemove(routingInfo, out var workerSession))
            return;

        workerSession.Close();
    }
}

internal static partial class RelayManagerLoggers
{
    [LoggerMessage(LogLevel.Warning, "[RELAY_MANAGER] RelayTo is empty from session [{sessionId}], possible bug or wrong sender!")]
    public static partial void LogRelayToEmpty(this ILogger logger, SessionId sessionId);

    [LoggerMessage(LogLevel.Information, "[RELAY_MANAGER] Relay destroyed, room [{roomId}], user [{userId}], state [{state}]")]
    public static partial void LogRelayDestroyed(this ILogger logger, Guid roomId, Guid userId, GroupUserStates state);

    [LoggerMessage(LogLevel.Information, "[RELAY_MANAGER] Relay info added, user [{userId}], room [{roomId}], is room owner [{isRoomOwner}], registered mapping count: {mappingCount}")]
    public static partial void LogRelayInfoAdded(this ILogger logger, Guid userId, Guid roomId, int mappingCount, bool isRoomOwner);

    [LoggerMessage(LogLevel.Warning, "[RELAY_MANAGER] Relay worker not found [{sessionId}] {from} -> {to}, possible bug or wrong sender!")]
    public static partial void LogRelayWorkerDestinationNotFound(this ILogger logger, SessionId sessionId, Guid? from, Guid? to);

    [LoggerMessage(LogLevel.Warning, "[RELAY_MANAGER] Relay not found [{sessionId}] {from} -> {to}, possible bug or wrong sender!")]
    public static partial void LogRelayDestinationNotFound(this ILogger logger, SessionId sessionId, Guid? from, Guid? to);

    [LoggerMessage(LogLevel.Debug, "[RELAY_MANAGER] Relay datagram sent from session [{fromSessionId}] to user [{toUserId}]")]
    public static partial void LogRelayDatagramSent(this ILogger logger, SessionId fromSessionId, Guid toUserId);

    [LoggerMessage(LogLevel.Information, "[RELAY_MANAGER] Relay link attached, session [{sessionId}] with user [{userId}]")]
    public static partial void LogRelayLinkAttached(this ILogger logger, SessionId sessionId, Guid userId);

    [LoggerMessage(LogLevel.Information, "[RELAY_MANAGER] Relay worker link attached, session [{sessionId}] with user [{userId}]")]
    public static partial void LogRelayWorkerLinkAttached(this ILogger logger, SessionId sessionId, Guid userId);

    [LoggerMessage(LogLevel.Warning, "[RELAY_MANAGER] Relay info update unauthorized from session [{sessionId}] {address}")]
    public static partial void LogRelayInfoUpdateUnauthorized(this ILogger logger, SessionId sessionId, IPAddress address);

    [LoggerMessage(LogLevel.Warning, "[RELAY_MANAGER] Can not find corresponding room for user [{sessionId}][{userId}] {address}")]
    public static partial void LogCanNotFindCorrespondingRoomForUser(this ILogger logger, SessionId sessionId, Guid userId, IPAddress address);

    [LoggerMessage(LogLevel.Warning, "[RELAY_MANAGER] User room does not match the record from session [{sessionId}] {address}")]
    public static partial void LogUserRoomDoesNotMatchTheRecord(this ILogger logger, SessionId sessionId, IPAddress address);
    
    [LoggerMessage(LogLevel.Warning, "[RELAY_MANAGER] Session remote end point is null [{sessionId}]")]
    public static partial void LogSessionRemoteEndPointIsNull(this ILogger logger, SessionId sessionId);

    [LoggerMessage(LogLevel.Critical, "[RELAY_MANAGER] Some session is faking main server [{address}]")]
    public static partial void LogSomeSessionIsFakingMainServer(this ILogger logger, IPEndPoint? address);

    [LoggerMessage(LogLevel.Warning, "[RELAY_MANAGER] Relay link recycle timeout [{sessionId}] {targetId}, link recycled")]
    public static partial void LogRelayLinkRecycleTimeout(this ILogger logger, SessionId sessionId, Guid targetId);
}