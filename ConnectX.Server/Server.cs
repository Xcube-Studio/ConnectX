using System.Collections.Concurrent;
using System.Net;
using ConnectX.Server.Interfaces;
using ConnectX.Server.Managers;
using ConnectX.Shared.Helpers;
using ConnectX.Shared.Messages;
using ConnectX.Shared.Messages.Identity;
using Hive.Both.General.Dispatchers;
using Hive.Network.Abstractions;
using Hive.Network.Abstractions.Session;
using Hive.Network.Tcp;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ConnectX.Server;

public class Server : BackgroundService
{
    private const int MaxSessionLoginTimeout = 600;
    private readonly IAcceptor<TcpSession> _acceptor;
    private readonly ClientManager _clientManager;
    private readonly P2PManager _p2pManager;

    private readonly IDispatcher _dispatcher;
    private readonly GroupManager _groupManager;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger _logger;
    private readonly IServerSettingProvider _serverSettingProvider;

    private readonly ConcurrentDictionary<SessionId, (DateTime AddTime, ISession Session)>
        _tempSessionMapping = new();

    private long _currentSessionCount;

    public Server(
        IDispatcher dispatcher,
        IAcceptor<TcpSession> acceptor,
        IServerSettingProvider serverSettingProvider,
        GroupManager groupManager,
        ClientManager clientManager,
        P2PManager p2pManager,
        IHostApplicationLifetime lifetime,
        ILogger<Server> logger)
    {
        _dispatcher = dispatcher;
        _acceptor = acceptor;
        _serverSettingProvider = serverSettingProvider;
        _groupManager = groupManager;
        _clientManager = clientManager;
        _p2pManager = p2pManager;
        _lifetime = lifetime;
        _logger = logger;

        _clientManager.OnSessionDisconnected += ClientManagerOnSessionDisconnected;
        _p2pManager.OnSessionDisconnected += ClientManagerOnSessionDisconnected;

        _acceptor.BindTo(_dispatcher);
        _dispatcher.AddHandler<SigninMessage>(OnSigninMessageReceived);
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

                await _dispatcher.SendAsync(session, new ShutdownMessage());
                _tempSessionMapping.TryRemove(id, out _);
            }

            await Task.Delay(1000, stoppingToken);
        }
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await _acceptor.SetupAsync(_serverSettingProvider.ListenIpEndPoint, cancellationToken);

        _acceptor.StartAcceptLoop(cancellationToken);
        _acceptor.OnSessionCreated += AcceptorOnOnSessionCreated;

        _logger.LogServerStarted(_serverSettingProvider.ListenIpEndPoint);

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

    private void OnSigninMessageReceived(MessageContext<SigninMessage> ctx)
    {
        var session = ctx.FromSession;

        // Remove temp session mapping
        if (!_tempSessionMapping.TryRemove(session.Id, out _))
            return;

        var newVal = Interlocked.Increment(ref _currentSessionCount);
        _logger.LogCurrentOnline(newVal);

        _logger.LogSigninMessageReceived(session.RemoteEndPoint!, session.Id);

        if (ctx.Message.Id != Guid.Empty)
        {
            _logger.LogUserCreatedTempLink(ctx.Message.Id);

            _p2pManager.AttachTempSession(session, ctx.Message);
            _dispatcher.SendAsync(session, new SigninSucceeded(ctx.Message.Id)).Forget();
        }
        else
        {
            _clientManager.AttachSession(session.Id, session);
            var userId = _groupManager.AttachSession(session.Id, session, ctx.Message);

            _dispatcher.SendAsync(session, new SigninSucceeded(userId)).Forget();
            _p2pManager.AttachSession(session, userId, ctx.Message);
        }
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

    [LoggerMessage(LogLevel.Information, "[CLIENT] SigninMessage received from [{endPoint}] ID [{id}]")]
    public static partial void LogSigninMessageReceived(this ILogger logger, IPEndPoint endPoint, SessionId id);

    [LoggerMessage(LogLevel.Information,
        "[CLIENT] User [{userId}] created a temp link for P2P connection. Attach session to P2PManager.")]
    public static partial void LogUserCreatedTempLink(this ILogger logger, Guid userId);

    [LoggerMessage(LogLevel.Information, "Server stopped.")]
    public static partial void LogServerStopped(this ILogger logger);
}