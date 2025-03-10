using ConnectX.Client.Models;
using ConnectX.Client.Route;
using Hive.Both.General.Dispatchers;
using Hive.Codec.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Buffers;
using ConnectX.Shared.Messages;
using ConnectX.Shared.Models;

namespace ConnectX.Client.Transmission.Connections;

public sealed class P2PConnection : ConnectionBase
{
    private readonly RouterPacketDispatcher _routerPacketDispatcher;

    public P2PConnection(
        Guid targetId,
        IDispatcher dispatcher,
        RouterPacketDispatcher routerPacketDispatcher,
        IPacketCodec codec,
        IHostApplicationLifetime lifetime,
        ILogger<P2PConnection> logger) : base("P2P_CONN", targetId, dispatcher, codec, lifetime, logger)
    {
        _routerPacketDispatcher = routerPacketDispatcher;
        _routerPacketDispatcher.OnReceive<TransDatagram>(OnTransDatagramReceived);
    }

    public override async Task<bool> ConnectAsync()
    {
        Logger.LogConnectingTo(Source, To);

        if (IsConnected) return true;

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(Timeout);

        // SYN
        var succeed = await _routerPacketDispatcher.SendAndListenOnceAsync<TransDatagram, TransDatagram>(
            To,
            TransDatagram.CreateHandShakeFirst(0),
            IsSecondShakeHand,
            cts.Token);

        if (!succeed)
        {
            Logger.LogConnectFailed(Source, To);

            return false;
        }

        //ACK
        _routerPacketDispatcher.Send(To, TransDatagram.CreateHandShakeThird(2));
        IsConnected = true;

        return true;

        static bool IsSecondShakeHand(TransDatagram t)
        {
            return t is { Flag: TransDatagram.SecondHandShakeFlag, SynOrAck: 1 };
        }
    }

    private void OnTransDatagramReceived(TransDatagram datagram, PacketContext context)
    {
        if (datagram.Flag == TransDatagram.FirstHandShakeFlag)
        {
            // 握手的回复
            _routerPacketDispatcher.Send(To, TransDatagram.CreateHandShakeSecond(1));

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

                Dispatcher.Dispatch(SessionPlaceHolder.Shared, message.GetType(), message);
            }

            _routerPacketDispatcher.Send(To, TransDatagram.CreateAck(datagram.SynOrAck));
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

    protected override void SendDatagram(TransDatagram datagram)
    {
        SendBufferAckFlag[SendPointer] = false;
        SendPointer = (SendPointer + 1) % BufferLength;

        _routerPacketDispatcher.Send(To, datagram);
    }
}