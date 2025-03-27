using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using ConnectX.Client.Interfaces;
using ConnectX.Shared.Helpers;
using ConnectX.Shared.Messages;
using ConnectX.Shared.Messages.Relay;
using Hive.Both.General.Dispatchers;
using Hive.Codec.Abstractions;
using Hive.Network.Abstractions;
using Hive.Network.Abstractions.Session;
using Hive.Network.Tcp;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ConnectX.Client.Transmission.Connections;

public sealed class RelayConnection : ConnectionBase, IDatagramTransmit<RelayDatagram>
{
    private ISession? _relayServerLink;
    
    private DateTime _lastHeartBeatTime;

    private readonly IPEndPoint _relayEndPoint;
    private readonly RelayPacketDispatcher _relayPacketDispatcher;
    private readonly IConnector<TcpSession> _tcpConnector;
    private readonly IRoomInfoManager _roomInfoManager;
    private readonly IServerLinkHolder _serverLinkHolder;

    private CancellationToken _linkCt;

    private static readonly ConcurrentDictionary<IPEndPoint, ISession> RelayServerLinkPool = new();
    private static readonly ConcurrentDictionary<IPEndPoint, SemaphoreSlim> ConnectionLocks = new();
    private static readonly ConcurrentDictionary<IPEndPoint, CancellationTokenSource> ConnectionCts = new();
    private static readonly ConcurrentDictionary<IPEndPoint, uint> ConnectionRefCount = new();

    public RelayConnection(
        Guid targetId,
        IPEndPoint relayEndPoint,
        IDispatcher dispatcher,
        RelayPacketDispatcher relayPacketDispatcher,
        IRoomInfoManager roomInfoManager,
        IServerLinkHolder serverLinkHolder,
        IConnector<TcpSession> tcpConnector,
        IPacketCodec codec,
        IHostApplicationLifetime lifetime,
        ILogger<P2PConnection> logger) : base("RELAY_CONN", targetId, dispatcher, codec, lifetime, logger)
    {
        _relayEndPoint = relayEndPoint;
        _relayPacketDispatcher = relayPacketDispatcher;
        _tcpConnector = tcpConnector;
        _roomInfoManager = roomInfoManager;
        _serverLinkHolder = serverLinkHolder;

        dispatcher.AddHandler<RelayDatagram>(OnTransDatagramReceived);
        dispatcher.AddHandler<HeartBeat>(OnHeartBeatReceived);
    }

    private void OnHeartBeatReceived(MessageContext<HeartBeat> obj)
    {
        _lastHeartBeatTime = DateTime.UtcNow;
        Logger.LogHeartbeatReceivedFromServer();
    }

    private void OnTransDatagramReceived(MessageContext<RelayDatagram> ctx)
    {
        if (ctx.Message.From != To)
        {
            // we want to make sure we are processing the right packet
            return;
        }

        if (!IsConnected || _relayServerLink == null)
        {
            Logger.LogReceiveFailedBecauseLinkDown(Source, To);
            return;
        }

        var datagram = ctx.Message;
        var sequence = new ReadOnlySequence<byte>(datagram.Payload);
        var message = Codec.Decode(sequence);

        if (message == null)
        {
            Logger.LogDecodeMessageFailed(Source, datagram.Payload.Length, To);

            return;
        }

        _relayPacketDispatcher.DispatchPacket(datagram);
        Dispatcher.Dispatch(_relayServerLink, message.GetType(), message);
    }

    public override void Send(ReadOnlyMemory<byte> payload)
    {
        SendDatagram(new RelayDatagram(_serverLinkHolder.UserId, To, payload));
    }

    public override async Task<bool> ConnectAsync()
    {
        if (_roomInfoManager.CurrentGroupInfo == null)
            return false;

        Logger.LogConnectingToRelayServer(_relayEndPoint);

        SemaphoreSlim? connectionLock = null;

        try
        {
            using var cts = new CancellationTokenSource();

            // Make a random delay to avoid duplicate creation
            await Task.Delay(Random.Shared.Next(100, 1000), cts.Token);

            if (!ConnectionLocks.TryGetValue(_relayEndPoint, out connectionLock))
            {
                connectionLock = new SemaphoreSlim(1, 1);
                ConnectionLocks.TryAdd(_relayEndPoint, connectionLock);
            }

            // Wait for the connection lock
            Logger.LogWaitingForConnectionLock(_relayEndPoint);
            await connectionLock.WaitAsync(cts.Token);

            var isReusingLink = false;
            ISession? session;

            // If we can find the link in the pool, we can reuse it.
            if (RelayServerLinkPool.TryGetValue(_relayEndPoint, out var link))
            {
                isReusingLink = true;
                session = link;
            }
            else
            {
                session = await _tcpConnector.ConnectAsync(_relayEndPoint, cts.Token);
            }

            if (!ConnectionCts.TryGetValue(_relayEndPoint, out var linkCts))
            {
                var newCts = new CancellationTokenSource();
                
                linkCts = newCts;
                
                ConnectionCts.TryAdd(_relayEndPoint, linkCts);
            }
            
            _linkCt = linkCts.Token;

            if (session == null)
            {
                Logger.LogConnectFailed(Source, To);
                Logger.LogFailedToConnectToRelayServer(_relayEndPoint);

                return false;
            }

            session.BindTo(Dispatcher);

            ConnectionRefCount.AddOrUpdate(_relayEndPoint, _ => 1, (_, u) => u + 1);

            if (!isReusingLink)
            {
                session.StartAsync(_linkCt).Forget();

                await Task.Delay(1000, cts.Token);

                var linkCreationReq = new CreateRelayLinkMessage
                {
                    UserId = _serverLinkHolder.UserId,
                    RoomId = _roomInfoManager.CurrentGroupInfo.RoomId
                };

                await Dispatcher.SendAndListenOnce<CreateRelayLinkMessage, RelayLinkCreatedMessage>(session,
                    linkCreationReq, cts.Token);

                RelayServerLinkPool.AddOrUpdate(_relayEndPoint, _ => session, (_, oldSession) =>
                {
                    Logger.LogClosingOldLink(oldSession.Id);

                    oldSession.OnMessageReceived -= Dispatcher.Dispatch;
                    oldSession.Close();

                    return session;
                });

                Logger.LogConnectedToRelayServer(_relayEndPoint);
            }
            else
            {
                Logger.LogConnectedToRelayServerUsingPool(_relayEndPoint);
            }

            _relayServerLink = session;

            IsConnected = true;

            Hive.Common.Shared.Helpers.TaskHelper.FireAndForget(SendHeartBeatAsync);
            Hive.Common.Shared.Helpers.TaskHelper.FireAndForget(CheckServerLivenessAsync);

            return true;
        }
        catch (TaskCanceledException)
        {
            IsConnected = false;

            return false;
        }
        catch (SocketException e)
        {
            IsConnected = false;

            Logger.LogFailedToConnectToRelayServerWithException(e, _relayEndPoint);
            return false;
        }
        finally
        {
            ArgumentNullException.ThrowIfNull(connectionLock);

            if (connectionLock.CurrentCount == 0)
                connectionLock.Release();
        }
    }

    private async Task SendHeartBeatAsync()
    {
        Logger.LogHeartbeatStarted();

        while (_linkCt is { IsCancellationRequested: false } && _relayServerLink != null)
        {
            await Dispatcher.SendAsync(_relayServerLink, new HeartBeat(), _linkCt);
            await Task.Delay(TimeSpan.FromSeconds(10), _linkCt);
        }

        Logger.LogHeartbeatStopped();
    }

    private async Task CheckServerLivenessAsync()
    {
        try
        {
            Logger.LogServerLivenessProbeStarted(_relayEndPoint);

            // Set the last for init
            _lastHeartBeatTime = DateTime.UtcNow;

            while (_linkCt is { IsCancellationRequested: false } &&
                   IsConnected &&
                   _relayServerLink != null)
            {
                await Task.Delay(TimeSpan.FromSeconds(10), _linkCt);

                var lastReceiveTimeSeconds = (DateTime.UtcNow - _lastHeartBeatTime).TotalSeconds;

                if (lastReceiveTimeSeconds <= 15)
                    continue;

                Logger.LogServerHeartbeatTimeout(_relayEndPoint, lastReceiveTimeSeconds);

                break;
            }
        }
        catch (TaskCanceledException)
        {
            // ignored
        }
        finally
        {
            if (!ConnectionRefCount.TryGetValue(_relayEndPoint, out var count) ||
                count <= 1)
            {
                // we need to destroy the connection
                RelayServerLinkPool.TryRemove(_relayEndPoint, out _);

                if (_relayServerLink != null)
                {
                    _relayServerLink.OnMessageReceived -= Dispatcher.Dispatch;
                    _relayServerLink?.Close();
                }

                _relayServerLink = null;

                if (ConnectionCts.TryRemove(_relayEndPoint, out var cts))
                    await cts.CancelAsync();

                IsConnected = false;
            }

            Logger.LogServerLivenessProbeStopped(_relayEndPoint);
        }
    }

    public override void Disconnect()
    {
        base.Disconnect();

        if (!ConnectionRefCount.TryGetValue(_relayEndPoint, out var count)) return;
        if (!ConnectionRefCount.TryUpdate(_relayEndPoint, count - 1, count))
        {
            Logger.LogFailedToUpdateRefCountForLink(_relayEndPoint);
            return;
        }

        if (count > 1)
        {
            // There are still other connections using this link, so we don't close it.
            return;
        }

        RelayServerLinkPool.TryRemove(_relayEndPoint, out _);

        if (ConnectionCts.TryRemove(_relayEndPoint, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }

        if (_relayServerLink == null) return;

        _relayServerLink.OnMessageReceived -= Dispatcher.Dispatch;
        _relayServerLink.Close();
        _relayServerLink = null;

        Logger.LogRelayDisconnected(_relayEndPoint);
    }

    public void SendDatagram(RelayDatagram datagram)
    {
        if (!IsConnected || _relayServerLink == null)
        {
            Logger.LogSendFailedBecauseLinkNotReadyYet(Source, To);
            return;
        }

        Dispatcher.SendAsync(_relayServerLink, datagram, _linkCt).Forget();
    }
}

internal static partial class RelayConnectionLoggers
{
    [LoggerMessage(LogLevel.Information, "[RELAY_CONN] Connecting to relay server [{relayEndPoint}]")]
    public static partial void LogConnectingToRelayServer(this ILogger logger, IPEndPoint relayEndPoint);

    [LoggerMessage(LogLevel.Error, "[RELAY_CONN] Failed to connect to relay server [{relayEndPoint}]")]
    public static partial void LogFailedToConnectToRelayServer(this ILogger logger, IPEndPoint relayEndPoint);

    [LoggerMessage(LogLevel.Error, "[RELAY_CONN] Failed to connect to relay server [{relayEndPoint}]")]
    public static partial void LogFailedToConnectToRelayServerWithException(this ILogger logger, Exception ex, IPEndPoint relayEndPoint);

    [LoggerMessage(LogLevel.Information, "[RELAY_CONN] Heartbeat started")]
    public static partial void LogHeartbeatStarted(this ILogger logger);

    [LoggerMessage(LogLevel.Information, "[RELAY_CONN] Heartbeat stopped")]
    public static partial void LogHeartbeatStopped(this ILogger logger);

    [LoggerMessage(LogLevel.Information, "[RELAY_CONN] Connected to relay server [{relayEndPoint}]")]
    public static partial void LogConnectedToRelayServer(this ILogger logger, IPEndPoint relayEndPoint);

    [LoggerMessage(LogLevel.Error, "[RELAY_CONN] Send failed because link is not ready yet. (Source: {source}, Target: {target})")]
    public static partial void LogSendFailedBecauseLinkNotReadyYet(this ILogger logger, string source, Guid target);

    [LoggerMessage(LogLevel.Information, "[RELAY_CONN] Connected to relay server [{relayEndPoint}] using pool")]
    public static partial void LogConnectedToRelayServerUsingPool(this ILogger logger, IPEndPoint relayEndPoint);

    [LoggerMessage(LogLevel.Debug, "[RELAY_CONN] Waiting for connection lock [{relayEndPoint}]")]
    public static partial void LogWaitingForConnectionLock(this ILogger logger, IPEndPoint relayEndPoint);

    [LoggerMessage(LogLevel.Error, "[RELAY_CONN] Receive failed because link is down. (Source: {source}, Target: {target})")]
    public static partial void LogReceiveFailedBecauseLinkDown(this ILogger logger, string source, Guid target);

    [LoggerMessage(LogLevel.Critical, "[RELAY_CONN] Failed to update ref count for link [{endPoint}]")]
    public static partial void LogFailedToUpdateRefCountForLink(this ILogger logger, IPEndPoint endPoint);
    
    [LoggerMessage(LogLevel.Information, "[RELAY_CONN] Link with server [{relayEndPoint}] is down, last heartbeat received [{seconds} seconds ago]")]
    public static partial void LogServerHeartbeatTimeout(this ILogger logger, IPEndPoint relayEndPoint, double seconds);

    [LoggerMessage(LogLevel.Information, "[RELAY_CONN] Closing old link [{sessionId}]")]
    public static partial void LogClosingOldLink(this ILogger logger, SessionId sessionId);

    [LoggerMessage(LogLevel.Information, "[RELAY_CONN] Server liveness probe started for [{relayEndPoint}]")]
    public static partial void LogServerLivenessProbeStarted(this ILogger logger, IPEndPoint relayEndPoint);

    [LoggerMessage(LogLevel.Information, "[RELAY_CONN] Server liveness probe stopped for [{relayEndPoint}]")]
    public static partial void LogServerLivenessProbeStopped(this ILogger logger, IPEndPoint relayEndPoint);

    [LoggerMessage(LogLevel.Information, "[RELAY_CONN] Relay disconnected [{relayEndPoint}]")]
    public static partial void LogRelayDisconnected(this ILogger logger, IPEndPoint relayEndPoint);
}