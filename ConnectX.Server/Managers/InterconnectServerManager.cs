using System.Collections.Concurrent;
using System.Net;
using Hive.Network.Abstractions.Session;
using Hive.Network.Abstractions;
using Microsoft.Extensions.Logging;
using ConnectX.Server.Messages.Queries;
using ConnectX.Shared.Helpers;
using Hive.Both.General.Dispatchers;
using ConnectX.Server.Interfaces;
using ConnectX.Shared.Messages.Group;
using ConnectX.Shared.Messages.Server;
using Microsoft.Extensions.DependencyInjection;

namespace ConnectX.Server.Managers;

public class InterconnectServerManager
{
    private readonly ConcurrentDictionary<SessionId, InterconnectServerRegistration> _registerServerInfo = [];
    private readonly ConcurrentDictionary<SessionId, ISession> _sessionMapping = new();

    private readonly ClientManager _clientManager;
    private readonly IInterconnectServerSettingProvider _interconnectServerSettingProvider;
    private readonly IDispatcher _dispatcher;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly ILogger _logger;

    public InterconnectServerManager(
        ClientManager clientManager,
        IInterconnectServerSettingProvider interconnectServerSettingProvider,
        IDispatcher dispatcher,
        IServiceScopeFactory serviceScopeFactory,
        ILogger<InterconnectServerManager> logger)
    {
        _clientManager = clientManager;
        _interconnectServerSettingProvider = interconnectServerSettingProvider;
        _dispatcher = dispatcher;
        _serviceScopeFactory = serviceScopeFactory;
        _logger = logger;

        _clientManager.OnSessionDisconnected += OnClientSessionDisconnected;

        // This should only be used for current server
        _dispatcher.AddHandler<QueryRemoteServerRoomInfo>(OnQueryRemoteServerRoomInfoReceived);
    }

    public bool IsServerRegisteredForInterconnect(SessionId sessionId)
    {
        return _sessionMapping.ContainsKey(sessionId);
    }

    private void OnClientSessionDisconnected(SessionId sessionId)
    {
        if (!_sessionMapping.TryRemove(sessionId, out var session))
            return;

        session.Close();

        if (!_registerServerInfo.TryRemove(sessionId, out var regInfo))
            return;

        _logger.LogInterconnectServerDisconnected(sessionId, regInfo.ServerAddress, regInfo.ServerName);
    }

    private void OnQueryRemoteServerRoomInfoReceived(MessageContext<QueryRemoteServerRoomInfo> ctx)
    {
        using var scope = _serviceScopeFactory.CreateScope();
        var groupManager = scope.ServiceProvider.GetRequiredService<GroupManager>();

        var fromSession = ctx.FromSession;

        if (!groupManager.TryQueryGroup(ctx.Message, out var group))
        {
            _logger.LogRemoteServerQueriedGroupButNotFound(
                ctx.FromSession.RemoteEndPoint,
                ctx.Message.JoinGroup.GroupId);

            var failedRes = new QueryRemoteServerRoomInfoResponse(true);

            _dispatcher.SendAsync(fromSession, failedRes).Forget();

            return;
        }

        var res = new QueryRemoteServerRoomInfoResponse(true);

        _dispatcher.SendAsync(fromSession, res).Forget();

        _logger.LogRemoteServerQueriedRoomInfo(
            ctx.FromSession.RemoteEndPoint,
            group.RoomId,
            group.RoomName);
    }

    public async Task<InterconnectServerRegistration?> TryFindTargetRemoteServerForRoomAsync(JoinGroup joinGroup)
    {
        var query = new QueryRemoteServerRoomInfo
        {
            JoinGroup = joinGroup
        };

        foreach (var (sessionId, session) in _sessionMapping)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var res = await _dispatcher.SendAndListenOnce<QueryRemoteServerRoomInfo, QueryRemoteServerRoomInfoResponse>(
                session,
                query,
                cts.Token);

            if (res == null) continue;
            if (!res.Found) continue;
            if (!_registerServerInfo.TryGetValue(sessionId, out var regInfo))
            {
                _logger.LogRoomFoundButMappingBroken(sessionId);
                break;
            }

            _logger.LogRoomInfoFoundFromRemoteServer(sessionId, regInfo);

            return regInfo;
        }

        _logger.LogFailedToGetRoomInfoFromRemoteServer(joinGroup);

        return null;
    }

    public SessionId AttachSession(
        SessionId id,
        ISession session,
        InterconnectServerRegistration message)
    {
        if (!_interconnectServerSettingProvider.EndPoints.Contains(message.ServerAddress))
        {
            _logger.LogUnauthorizedServerTryingToMakeInterconnect(message.ServerAddress);
            session.Close();

            return id;
        }

        _registerServerInfo.AddOrUpdate(id, _ => message, (_, _) => message);
        _sessionMapping.AddOrUpdate(id, _ => session, (_, oldSession) =>
        {
            _logger.LogInterconnectServerDisconnectedBecauseReplaced(id, message.ServerAddress, message.ServerName);
            oldSession.Close();

            return session;
        });

        _logger.LogInterconnectServerAttached(
            id,
            message.ServerName,
            message.ServerAddress);

        return id;
    }
}

internal static partial class InterconnectServerManagerLoggers
{
    [LoggerMessage(
        LogLevel.Information,
        "Remote server [{RemoteEndPoint}] queried group {RoomId} but it does not exist on current server.")]
    public static partial void LogRemoteServerQueriedGroupButNotFound(
        this ILogger logger,
        IPEndPoint? remoteEndPoint,
        Guid roomId);

    [LoggerMessage(
        LogLevel.Information,
        "Remote server [{RemoteEndPoint}] queried group {RoomId} with name {RoomName}.")]
    public static partial void LogRemoteServerQueriedRoomInfo(
        this ILogger logger,
        IPEndPoint? remoteEndPoint,
        Guid roomId,
        string roomName);

    [LoggerMessage(
        LogLevel.Information,
        "Interconnect server [{SessionId}] disconnected from [{ServerAddress}] with name {ServerName}.")]
    public static partial void LogInterconnectServerDisconnected(
        this ILogger logger,
        SessionId sessionId,
        IPEndPoint serverAddress,
        string serverName);

    [LoggerMessage(
        LogLevel.Information,
        "Interconnect server [{SessionId}] disconnected from [{ServerAddress}] with name {ServerName} because it was replaced.")]
    public static partial void LogInterconnectServerDisconnectedBecauseReplaced(
        this ILogger logger,
        SessionId sessionId,
        IPEndPoint serverAddress,
        string serverName);

    [LoggerMessage(
        LogLevel.Information,
        "Interconnect server [{SessionId}] attached with name {ServerName} from [{ServerAddress}].")]
    public static partial void LogInterconnectServerAttached(
        this ILogger logger,
        SessionId sessionId,
        string serverName,
        IPEndPoint serverAddress);

    [LoggerMessage(
        LogLevel.Critical,
        "Unauthorized server [{ServerAddress}] trying to make interconnect. Connection has been closed.")]
    public static partial void LogUnauthorizedServerTryingToMakeInterconnect(
        this ILogger logger,
        IPEndPoint serverAddress);

    [LoggerMessage(
        LogLevel.Error,
        "Room found but mapping is broken. Session [{SessionId}] does not exist. Possible internal error or bug!")]
    public static partial void LogRoomFoundButMappingBroken(
        this ILogger logger,
        SessionId sessionId);

    [LoggerMessage(
        LogLevel.Error,
        "Failed to get room info from remote server [{JoinGroup}].")]
    public static partial void LogFailedToGetRoomInfoFromRemoteServer(
        this ILogger logger,
        JoinGroup joinGroup);

    [LoggerMessage(
        LogLevel.Information,
        "Room info found from remote server [{SessionId}] with registration {Registration}.")]
    public static partial void LogRoomInfoFoundFromRemoteServer(
        this ILogger logger,
        SessionId sessionId,
        InterconnectServerRegistration registration);
}