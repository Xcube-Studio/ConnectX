using ConnectX.Shared.Messages;
using Hive.Network.Abstractions.Session;
using Hive.Network.Abstractions;
using Hive.Network.Tcp;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Net;
using ConnectX.Relay.Interfaces;
using ConnectX.Relay.Managers;
using ConnectX.Shared.Helpers;
using Hive.Both.General.Dispatchers;
using ConnectX.Shared.Messages.Relay;

namespace ConnectX.Relay;

public class RelayServer : BackgroundService
{
    private const int MaxSessionLoginTimeout = 600;
    private readonly IAcceptor<TcpSession> _acceptor;

    private readonly ClientManager _clientManager;
    private readonly RelayManager _relayManager;

    private readonly IDispatcher _dispatcher;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger _logger;
    private readonly IServerSettingProvider _serverSettingProvider;

    private readonly ConcurrentDictionary<SessionId, (DateTime AddTime, ISession Session)>
        _tempSessionMapping = new();

    private long _currentSessionCount;

    public RelayServer(
        IDispatcher dispatcher,
        IAcceptor<TcpSession> acceptor,
        IServerSettingProvider serverSettingProvider,
        ClientManager clientManager,
        RelayManager relayManager,
        IHostApplicationLifetime lifetime,
        ILogger<RelayServer> logger)
    {
        _dispatcher = dispatcher;
        _acceptor = acceptor;
        _serverSettingProvider = serverSettingProvider;

        _clientManager = clientManager;
        _relayManager = relayManager;

        _lifetime = lifetime;
        _logger = logger;

        _clientManager.OnSessionDisconnected += ClientManagerOnSessionDisconnected;

        _acceptor.BindTo(_dispatcher);
        _dispatcher.AddHandler<CreateRelayLinkMessage>(OnCreateRelayLinkMessageReceived);
    }

    private void ClientManagerOnSessionDisconnected(SessionId sessionId)
    {
        var newVal = Interlocked.Decrement(ref _currentSessionCount);
        _logger.LogCurrentOnline(newVal);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogStartingServer();

        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var (id, (add, session)) in _tempSessionMapping)
            {
                var currentTime = DateTime.UtcNow;
                if (!((currentTime - add).TotalSeconds > MaxSessionLoginTimeout)) continue;

                _logger.LogSessionLoginTimeout(session.Id);
                _logger.LogCurrentOnline(Interlocked.Read(ref _currentSessionCount));

                await _dispatcher.SendAsync(session, new ShutdownMessage(), stoppingToken);
                _tempSessionMapping.TryRemove(id, out _);
            }

            await Task.Delay(1000, stoppingToken);
        }
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await _acceptor.SetupAsync(_serverSettingProvider.RelayEndPoint, cancellationToken);

        Hive.Common.Shared.Helpers.TaskHelper.FireAndForget(() => _acceptor.StartAcceptLoop(cancellationToken));
        _acceptor.OnSessionCreated += AcceptorOnOnSessionCreated;

        _logger.LogServerStarted(_serverSettingProvider.RelayEndPoint);

        await base.StartAsync(cancellationToken);
    }

    private void AcceptorOnOnSessionCreated(IAcceptor acceptor, SessionId id, TcpSession session)
    {
        var currentTime = DateTime.UtcNow;

        session.StartAsync(_lifetime.ApplicationStopping).Forget();

        _tempSessionMapping.AddOrUpdate(
            id,
            _ => (currentTime, session),
            (_, old) =>
            {
                old.Session.Close();
                return (currentTime, session);
            });

        _logger.LogNewSessionJoined(session.RemoteEndPoint!, id);
    }

    private void OnCreateRelayLinkMessageReceived(MessageContext<CreateRelayLinkMessage> ctx)
    {
        var session = ctx.FromSession;

        // Remove temp session mapping
        _tempSessionMapping.TryRemove(session.Id, out _);

        var newVal = Interlocked.Increment(ref _currentSessionCount);

        _logger.LogCurrentOnline(newVal);
        _logger.LogRelayLinkCreateMessageReceived(session.RemoteEndPoint!, session.Id);

        _clientManager.AttachSession(session.Id, session);
        _relayManager.AttachSession(session, ctx.Message.UserId, ctx.Message.RoomId);

        _dispatcher.SendAsync(session, new RelayLinkCreatedMessage()).Forget();
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await _acceptor.TryCloseAsync(cancellationToken);

        _logger.LogServerStopped();

        await base.StopAsync(cancellationToken);
    }
}

internal static partial class ServerLoggers
{
    [LoggerMessage(LogLevel.Information, "[SERVER] Current online [{count}]")]
    public static partial void LogCurrentOnline(this ILogger logger, long count);

    [LoggerMessage(LogLevel.Information, "[SERVER] Starting server...")]
    public static partial void LogStartingServer(this ILogger logger);

    [LoggerMessage(LogLevel.Warning, "[CLIENT] Session [{id}] login timeout, disconnecting...")]
    public static partial void LogSessionLoginTimeout(this ILogger logger, SessionId id);

    [LoggerMessage(LogLevel.Information, "Server started on endpoint [{endPoint}]")]
    public static partial void LogServerStarted(this ILogger logger, IPEndPoint endPoint);

    [LoggerMessage(LogLevel.Information,
        "[CLIENT] New Session joined, EndPoint [{endPoint}] ID [{id}], wait for signin message.")]
    public static partial void LogNewSessionJoined(this ILogger logger, IPEndPoint endPoint, SessionId id);

    [LoggerMessage(LogLevel.Information, "[CLIENT] RelayLinkCreate received from [{endPoint}] ID [{id}]")]
    public static partial void LogRelayLinkCreateMessageReceived(this ILogger logger, IPEndPoint endPoint, SessionId id);

    [LoggerMessage(LogLevel.Information, "Server stopped.")]
    public static partial void LogServerStopped(this ILogger logger);
}