using System.Net;
using ConnectX.Client.Interfaces;
using ConnectX.Shared.Helpers;
using ConnectX.Shared.Messages;
using ConnectX.Shared.Messages.Identity;
using Hive.Both.General.Dispatchers;
using Hive.Network.Abstractions.Session;
using Hive.Network.Tcp;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ConnectX.Client;

public class ServerLinkHolder : BackgroundService, IServerLinkHolder
{
    private readonly IDispatcher _dispatcher;
    private readonly ILogger _logger;
    private readonly IClientSettingProvider _settingProvider;
    private readonly IConnector<TcpSession> _tcpConnector;

    public ServerLinkHolder(
        IDispatcher dispatcher,
        IClientSettingProvider settingProvider,
        IConnector<TcpSession> tcpConnector,
        ILogger<ServerLinkHolder> logger)
    {
        _dispatcher = dispatcher;
        _settingProvider = settingProvider;
        _tcpConnector = tcpConnector;
        _logger = logger;

        _dispatcher.AddHandler<SigninSucceeded>(OnSigninSucceededReceived);
        _dispatcher.AddHandler<HeartBeat>(OnHeartBeatReceived);
        _dispatcher.AddHandler<ShutdownMessage>(OnShutdownMessageReceived);
    }

    public ISession? ServerSession { get; private set; }
    public bool IsConnected { get; private set; }
    public bool IsSignedIn { get; private set; }
    public Guid UserId { get; private set; }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogStartingServerLinkHolder();

        await ConnectAsync(cancellationToken);
        await TaskHelper.WaitUntilAsync(() => IsConnected, cancellationToken);
        await base.StartAsync(cancellationToken);
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogStoppingServerLinkHolder();
        return base.StopAsync(cancellationToken);
    }

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        _logger.LogGettingClientNatType();
        _logger.LogConnectingToServer();

        var endPoint = new IPEndPoint(_settingProvider.ServerAddress, _settingProvider.ServerPort);
        var session = await _tcpConnector.ConnectAsync(endPoint, cancellationToken);

        if (session == null)
        {
            _logger.LogFailedToConnectToServer(endPoint);
            return;
        }

        session.BindTo(_dispatcher);
        session.StartAsync(cancellationToken).Forget();

        _logger.LogSendingSigninMessageToServer();

        await Task.Delay(1000, cancellationToken);
        await _dispatcher.SendAsync(session, new SigninMessage
        {
            JoinP2PNetwork = _settingProvider.JoinP2PNetwork,
            DisplayName = session.RemoteEndPoint?.ToString() ?? Guid.NewGuid().ToString("N")
        });

        await TaskHelper.WaitUntilAsync(() => IsSignedIn, cancellationToken);

        _logger.LogConnectedAndSignedToServer(endPoint);

        ServerSession = session;
        IsConnected = true;
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        if (!IsConnected || ServerSession == null) return;

        _logger.LogDisconnectingFromServer();

        await _dispatcher.SendAsync(ServerSession, new ShutdownMessage());
        ServerSession.Close();

        _logger.LogDisconnectedFromServer();

        IsSignedIn = false;
        IsConnected = false;
    }

    private void OnSigninSucceededReceived(MessageContext<SigninSucceeded> obj)
    {
        IsSignedIn = true;
        UserId = obj.Message.UserId;

        _logger.LogSuccessfullyLoggedIn(UserId);

        _dispatcher.RemoveHandler<SigninSucceeded>(OnSigninSucceededReceived);
    }

    private void OnHeartBeatReceived(MessageContext<HeartBeat> ctx)
    {
        _logger.LogHeartbeatReceivedFromServer();
    }

    private void OnShutdownMessageReceived(MessageContext<ShutdownMessage> ctx)
    {
        _logger.LogShutdownMessageReceivedFromServer();
        DisconnectAsync(CancellationToken.None).Forget();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogStartSendingHeartbeat();

        await TaskHelper.WaitUntilAsync(() => IsConnected, stoppingToken);

        while (!stoppingToken.IsCancellationRequested && IsConnected && ServerSession != null)
        {
            await _dispatcher.SendAsync(ServerSession, new HeartBeat(), stoppingToken);
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }

        _logger.LogStopSendingHeartbeat();
    }
}

internal static partial class ServerLinkHolderLoggers
{
    [LoggerMessage(LogLevel.Information, "[CLIENT] Successfully logged into server, assigned id: {id}")]
    public static partial void LogSuccessfullyLoggedIn(this ILogger logger, Guid id);

    [LoggerMessage(LogLevel.Warning, "[CLIENT] Failed to fetch NAT type, retrying...")]
    public static partial void LogFailedToAcquireNatType(this ILogger logger);

    [LoggerMessage(LogLevel.Information, "[CLIENT] Starting server link holder...")]
    public static partial void LogStartingServerLinkHolder(this ILogger logger);

    [LoggerMessage(LogLevel.Information, "[CLIENT] Stopping server link holder...")]
    public static partial void LogStoppingServerLinkHolder(this ILogger logger);

    [LoggerMessage(LogLevel.Information, "[CLIENT] Getting client NAT type...")]
    public static partial void LogGettingClientNatType(this ILogger logger);

    [LoggerMessage(LogLevel.Information, "[CLIENT] Connecting to server...")]
    public static partial void LogConnectingToServer(this ILogger logger);

    [LoggerMessage(LogLevel.Error, "[CLIENT] Failed to connect to server at endpoint {endPoint}")]
    public static partial void LogFailedToConnectToServer(this ILogger logger, IPEndPoint endPoint);

    [LoggerMessage(LogLevel.Information, "[CLIENT] Sending signin message to server...")]
    public static partial void LogSendingSigninMessageToServer(this ILogger logger);

    [LoggerMessage(LogLevel.Information, "[CLIENT] Connected and signed to server at endpoint {endPoint}")]
    public static partial void LogConnectedAndSignedToServer(this ILogger logger, IPEndPoint endPoint);

    [LoggerMessage(LogLevel.Information, "[CLIENT] Disconnecting from server...")]
    public static partial void LogDisconnectingFromServer(this ILogger logger);

    [LoggerMessage(LogLevel.Information, "[CLIENT] Disconnected from server.")]
    public static partial void LogDisconnectedFromServer(this ILogger logger);

    [LoggerMessage(LogLevel.Debug, "[CLIENT] Heartbeat received from server.")]
    public static partial void LogHeartbeatReceivedFromServer(this ILogger logger);

    [LoggerMessage(LogLevel.Information, "[CLIENT] Shutdown message received from server.")]
    public static partial void LogShutdownMessageReceivedFromServer(this ILogger logger);

    [LoggerMessage(LogLevel.Information, "[CLIENT] Start sending heartbeat...")]
    public static partial void LogStartSendingHeartbeat(this ILogger logger);

    [LoggerMessage(LogLevel.Information, "[CLIENT] Stop sending heartbeat.")]
    public static partial void LogStopSendingHeartbeat(this ILogger logger);
}