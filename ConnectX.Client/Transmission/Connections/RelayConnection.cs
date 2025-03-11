using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
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
    private readonly IConnector<TcpSession> _tcpConnector;
    private readonly IRoomInfoManager _roomInfoManager;
    private readonly IServerLinkHolder _serverLinkHolder;

    private static ConcurrentDictionary<IPEndPoint, ISession> RelayServerLinkPool { get; } = new();

    public RelayConnection(
        Guid targetId,
        IPEndPoint relayEndPoint,
        IDispatcher dispatcher,
        IRoomInfoManager roomInfoManager,
        IServerLinkHolder serverLinkHolder,
        IConnector<TcpSession> tcpConnector,
        IPacketCodec codec,
        IHostApplicationLifetime lifetime,
        ILogger<P2PConnection> logger) : base("RELAY_CONN", targetId, dispatcher, codec, lifetime, logger)
    {
        _relayEndPoint = relayEndPoint;
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

        ArgumentNullException.ThrowIfNull(_relayServerLink);

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
        if (_roomInfoManager.CurrentGroupInfo == null)
            return false;

        ResetCts();

        Logger.LogConnectingToRelayServer(_relayEndPoint);

        using var cts = new CancellationTokenSource();
        var linkCreationReq = new CreateRelayLinkMessage
        {
            UserId = _serverLinkHolder.UserId,
            RoomId = _roomInfoManager.CurrentGroupInfo.RoomId
        };

        // Make a random delay to avoid duplicate creation
        await Task.Delay(Random.Shared.Next(1000, 2000), cts.Token);

        // If we can find the link in the pool, we can reuse it.
        if (RelayServerLinkPool.TryGetValue(_relayEndPoint, out var link))
        {
            link.BindTo(Dispatcher);

            await Task.Delay(1000, cts.Token);

            Dispatcher.SendAsync(link, linkCreationReq, cts.Token).Forget();

            IsConnected = true;
            return true;
        }
        
        var session = await _tcpConnector.ConnectAsync(_relayEndPoint, cts.Token);

        if (session == null)
        {
            Logger.LogConnectFailed(Source, To);
            Logger.LogFailedToConnectToRelayServer(_relayEndPoint);

            return false;
        }

        session.BindTo(Dispatcher);
        session.StartAsync(_cts!.Token).Forget();

        await Task.Delay(1000, cts.Token);

        await Dispatcher.SendAndListenOnce<CreateRelayLinkMessage, RelayLinkCreatedMessage>(session, linkCreationReq, cts.Token);

        RelayServerLinkPool.AddOrUpdate(_relayEndPoint, _ => session, (_, oldSession) =>
        {
            oldSession.OnMessageReceived -= Dispatcher.Dispatch;
            oldSession.Close();

            return session;
        });

        _relayServerLink = session;

        SendHeartBeatAsync().Forget();

        Logger.LogConnectedToRelayServer(_relayEndPoint);

        IsConnected = true;

        return true;
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
        _relayServerLink.Close();
        _relayServerLink = null;

        RelayServerLinkPool.TryRemove(_relayEndPoint, out _);
    }

    protected override void SendDatagram(TransDatagram datagram)
    {
        ArgumentNullException.ThrowIfNull(_relayServerLink);

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

    [LoggerMessage(LogLevel.Information, "[RELAY_CONN] Heartbeat started")]
    public static partial void LogHeartbeatStarted(this ILogger logger);

    [LoggerMessage(LogLevel.Information, "[RELAY_CONN] Heartbeat stopped")]
    public static partial void LogHeartbeatStopped(this ILogger logger);

    [LoggerMessage(LogLevel.Information, "[RELAY_CONN] Connected to relay server [{relayEndPoint}]")]
    public static partial void LogConnectedToRelayServer(this ILogger logger, IPEndPoint relayEndPoint);
}