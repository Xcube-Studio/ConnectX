﻿using ConnectX.Relay.Helpers;
using ConnectX.Relay.Interfaces;
using ConnectX.Shared;
using ConnectX.Shared.Helpers;
using ConnectX.Shared.Messages;
using ConnectX.Shared.Messages.Identity;
using ConnectX.Shared.Messages.Relay;
using Hive.Both.General.Dispatchers;
using Hive.Network.Abstractions.Session;
using Hive.Network.Tcp;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Net;

namespace ConnectX.Relay;

public class ServerLinkHolder : BackgroundService, IServerLinkHolder
{
    private readonly IDispatcher _dispatcher;
    private readonly IServerSettingProvider _settingProvider;
    private readonly IConnector<TcpSession> _tcpConnector;
    private readonly IHostApplicationLifetime _applicationLifetime;
    private readonly ILogger _logger;

    private DateTime _lastHeartBeatTime;

    public ServerLinkHolder(
        IDispatcher dispatcher,
        IServerSettingProvider settingProvider,
        IConnector<TcpSession> tcpConnector,
        IHostApplicationLifetime applicationLifetime,
        ILogger<ServerLinkHolder> logger)
    {
        _dispatcher = dispatcher;
        _settingProvider = settingProvider;
        _tcpConnector = tcpConnector;
        _applicationLifetime = applicationLifetime;
        _logger = logger;

        _dispatcher.AddHandler<HeartBeat>(OnHeartBeatReceived);
        _dispatcher.AddHandler<ShutdownMessage>(OnShutdownMessageReceived);
    }

    public ISession? ServerSession { get; private set; }
    public bool IsConnected { get; private set; }
    public bool IsSignedIn { get; private set; }

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

    private IPAddress? TryGetPublicListenAddress()
    {
        if (_settingProvider.PublicListenAddress != null)
            return _settingProvider.PublicListenAddress;

        var serverAddress = _settingProvider.RelayServerAddress.Equals(IPAddress.Any)
            ? AddressHelper.GetServerPublicAddress().FirstOrDefault()
            : _settingProvider.RelayServerAddress;

        if (serverAddress != null) return serverAddress;

        _logger.FailedToAcquirePublicAddress();

        return null;
    }

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        _logger.LogConnectingToServer();

        var endPoint = new IPEndPoint(_settingProvider.ServerAddress, _settingProvider.ServerPort);
        var session = await _tcpConnector.ConnectAsync(endPoint, cancellationToken);

        if (session == null)
        {
            _logger.LogFailedToConnectToServer(endPoint);
            _applicationLifetime.StopApplication();
            return;
        }

        session.BindTo(_dispatcher);
        session.StartAsync(cancellationToken).Forget();

        _logger.LogSendingSigninMessageToServer();

        await Task.Delay(1000, cancellationToken);

        var signin = new SigninMessage
        {
            JoinP2PNetwork = _settingProvider.JoinP2PNetwork,
            DisplayName = session.RemoteEndPoint?.ToString() ?? Guid.CreateVersion7().ToString("N"),
            LinkProtocolMajor = LinkProtocolConstants.ProtocolMajor,
            LinkProtocolMinor = LinkProtocolConstants.ProtocolMinor
        };

        var result = await _dispatcher.SendAndListenOnce<SigninMessage, SigninResult>(session, signin, cancellationToken);

        if (result == null)
        {
            _logger.LogWaitForSigninResultFailed(endPoint);
            return;
        }

        if (!result.Succeeded)
        {
            _logger.LogServerRefusedClientToLogin(endPoint, result);
            return;
        }

        IsSignedIn = result.Succeeded;

        _logger.LogConnectedAndSignedToServer(endPoint);

        var serverAddress = TryGetPublicListenAddress();
        var serverPort = _settingProvider.RelayServerPort == 0
            ? _settingProvider.PublicListenPort
            : _settingProvider.RelayServerPort;

        if (serverAddress == null) return;

        var serverEndPoint = new IPEndPoint(serverAddress, serverPort);

        _logger.LogServerPublicAddressAcquired(serverEndPoint);

        var res = await _dispatcher.SendAndListenOnce<RegisterRelayServerMessage, RelayServerRegisteredMessage>(
            session,
            new RegisterRelayServerMessage(
                _settingProvider.ServerId,
                serverEndPoint),
            cancellationToken);

        if (res == null)
        {
            _logger.LogFailedToRegisterRelayServer();
            return;
        }

        _logger.LogSuccessfullyRegisteredRelayServer();

        ServerSession = session;
        IsConnected = true;

        Hive.Common.Shared.Helpers.TaskHelper.FireAndForget(() => CheckServerLivenessAsync(cancellationToken));
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        if (!IsConnected || ServerSession == null) return;

        _logger.LogDisconnectingFromServer();

        await _dispatcher.SendAsync(ServerSession, new ShutdownMessage(), CancellationToken.None);
        ServerSession.Close();

        _logger.LogDisconnectedFromServer();

        IsSignedIn = false;
        IsConnected = false;
    }

    private void OnHeartBeatReceived(MessageContext<HeartBeat> ctx)
    {
        _lastHeartBeatTime = DateTime.UtcNow;
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

    private async Task CheckServerLivenessAsync(CancellationToken cancellationToken)
    {
        var endPoint = new IPEndPoint(_settingProvider.ServerAddress, _settingProvider.ServerPort);

        try
        {
            _logger.LogMainServerLivenessProbeStarted(endPoint);

            // Set the last for init
            _lastHeartBeatTime = DateTime.UtcNow;

            while (cancellationToken is { IsCancellationRequested: false } &&
                   IsConnected &&
                   ServerSession != null)
            {
                await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);

                var lastReceiveTimeSeconds = (DateTime.UtcNow - _lastHeartBeatTime).TotalSeconds;

                if (lastReceiveTimeSeconds <= 15)
                    continue;

                _logger.LogMainServerHeartbeatTimeout(endPoint, lastReceiveTimeSeconds);

                break;
            }
        }
        catch (TaskCanceledException)
        {
            // ignored
        }
        finally
        {
            IsConnected = false;
            ServerSession?.Close();
            ServerSession = null;

            _applicationLifetime.StopApplication();
        }
    }
}

internal static partial class ServerLinkHolderLoggers
{
    [LoggerMessage(LogLevel.Information, "[CLIENT] Successfully logged into server, assigned id: {id}")]
    public static partial void LogSuccessfullyLoggedIn(this ILogger logger, Guid id);

    [LoggerMessage(LogLevel.Information, "[CLIENT] Starting server link holder...")]
    public static partial void LogStartingServerLinkHolder(this ILogger logger);

    [LoggerMessage(LogLevel.Information, "[CLIENT] Stopping server link holder...")]
    public static partial void LogStoppingServerLinkHolder(this ILogger logger);

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

    [LoggerMessage(LogLevel.Critical, "[CLIENT] Failed to register relay server.")]
    public static partial void LogFailedToRegisterRelayServer(this ILogger logger);

    [LoggerMessage(LogLevel.Information, "[CLIENT] Successfully registered relay server.")]
    public static partial void LogSuccessfullyRegisteredRelayServer(this ILogger logger);

    [LoggerMessage(LogLevel.Error, "[CLIENT] Failed to acquire public address.")]
    public static partial void FailedToAcquirePublicAddress(this ILogger logger);

    [LoggerMessage(LogLevel.Information, "[CLIENT] Server public address acquired [{endPoint}]")]
    public static partial void LogServerPublicAddressAcquired(this ILogger logger, IPEndPoint endPoint);

    [LoggerMessage(LogLevel.Information, "[CLIENT] Server liveness probe started for [{relayEndPoint}]")]
    public static partial void LogMainServerLivenessProbeStarted(this ILogger logger, IPEndPoint relayEndPoint);

    [LoggerMessage(LogLevel.Warning, "[CLIENT] Server liveness probe stopped for [{relayEndPoint}]")]
    public static partial void LogMainServerLivenessProbeStopped(this ILogger logger, IPEndPoint relayEndPoint);

    [LoggerMessage(LogLevel.Critical, "[CLIENT] Link with server [{relayEndPoint}] is down, last heartbeat received [{seconds} seconds ago]")]
    public static partial void LogMainServerHeartbeatTimeout(this ILogger logger, IPEndPoint relayEndPoint, double seconds);

    [LoggerMessage(LogLevel.Information, "[CLIENT] Wait for signin result failed at endpoint {endPoint}")]
    public static partial void LogWaitForSigninResultFailed(this ILogger logger, IPEndPoint endPoint);

    [LoggerMessage(LogLevel.Information, "[CLIENT] Server refused client to login at endpoint {endPoint}, {signinResult}")]
    public static partial void LogServerRefusedClientToLogin(this ILogger logger, IPEndPoint endPoint, SigninResult signinResult);
}