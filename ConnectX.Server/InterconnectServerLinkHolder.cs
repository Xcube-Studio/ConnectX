using System.Collections.Concurrent;
using System.Net;
using ConnectX.Server.Interfaces;
using ConnectX.Server.Messages;
using ConnectX.Shared.Helpers;
using ConnectX.Shared.Messages;
using ConnectX.Shared.Messages.Server;
using Hive.Both.General.Dispatchers;
using Hive.Network.Abstractions.Session;
using Hive.Network.Tcp;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ConnectX.Server;

public class InterconnectServerLinkHolder : BackgroundService
{
    private readonly ConcurrentQueue<IPEndPoint> _pendingEstablishInterconnectServerLinks = [];
    private readonly ConcurrentDictionary<IPEndPoint, ISession> _establishedInterconnectServerLinks = [];

    private readonly IDispatcher _dispatcher;
    private readonly IConnector<TcpSession> _tcpConnector;
    private readonly IServerSettingProvider _serverSettingProvider;
    private readonly IInterconnectServerSettingProvider _interconnectServerSettingProvider;
    private readonly ILogger _logger;

    public InterconnectServerLinkHolder(
        IDispatcher dispatcher,
        IConnector<TcpSession> tcpConnector,
        IServerSettingProvider serverSettingProvider,
        IInterconnectServerSettingProvider interconnectServerSettingProvider,
        ILogger<InterconnectServerLinkHolder> logger)
    {
        _dispatcher = dispatcher;
        _tcpConnector = tcpConnector;
        _serverSettingProvider = serverSettingProvider;
        _interconnectServerSettingProvider = interconnectServerSettingProvider;
        _logger = logger;
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var endPoint in _interconnectServerSettingProvider.EndPoints)
        {
            _pendingEstablishInterconnectServerLinks.Enqueue(endPoint);
            _logger.LogInterconnectServerLinkPending(endPoint);
        }

        return base.StartAsync(cancellationToken);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Task[] tasks =
        [
            KeepInterconnectLinkHeartBeatAsync(stoppingToken),
            EstablishLinkAsync(stoppingToken)
        ];

        return Task.WhenAll(tasks);
    }

    private async Task KeepInterconnectLinkHeartBeatAsync(CancellationToken stoppingToken)
    {
        _logger.LogStartInterconnectServerHeartbeatSendLoop();

        while (!stoppingToken.IsCancellationRequested)
        {
            var sessionsNeedToReconnect = new List<(IPEndPoint, TcpSession)>();

            foreach (var (endPoint, session) in _establishedInterconnectServerLinks)
            {
                if (session is not TcpSession tcpSession)
                    continue;

                if (!tcpSession.IsConnected)
                {
                    sessionsNeedToReconnect.Add((endPoint, tcpSession));
                    continue;
                }

                _dispatcher.SendAsync(tcpSession, new HeartBeat(), stoppingToken).Forget();
            }

            foreach (var (endPoint, session) in sessionsNeedToReconnect)
            {
                _logger.LogInterconnectServerLinkDisconnected(endPoint);
                _establishedInterconnectServerLinks.TryRemove(endPoint, out _);
                _pendingEstablishInterconnectServerLinks.Enqueue(endPoint);
                session.Close();
            }

            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }

        _logger.LogInterconnectServerHeartbeatSendLoopStopped();
    }

    private async Task EstablishLinkAsync(CancellationToken stoppingToken)
    {
        _logger.LogStartInterconnectServerEstablishLoop();

        while (!stoppingToken.IsCancellationRequested)
        {
            while (_pendingEstablishInterconnectServerLinks.TryDequeue(out var endPoint))
            {
                _logger.LogTryingToConnectToRemoteServer(endPoint);

                TcpSession? session = null;

                try
                {
                    using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    using var linkedConnectCts = CancellationTokenSource.CreateLinkedTokenSource(
                        stoppingToken,
                        connectCts.Token);

                    session = await _tcpConnector.ConnectAsync(endPoint, linkedConnectCts.Token);
                }
                catch (OperationCanceledException)
                {
                    // Ignore
                }

                if (session == null)
                {
                    _logger.LogFailedToConnectToRemoteServer(endPoint);
                    _pendingEstablishInterconnectServerLinks.Enqueue(endPoint);

                    await Task.Delay(5000, stoppingToken);

                    continue;
                }

                session.BindTo(_dispatcher);
                session.StartAsync(stoppingToken).Forget();

                _logger.LogSendingInterconnectServerRegistrationToRemoteServer(endPoint);

                await Task.Delay(1000, stoppingToken);

                using var registrationCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using var linkedRegistrationCts = CancellationTokenSource.CreateLinkedTokenSource(
                    stoppingToken,
                    registrationCts.Token);

                var registration = new InterconnectServerRegistration
                {
                    ServerName = _serverSettingProvider.ServerName,
                    ServerMotd = _serverSettingProvider.ServerMotd,
                    ServerAddress = _serverSettingProvider.ServerPublicEndPoint
                };

                var res = await _dispatcher.SendAndListenOnce<InterconnectServerRegistration, InterconnectServerRegistrationSucceeded>(
                    session,
                    registration,
                    linkedRegistrationCts.Token);

                if (res == null)
                {
                    _logger.LogFailedToConnectToRemoteServer(endPoint);
                    _pendingEstablishInterconnectServerLinks.Enqueue(endPoint);
                    continue;
                }

                _logger.LogInterconnectServerRegistrationSucceeded(endPoint);
                _establishedInterconnectServerLinks.TryAdd(endPoint, session);

                // ReSharper disable once PossiblyMistakenUseOfCancellationToken
                await Task.Delay(3000, stoppingToken);
            }

            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }

        _logger.LogInterconnectServerEstablishLoopStopped();
    }
}

internal static partial class InterconnectServerLinkHolderLoggers
{
    [LoggerMessage(LogLevel.Information, "Pending establish interconnect server link with [{endPoint}]...")]
    public static partial void LogInterconnectServerLinkPending(
        this ILogger logger,
        IPEndPoint endPoint);

    [LoggerMessage(LogLevel.Information, "Starting interconnect server heartbeat send loop...")]
    public static partial void LogStartInterconnectServerHeartbeatSendLoop(this ILogger logger);

    [LoggerMessage(LogLevel.Warning, "Interconnect server link with [{endPoint}] disconnected. Waiting for reconnect.")]
    public static partial void LogInterconnectServerLinkDisconnected(
        this ILogger logger,
        IPEndPoint endPoint);

    [LoggerMessage(LogLevel.Information, "Starting interconnect server establish loop...")]
    public static partial void LogStartInterconnectServerEstablishLoop(this ILogger logger);

    [LoggerMessage(LogLevel.Information, "Interconnect server establish loop stopped.")]
    public static partial void LogInterconnectServerEstablishLoopStopped(this ILogger logger);

    [LoggerMessage(LogLevel.Information, "Interconnect server heartbeat send loop stopped.")]
    public static partial void LogInterconnectServerHeartbeatSendLoopStopped(this ILogger logger);

    [LoggerMessage(LogLevel.Information, "Trying to connect to remote server [{endPoint}]...")]
    public static partial void LogTryingToConnectToRemoteServer(
        this ILogger logger,
        IPEndPoint endPoint);

    [LoggerMessage(LogLevel.Warning, "Failed to connect to remote server [{endPoint}].")]
    public static partial void LogFailedToConnectToRemoteServer(
        this ILogger logger,
        IPEndPoint endPoint);

    [LoggerMessage(LogLevel.Information, "Sending interconnect server registration to remote server [{endPoint}]...")]
    public static partial void LogSendingInterconnectServerRegistrationToRemoteServer(
        this ILogger logger,
        IPEndPoint endPoint);

    [LoggerMessage(LogLevel.Information, "Interconnect server registration succeeded with [{endPoint}]...")]
    public static partial void LogInterconnectServerRegistrationSucceeded(
        this ILogger logger,
        IPEndPoint endPoint);
}