using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using ConnectX.Client.Interfaces;
using ConnectX.Shared.Helpers;
using ConnectX.Shared.Messages;
using ConnectX.Shared.Messages.Relay;
using ConnectX.Shared.Models;
using Hive.Both.General.Dispatchers;
using Hive.Codec.Abstractions;
using Hive.Network.Abstractions.Session;
using Hive.Network.Tcp;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ConnectX.Client.Transmission.Connections;

public sealed class RelayConnection : ConnectionBase
{
    private ISession? _relayServerLink;
    private CancellationTokenSource? _cts;

    private readonly IPEndPoint _relayEndPoint;
    private readonly RelayPacketDispatcher _relayPacketDispatcher;
    private readonly IConnector<TcpSession> _tcpConnector;
    private readonly IRoomInfoManager _roomInfoManager;
    private readonly IServerLinkHolder _serverLinkHolder;

    private static readonly ConcurrentDictionary<IPEndPoint, ISession> RelayServerLinkPool = new();
    private static readonly ConcurrentDictionary<IPEndPoint, SemaphoreSlim> ConnectionLocks = new();
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

        dispatcher.AddHandler<TransDatagram>(OnTransDatagramReceived);
        dispatcher.AddHandler<HeartBeat>(OnHeartBeatReceived);
    }

    private void OnHeartBeatReceived(MessageContext<HeartBeat> obj)
    {
        Logger.LogHeartbeatReceivedFromServer();
    }

    private void ResetCts()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
    }

    private void OnTransDatagramReceived(MessageContext<TransDatagram> ctx)
    {
        if (ctx.Message.RelayFrom.HasValue &&
            ctx.Message.RelayFrom.Value != To)
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

        if (datagram.Flag == TransDatagram.FirstHandShakeFlag)
        {
            // 握手的回复
            var handshake = TransDatagram.CreateHandShakeSecond(1, _serverLinkHolder.UserId, To);
            Dispatcher.SendAsync(_relayServerLink, handshake).Forget();

            Logger.LogReceiveFirstShakeHandPacket(Source, To);

            IsConnected = true;
            return;
        }

        // 如果是TransDatagram，需要回复确认
        if ((datagram.Flag & DatagramFlag.SYN) != 0)
        {
            if (datagram.Payload != null)
            {
                var sequence = new ReadOnlySequence<byte>(datagram.Payload.Value);
                var message = Codec.Decode(sequence);

                if (message == null)
                {
                    Logger.LogDecodeMessageFailed(Source, datagram.Payload.Value.Length, To);

                    return;
                }

                _relayPacketDispatcher.DispatchPacket(datagram);
                Dispatcher.Dispatch(_relayServerLink, message.GetType(), message);
            }

            var ack = TransDatagram.CreateAck(datagram.SynOrAck, _serverLinkHolder.UserId, To);

            Dispatcher.SendAsync(_relayServerLink, ack).Forget();
        }
        else if ((datagram.Flag & DatagramFlag.ACK) != 0)
        {
            //是ACK包，需要更新发送缓冲区的状态

            SendBufferAckFlag[datagram.SynOrAck] = true;

            if (AckPointer != datagram.SynOrAck) return;

            LastAckTime = DateTime.Now.Millisecond;

            // 向后寻找第一个未收到ACK的包
            for (;
                 SendBufferAckFlag[AckPointer] && AckPointer <= SendPointer;
                 AckPointer = (AckPointer + 1) % BufferLength)
                SendBufferAckFlag[AckPointer] = false;
        }
    }

    public override void Send(ReadOnlyMemory<byte> payload)
    {
        SendDatagram(TransDatagram.CreateNormal(SendPointer, payload, _serverLinkHolder.UserId, To));
    }

    public override async Task<bool> ConnectAsync()
    {
        ResetCts();

        if (_roomInfoManager.CurrentGroupInfo == null)
            return false;

        Logger.LogConnectingToRelayServer(_relayEndPoint);

        var linkCreationReq = new CreateRelayLinkMessage
        {
            UserId = _serverLinkHolder.UserId,
            RoomId = _roomInfoManager.CurrentGroupInfo.RoomId
        };

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

            if (session == null)
            {
                Logger.LogConnectFailed(Source, To);
                Logger.LogFailedToConnectToRelayServer(_relayEndPoint);

                return false;
            }

            session.BindTo(Dispatcher);

            if (!isReusingLink)
                session.StartAsync(_cts!.Token).Forget();

            await Task.Delay(1000, cts.Token);

            await Dispatcher.SendAndListenOnce<CreateRelayLinkMessage, RelayLinkCreatedMessage>(session,
                linkCreationReq, cts.Token);

            ConnectionRefCount.AddOrUpdate(_relayEndPoint, _ => 1, (_, u) => u + 1);
            RelayServerLinkPool.AddOrUpdate(_relayEndPoint, _ => session, (_, oldSession) =>
            {
                oldSession.OnMessageReceived -= Dispatcher.Dispatch;
                oldSession.Close();

                return session;
            });

            _relayServerLink = session;

            SendHeartBeatAsync().Forget();

            if (isReusingLink)
                Logger.LogConnectedToRelayServerUsingPool(_relayEndPoint);
            else
                Logger.LogConnectedToRelayServer(_relayEndPoint);

            IsConnected = true;

            return true;
        }
        catch (TaskCanceledException)
        {
            ResetCts();
            IsConnected = false;

            return false;
        }
        catch (SocketException e)
        {
            ResetCts();
            IsConnected = false;

            Logger.LogFailedToConnectToRelayServerWithException(e, _relayEndPoint);
            return false;
        }
        finally
        {
            ArgumentNullException.ThrowIfNull(connectionLock);

            connectionLock.Release();
        }
    }

    private async Task SendHeartBeatAsync()
    {
        Logger.LogHeartbeatStarted();

        while (_cts is { IsCancellationRequested: false } && _relayServerLink != null)
        {
            await Dispatcher.SendAsync(_relayServerLink, new HeartBeat(), _cts.Token);
            await Task.Delay(TimeSpan.FromSeconds(10), _cts.Token);
        }

        Logger.LogHeartbeatStopped();
    }

    public override void Disconnect()
    {
        base.Disconnect();

        ResetCts();

        if (_relayServerLink == null) return;

        _relayServerLink.OnMessageReceived -= Dispatcher.Dispatch;

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

        _relayServerLink.Close();
        _relayServerLink = null;

        RelayServerLinkPool.TryRemove(_relayEndPoint, out _);
    }

    protected override void SendDatagram(TransDatagram datagram)
    {
        if (!IsConnected || _relayServerLink == null)
        {
            Logger.LogSendFailedBecauseLinkNotReadyYet(Source, To);
            return;
        }

        SendBufferAckFlag[SendPointer] = false;
        SendPointer = (SendPointer + 1) % BufferLength;

        Dispatcher.SendAsync(_relayServerLink, datagram).Forget();
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

    [LoggerMessage(LogLevel.Critical, "[RELAY_CONN] Failed to update ref count for link [{EndPoint}]")]
    public static partial void LogFailedToUpdateRefCountForLink(this ILogger logger, IPEndPoint EndPoint);
}