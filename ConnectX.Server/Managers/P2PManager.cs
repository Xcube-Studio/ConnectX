using System.Collections.Concurrent;
using ConnectX.Shared.Helpers;
using ConnectX.Shared.Messages.Identity;
using ConnectX.Shared.Messages.P2P;
using ConnectX.Shared.Models;
using Hive.Both.General.Dispatchers;
using Hive.Network.Abstractions;
using Hive.Network.Abstractions.Session;
using Microsoft.Extensions.Logging;
using STUN.Enums;

namespace ConnectX.Server.Managers;

public class P2PManager
{
    private readonly IDispatcher _dispatcher;
    private readonly ClientManager _clientManager;
    private readonly GroupManager _groupManager;
    private readonly ILogger _logger;

    private readonly ConcurrentDictionary<SessionId, Guid> _sessionIdMapping = new();
    private readonly ConcurrentDictionary<Guid, ISession> _userSessionMappings = new();
    private readonly ConcurrentDictionary<SessionId, List<ISession>> _tempLinkMappings = new();
    private readonly ConcurrentDictionary<int, (ISession, P2PConRequest)> _conRequests = new();
    
    public P2PManager(
        IDispatcher dispatcher,
        ClientManager clientManager,
        GroupManager groupManager,
        ILogger<P2PManager> logger)
    {
        _dispatcher = dispatcher;
        _clientManager = clientManager;
        _groupManager = groupManager;
        _logger = logger;
        
        _clientManager.OnSessionDisconnected += ClientManagerOnSessionDisconnected;

        _dispatcher.AddHandler<P2PConRequest>(OnReceivedP2PConRequest);
        _dispatcher.AddHandler<P2PConAccept>(OnReceivedP2PConAccept);
    }
    
    public event SessionDisconnectedHandler? OnSessionDisconnected;

    private void ClientManagerOnSessionDisconnected(SessionId sessionId)
    {
        _logger.LogInformation(
            "[P2P_MANAGER] User disconnected, session id: {sessionId}",
            sessionId.Id);

        if (_sessionIdMapping.TryRemove(sessionId, out var userId) &&
            _userSessionMappings.TryRemove(userId, out var attachedSession))
        {
            attachedSession.Close();
        }
            
        if (!_tempLinkMappings.TryRemove(sessionId, out var tempSessions)) return;
        
        foreach (var session in tempSessions)
        {
            session.Close();
            OnSessionDisconnected?.Invoke(session.Id);
        }
    }

    private void OnReceivedP2PConRequest(MessageContext<P2PConRequest> ctx)
    {
        _logger.LogInformation(
            "[P2P_MANAGER] User {userId} trying to make P2P conn with target [{targetId}], session id: {sessionId}",
            ctx.FromSession.Id.Id, ctx.Message.TargetId, ctx.FromSession.Id.Id);
        
        var session = ctx.FromSession;
        var message = ctx.Message;

        if (!_conRequests.TryAdd(message.Bargain, (session, message)))
        {
            _logger.LogError(
                "[P2P_MANAGER] User {userId} trying to make P2P conn with target [{targetId}], but the request already exists",
                message.SelfId, message.TargetId);
        }

        if (!_userSessionMappings.TryGetValue(message.TargetId, out var targetConnection))
        {
            _logger.LogWarning(
                "[P2P_MANAGER] User {userId} trying to make P2P conn with target [{targetId}], but the target does not exist",
                message.SelfId, message.TargetId);
            
            var err = new P2POpResult(false, "Target does not exist");
            ctx.Dispatcher.SendAsync(session, err).Forget();
            
            return;
        }

        _dispatcher.SendAsync(
            targetConnection,
            new P2PConNotification
            {
                Bargain = message.Bargain,
                PartnerIds = message.SelfId,
                PartnerIp = session.RemoteEndPoint!,
            }).Forget();
        
        var result = new P2POpResult(true);
        ctx.Dispatcher.SendAsync(session, result).Forget();

        _logger.LogInformation(
            "[P2P_MANAGER] User {userId} trying to make P2P conn with target [{targetId}], session id: {sessionId}",
            message.SelfId, message.TargetId, session.Id.Id);
    }
    
    private void OnReceivedP2PConAccept(MessageContext<P2PConAccept> ctx)
    {
        _logger.LogInformation(
            "[P2P_MANAGER] User {userId} accepted P2P conn, session id: {sessionId}",
            ctx.FromSession.Id.Id, ctx.FromSession.Id.Id);
        
        var from = ctx.FromSession;
        var message = ctx.Message;

        if (!_conRequests.TryRemove(message.Bargain, out var value))
        {
            _logger.LogWarning(
                "[P2P_MANAGER] User {userId} trying to accept P2P conn, but the request does not exist, session id: {sessionId}",
                message.SelfId, ctx.FromSession.Id.Id);
            
            var err = new P2POpResult(false, "Request does not exist");
            ctx.Dispatcher.SendAsync(from, err).Forget();
            
            return;
        }
        
        var (requesterCon, request) = value;

        var time = DateTime.UtcNow.AddSeconds(5).Ticks;
        var recipientIpBelievable = Equals(message.PublicAddress, from.RemoteEndPoint?.Address);
        var requesterIpBelievable = Equals(request.PublicAddress, requesterCon.RemoteEndPoint?.Address);

        if (message.PortDeterminationMode == PortDeterminationMode.UseTempLinkPort)
            message.PublicPort = (ushort)from.RemoteEndPoint!.Port;
        if (request.PortDeterminationMode == PortDeterminationMode.UseTempLinkPort)
            request.PublicPort = (ushort)requesterCon.RemoteEndPoint!.Port;

        _dispatcher.SendAsync(
            requesterCon,
            new P2PConReady(message.SelfId, time, message)
            {
                PublicAddress = recipientIpBelievable ? message.PublicAddress : from.RemoteEndPoint?.Address,
                Bargain = message.Bargain
            }).Forget();

        var result = new P2POpResult(true)
        {
            Context = new P2PConReady(request.SelfId, time, request)
            {
                PublicAddress = requesterIpBelievable ? request.PublicAddress : requesterCon.RemoteEndPoint?.Address,
                Bargain = message.Bargain
            }
        };
        ctx.Dispatcher.SendAsync(from, result).Forget();
        
        _logger.LogInformation(
            "[P2P_MANAGER] User {userId} accepted P2P conn, session id: {sessionId}",
            message.SelfId, ctx.FromSession.Id.Id);
    }

    private IEnumerable<Guid> GetPossibleInterconnectUsers(Guid selfUserId)
    {
        foreach (var (sessionId, _) in _userSessionMappings)
        {
            if (!_groupManager.TryGetUser(sessionId, out var user)) continue;
            if (user!.UserId == selfUserId) continue;
            
            switch (user.MappingBehavior)
            {
                case MappingBehavior.EndpointIndependent:
                    if (user.MappingBehavior
                        is MappingBehavior.EndpointIndependent
                        or MappingBehavior.AddressDependent
                        or MappingBehavior.AddressAndPortDependent)
                        yield return user.UserId;
                    break;
                case MappingBehavior.AddressDependent:
                case MappingBehavior.AddressAndPortDependent:
                    if (user.MappingBehavior == MappingBehavior.EndpointIndependent)
                        yield return user.UserId;
                    break;
            }
        }
    }

    public void AttachTempSession(
        ISession session,
        SigninMessage signinMessage)
    {
        _logger.LogInformation(
            "[P2P_MANAGER] User requested to join the P2P network, session id: {sessionId}",
            signinMessage.Id);

        _tempLinkMappings.AddOrUpdate(session.Id, [session], (_, list) =>
        {
            list.Add(session);
            return list;
        });
        
        _logger.LogInformation(
            "[P2P_MANAGER] User {userId} requested to join the P2P network, session id: {sessionId}",
            signinMessage.Id,
            session.Id.Id);
    }
    
    public void AttachSession(
        ISession session,
        Guid userId,
        SigninMessage signinMessage)
    {
        if (!signinMessage.JoinP2PNetwork)
        {
            _logger.LogInformation(
                "[P2P_MANAGER] User requested to not join the P2P network, session id: {sessionId}",
                session.Id.Id);
            return;
        }
        
        if (!_userSessionMappings.TryAdd(userId, session) ||
            !_sessionIdMapping.TryAdd(session.Id, userId))
        {
            _logger.LogError(
                "[P2P_MANAGER] Failed to add session to the session mapping, session id: {sessionId}",
                session.Id.Id);
            return;
        }

        var possibleInterconnectUsers = GetPossibleInterconnectUsers(userId).ToArray();
        
        if (possibleInterconnectUsers.Length == 0) return;

        _logger.LogInformation(
            "[P2P_MANAGER] User has {count} possible interconnect users, session id: {sessionId}",
            possibleInterconnectUsers.Length, session.Id.Id);

        var message = new P2PInterconnect(possibleInterconnectUsers);
        
        _dispatcher.SendAsync(session, message).Forget();
    }
}