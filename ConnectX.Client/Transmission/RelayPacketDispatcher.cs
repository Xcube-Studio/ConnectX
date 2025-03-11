using Hive.Codec.Abstractions;
using Microsoft.Extensions.Logging;
using ConnectX.Shared.Messages;
using System.Buffers;

namespace ConnectX.Client.Transmission;

public sealed class RelayPacketDispatcher(
    IPacketCodec codec,
    ILogger logger) : PacketDispatcherBase<TransDatagram>(codec, logger)
{
    public void DispatchPacket(TransDatagram packet) => OnReceiveTransDatagram(packet);

    protected override void OnReceiveTransDatagram(TransDatagram packet)
    {
        if (packet.Payload == null ||
            !packet.RelayFrom.HasValue)
            return;

        var sequence = new ReadOnlySequence<byte>(packet.Payload.Value);
        var message = Codec.Decode(sequence);
        var messageType = message!.GetType();

        Logger.LogReceived(messageType.Name, packet.RelayFrom.Value);

        Dispatch(message, messageType, packet.RelayFrom.Value);
    }
}