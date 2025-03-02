using System.Collections;
using System.Net;
using ConnectX.Client.Messages;
using ConnectX.Client.Models;
using ConnectX.Client.Route;
using ConnectX.Shared.Helpers;
using ConnectX.Shared.Interfaces;
using Hive.Both.General.Dispatchers;
using Hive.Codec.Abstractions;
using Hive.Network.Abstractions;
using Hive.Network.Abstractions.Session;
using Hive.Network.Shared;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ConnectX.Client.Transmission;

public class P2PConnection : ISender, ISession
{
    public const int Timeout = 5000;
    public const int BufferLength = 256;
    private readonly IPacketCodec _codec;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger _logger;
    private readonly RouterPacketDispatcher _routerPacketDispatcher;

    private readonly TransDatagram[] _sendBuffer = new TransDatagram[BufferLength];
    private readonly BitArray _sendBufferAckFlag = new(BufferLength);

    private readonly Guid _targetId;

    private int _ackPointer;
    private int _lastAckTime;
    private int _sendPointer;

    public P2PConnection(
        Guid targetId,
        IDispatcher dispatcher,
        RouterPacketDispatcher routerPacketDispatcher,
        IPacketCodec codec,
        IHostApplicationLifetime lifetime,
        ILogger<P2PConnection> logger)
    {
        Dispatcher = dispatcher;

        _targetId = targetId;
        _routerPacketDispatcher = routerPacketDispatcher;
        _codec = codec;
        _lifetime = lifetime;
        _logger = logger;

        Task.Run(StartResendCoroutineAsync, _lifetime.ApplicationStopping).Forget();

        _routerPacketDispatcher.OnReceive<TransDatagram>(OnTransDatagramReceived);
    }

    public bool IsConnected { get; private set; }
    public IDispatcher Dispatcher { get; }

    public void Send(ReadOnlyMemory<byte> payload)
    {
        SendDatagram(TransDatagram.CreateNormal(_sendPointer, payload));
    }

    public void SendData<T>(T data)
    {
        using var stream = RecycleMemoryStreamManagerHolder.Shared.GetStream();
        _codec.Encode(data, stream);

        stream.Seek(0, SeekOrigin.Begin);

        var mem = stream.GetBuffer();
        var segment = new ArraySegment<byte>(mem, 0, (int)stream.Length);

        Send(segment);
    }

    private void OnTransDatagramReceived(TransDatagram datagram, PacketContext context)
    {
        if (datagram.Flag == TransDatagram.FirstHandShakeFlag)
        {
            // 握手的回复
            _routerPacketDispatcher.Send(_targetId, TransDatagram.CreateHandShakeSecond(1));

            _logger.LogReceiveFirstShakeHandPacket(_targetId);

            IsConnected = true;
            return;
        }

        // 如果是TransDatagram，需要回复确认
        if ((datagram.Flag & DatagramFlag.SYN) != 0)
        {
            if (datagram.Payload != null)
            {
                var message = _codec.Decode(datagram.Payload.Value);

                if (message == null)
                {
                    _logger.LogDecodeMessageFailed(datagram.Payload.Value.Length, _targetId);

                    return;
                }

                Dispatcher.Dispatch(SessionPlaceHolder.Shared, message.GetType(), message);
            }

            _routerPacketDispatcher.Send(_targetId, TransDatagram.CreateAck(datagram.SynOrAck));
        }
        else if ((datagram.Flag & DatagramFlag.ACK) != 0)
        {
            //是ACK包，需要更新发送缓冲区的状态

            _sendBufferAckFlag[datagram.SynOrAck] = true;

            if (_ackPointer != datagram.SynOrAck) return;

            _lastAckTime = DateTime.Now.Millisecond;

            // 向后寻找第一个未收到ACK的包
            for (;
                 _sendBufferAckFlag[_ackPointer] && _ackPointer <= _sendPointer;
                 _ackPointer = (_ackPointer + 1) % BufferLength)
                _sendBufferAckFlag[_ackPointer] = false;
        }
    }

    private async Task StartResendCoroutineAsync()
    {
        while (_lifetime.ApplicationStopping.IsCancellationRequested == false)
        {
            await TaskHelper.WaitUntilAsync(NeedResend, _lifetime.ApplicationStopping);

            if (!_lifetime.ApplicationStopping.IsCancellationRequested) _logger.LogResendCoroutineStarted(_targetId);
        }

        return;

        bool NeedResend()
        {
            if (_ackPointer == _sendPointer) return false;
            var now = DateTime.Now.Millisecond;
            var time = now - _lastAckTime;
            return time > Timeout;
        }
    }

    public async Task<bool> ConnectAsync()
    {
        _logger.LogConnectingTo(_targetId);

        if (IsConnected) return true;

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(Timeout);

        // SYN
        var succeed = await _routerPacketDispatcher.SendAndListenOnceAsync<TransDatagram, TransDatagram>(
            _targetId,
            TransDatagram.CreateHandShakeFirst(0),
            IsSecondShakeHand,
            cts.Token);

        if (!succeed)
        {
            _logger.LogConnectFailed(_targetId);

            return false;
        }

        //ACK
        _routerPacketDispatcher.Send(_targetId, TransDatagram.CreateHandShakeThird(2));
        IsConnected = true;

        return true;

        static bool IsSecondShakeHand(TransDatagram t)
        {
            return t is { Flag: TransDatagram.SecondHandShakeFlag, SynOrAck: 1 };
        }
    }

    public void Disconnect()
    {
        IsConnected = false;
    }

    private void SendDatagram(TransDatagram datagram)
    {
        _sendBuffer[_sendPointer] = datagram;
        _sendBufferAckFlag[_sendPointer] = false;
        _sendPointer = (_sendPointer + 1) % BufferLength;

        _routerPacketDispatcher.Send(_targetId, datagram);
    }

    public SessionId Id => throw new NotImplementedException();
    public IPEndPoint LocalEndPoint => new (IPAddress.None, 0);
    public IPEndPoint RemoteEndPoint => new(IPAddress.None, 0);
    public long LastHeartBeatTime => throw new NotImplementedException();

    public event SessionReceivedHandler? OnMessageReceived;

    public Task StartAsync(CancellationToken token) => throw new NotImplementedException();

    public ValueTask SendAsync(MemoryStream ms, CancellationToken token = default)
    {
        var mem = ms.GetBuffer();
        var segment = new ArraySegment<byte>(mem, 0, (int)ms.Length);

        Send(segment);

        return ValueTask.CompletedTask;
    }

    public ValueTask<bool> TrySendAsync(MemoryStream ms, CancellationToken token = default)
    {
        var mem = ms.GetBuffer();
        var segment = new ArraySegment<byte>(mem, 0, (int)ms.Length);

        Send(segment);

        return ValueTask.FromResult(true);
    }

    public void Close() => throw new NotImplementedException();
}

internal static partial class P2PConnectionLoggers
{
    [LoggerMessage(LogLevel.Trace,
        "[P2P_CONNECTION] Receive first shakehand packet, send second shakehand packet. (TargetId: {Id})")]
    public static partial void LogReceiveFirstShakeHandPacket(this ILogger logger, Guid id);

    [LoggerMessage(LogLevel.Error,
        "[P2P_CONNECTION] Decode message with payload length [{Length}] failed. (TargetId: {Id})")]
    public static partial void LogDecodeMessageFailed(this ILogger logger, long length, Guid id);

    [LoggerMessage(LogLevel.Debug, "[P2P_CONNECTION] Resend coroutine started. (TargetId: {Id})")]
    public static partial void LogResendCoroutineStarted(this ILogger logger, Guid id);

    [LoggerMessage(LogLevel.Information, "[P2P_CONNECTION] Connecting to {TargetId}")]
    public static partial void LogConnectingTo(this ILogger logger, Guid targetId);

    [LoggerMessage(LogLevel.Error, "[P2P_CONNECTION] Connect failed, no SYNACK response. (TargetId: {Id})")]
    public static partial void LogConnectFailed(this ILogger logger, Guid id);
}