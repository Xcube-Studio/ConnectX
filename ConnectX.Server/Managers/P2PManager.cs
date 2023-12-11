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

    private readonly ConcurrentDictionary<Guid, ISession> _userSessionMappings = new();
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

    private void ClientManagerOnSessionDisconnected(SessionId sessionId)
    {
        if (!_groupManager.TryGetUser(sessionId, out var user))
            return;
        
        _logger.LogInformation(
            "[P2P_MANAGER] User {userId} disconnected, session id: {sessionId}",
            user!.UserId, sessionId.Id);
        
        _userSessionMappings.TryRemove(user.UserId, out _);
    }

    private void OnReceivedP2PConRequest(MessageContext<P2PConRequest> ctx)
    {
        var session = ctx.FromSession;
        var message = ctx.Message;
        
        _conRequests.TryAdd(message.Bargain, (session, message));

        if (_userSessionMappings.TryGetValue(message.TargetId, out var targetConnection))
        {
            _logger.LogWarning(
                "[P2P_MANAGER] User {userId} trying to make P2P conn with target [{targetId}], but the target does not exist",
                message.SelfId, message.TargetId);
            
            var err = new P2POpResult(false, "Target does not exist");
            ctx.Dispatcher.SendAsync(session, err).Forget();
            
            return;
        }
        
        _dispatcher.SendAsync(
            targetConnection!,
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
        var session = ctx.FromSession;
        var message = ctx.Message;

        if (!_conRequests.TryGetValue(message.Bargain, out var value))
        {
            _logger.LogWarning(
                "[P2P_MANAGER] User {userId} trying to accept P2P conn, but the request does not exist, session id: {sessionId}",
                message.SelfId, ctx.FromSession.Id.Id);
            
            var err = new P2POpResult(false, "Request does not exist");
            ctx.Dispatcher.SendAsync(session, err).Forget();
            
            return;
        }
        
        var (requesterCon, request) = value;
        _conRequests.TryRemove(message.Bargain, out _);

        var time = DateTime.UtcNow.AddSeconds(5).Ticks;
        var recipientIpBelievable = Equals(message.PublicAddress, session.RemoteEndPoint?.Address);
        var requesterIpBelievable = Equals(request.PublicAddress, requesterCon.RemoteEndPoint?.Address);

        if (message.PortDeterminationMode == PortDeterminationMode.UseTempLinkPort)
            message.PublicPort = (ushort)session.RemoteEndPoint!.Port;
        if (request.PortDeterminationMode == PortDeterminationMode.UseTempLinkPort)
            request.PublicPort = (ushort)requesterCon.RemoteEndPoint!.Port;

        _dispatcher.SendAsync(
            requesterCon,
            new P2PConReady(message.SelfId, time, message)
            {
                PublicAddress = recipientIpBelievable ? message.PublicAddress : session.RemoteEndPoint?.Address,
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
        ctx.Dispatcher.SendAsync(session, result).Forget();
        
        _logger.LogInformation(
            "[P2P_MANAGER] User {userId} accepted P2P conn, session id: {sessionId}",
            message.SelfId, ctx.FromSession.Id.Id);
    }

    private IEnumerable<Guid> GetPossibleInterconnectUsers(SigninMessage user)
    {
        foreach (var tempUser in _groupManager.GetAllUsers())
        {
            if (tempUser.UserId == user.Id) continue;
            
            switch (user.MappingBehavior)
            {
                case MappingBehavior.EndpointIndependent:
                    if (tempUser.MappingBehavior
                        is MappingBehavior.EndpointIndependent
                        or MappingBehavior.AddressDependent
                        or MappingBehavior.AddressAndPortDependent)
                        yield return tempUser.UserId;
                    break;
                case MappingBehavior.AddressDependent:
                case MappingBehavior.AddressAndPortDependent:
                    if (tempUser.MappingBehavior == MappingBehavior.EndpointIndependent)
                        yield return tempUser.UserId;
                    break;
            }
        }
    }
    
    public void AttachSession(
        ISession session,
        SigninMessage signinMessage)
    {
        if (!signinMessage.JoinP2PNetwork)
        {
            _logger.LogInformation(
                "[P2P_MANAGER] User {userId} requested to not join the P2P network, session id: {sessionId}",
                signinMessage.Id,
                session.Id.Id);
            return;
        }
        
        if (!_userSessionMappings.TryAdd(signinMessage.Id, session))
        {
            _logger.LogError(
                "[P2P_MANAGER] Failed to add session to the session mapping, session id: {sessionId}",
                session.Id.Id);
            return;
        }
        
        _logger.LogInformation(
            "[P2P_MANAGER] User {userId} requested to join the P2P network, session id: {sessionId}",
            signinMessage.Id,
            session.Id.Id);

        var possibleInterconnectUsers =
            GetPossibleInterconnectUsers(signinMessage)
                .ToArray();

        _logger.LogInformation(
            "[P2P_MANAGER] User {userId} has {count} possible interconnect users, session id: {sessionId}",
            signinMessage.Id, possibleInterconnectUsers.Length, session.Id.Id);

        var message = new P2PInterconnect(possibleInterconnectUsers);
        
        _dispatcher.SendAsync(session, message).Forget();
    }
}