using Microsoft.Extensions.Logging;
using ConnectX.Shared.Messages.Relay;
using Hive.Both.General.Dispatchers;
using Hive.Network.Abstractions;
using Hive.Network.Abstractions.Session;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using ConnectX.Server.Interfaces;
using ConnectX.Shared.Helpers;

namespace ConnectX.Server.Managers;

public class RelayServerManager
{
    private readonly ClientManager _clientManager;
    private readonly IServerSettingProvider _serverSettingProvider;
    private readonly IDispatcher _dispatcher;
    private readonly ILogger _logger;

    private readonly ConcurrentDictionary<SessionId, Guid> _sessionIdMapping = new();
    private readonly ConcurrentDictionary<Guid, ISession> _sessionMapping = new();
    private readonly ConcurrentDictionary<Guid, IPEndPoint> _serverAddressMapping = new();
    private readonly ConcurrentDictionary<IPEndPoint, ISession> _relayAddressSessionMapping = new();

    public RelayServerManager(
        ClientManager clientManager,
        IServerSettingProvider serverSettingProvider,
        IDispatcher dispatcher,
        ILogger<ClientManager> logger)
    {
        _clientManager = clientManager;
        _serverSettingProvider = serverSettingProvider;
        _dispatcher = dispatcher;
        _logger = logger;

        _clientManager.OnSessionDisconnected += ClientManagerOnOnSessionDisconnected;

        _dispatcher.AddHandler<RegisterRelayServerMessage>(OnReceivedRegisterRelayServerMessage);
    }

    private bool IsSessionAttached(ISession session)
    {
        if (_clientManager.IsSessionAttached(session.Id)) return true;

        _logger.LogReceivedGroupOpMessageFromUnattachedSession(session.Id);

        return true;
    }

    public IPEndPoint? GetRandomRelayServerAddress()
    {
        if (_serverAddressMapping.IsEmpty) return null;

        return Random.Shared.GetItems(_serverAddressMapping.Values.ToArray(), 1)[0];
    }

    public bool TryRelayServerSession(IPEndPoint endPoint, [NotNullWhen(true)] out ISession? session)
    {
        return _relayAddressSessionMapping.TryGetValue(endPoint, out session);
    }

    /// <summary>
    ///     Attach the session to the manager
    /// </summary>
    /// <param name="id"></param>
    /// <param name="sessionUserId"></param>
    /// <param name="session"></param>
    /// <returns>the assigned id for the session, if the return value is default, it means the add has failed</returns>
    public void AttachSession(
        SessionId id,
        Guid sessionUserId,
        ISession session)
    {
        if (!_clientManager.IsSessionAttached(id))
        {
            _logger.LogFailedToAttachSession(id);
            return;
        }

        if (!_sessionMapping.TryAdd(sessionUserId, session) ||
            !_sessionIdMapping.TryAdd(id, sessionUserId))
        {
            _logger.LogRelayServerManagerFailedToAddSessionToSessionMapping(id);
            return;
        }
    }

    private void ClientManagerOnOnSessionDisconnected(SessionId sessionId)
    {
        if (!_sessionIdMapping.TryRemove(sessionId, out var userId)) return;
        if (!_sessionMapping.TryRemove(userId, out var session)) return;
        if (!_serverAddressMapping.TryRemove(userId, out var endPoint)) return;
        if (!_relayAddressSessionMapping.TryRemove(endPoint, out _)) return;

        session.Close();
    }

    private void OnReceivedRegisterRelayServerMessage(MessageContext<RegisterRelayServerMessage> ctx)
    {
        if (!IsSessionAttached(ctx.FromSession)) return;
        if (!_sessionIdMapping.TryGetValue(ctx.FromSession.Id, out var userId)) return;
        if (!_sessionMapping.TryGetValue(userId, out var session)) return;
        if (ctx.Message.ServerId == Guid.Empty ||
            _serverSettingProvider.ServerId != ctx.Message.ServerId)
        {
            _logger.LogFailedToRegisterRelayServerBecauseIdNotMatch(ctx.Message.ServerId);
            return;
        }

        if (!_serverAddressMapping.TryAdd(userId, ctx.Message.ServerAddress))
        {
            _logger.LogFailedToAddServerAddressMapping(userId);
            return;
        }

        if (!_relayAddressSessionMapping.TryAdd(ctx.Message.ServerAddress, ctx.FromSession))
        {
            _logger.LogFailedToAddServerAddressMapping(userId);
            return;
        }

        _dispatcher.SendAsync(session, new RelayServerRegisteredMessage(_serverSettingProvider.ServerId)).Forget();

        _logger.LogRelayServerAttached(ctx.FromSession.Id, ctx.Message.ServerId);
        _logger.LogRelayServerRegistered(ctx.FromSession.Id, ctx.Message.ServerId);
    }
}

internal static partial class RelayServerManagerLoggers
{
    [LoggerMessage(LogLevel.Information, "[RELAY_SERVER_MANAGER] Relay server attached {SessionId} with user {UserId}")]
    public static partial void LogRelayServerAttached(this ILogger logger, SessionId sessionId, Guid userId);

    [LoggerMessage(LogLevel.Error, "[RELAY_SERVER_MANAGER] Failed to attach session {SessionId} to relay server manager.")]
    public static partial void LogRelayServerManagerFailedToAddSessionToSessionMapping(this ILogger logger, SessionId sessionId);

    [LoggerMessage(LogLevel.Warning, "[RELAY_SERVER_MANAGER] Failed to register relay server because id [{id}] not match.")]
    public static partial void LogFailedToRegisterRelayServerBecauseIdNotMatch(this ILogger logger, Guid id);

    [LoggerMessage(LogLevel.Information, "[RELAY_SERVER_MANAGER] Relay server registered {SessionId} with server id {ServerId}")]
    public static partial void LogRelayServerRegistered(this ILogger logger, SessionId sessionId, Guid serverId);

    [LoggerMessage(LogLevel.Error, "[RELAY_SERVER_MANAGER] Failed to add server address mapping {ServerId}")]
    public static partial void LogFailedToAddServerAddressMapping(this ILogger logger, Guid serverId);
}