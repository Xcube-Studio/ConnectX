using Hive.Codec.Abstractions;
using Microsoft.Extensions.Logging;
using ConnectX.Shared.Messages;
using System.Buffers;

namespace ConnectX.Client.Transmission;

public sealed class RelayPacketDispatcher(
    IPacketCodec codec,
    ILogger<RelayPacketDispatcher> logger) : PacketDispatcherBase<RelayDatagram>(codec, logger)
{
    public void DispatchPacket(RelayDatagram packet) => OnReceiveTransDatagram(packet);

    protected override void OnReceiveTransDatagram(RelayDatagram packet)
    {
        var sequence = new ReadOnlySequence<byte>(packet.Payload);
        var message = Codec.Decode(sequence);
        var messageType = message!.GetType();

        Logger.LogReceived(messageType.Name, packet.From);

        Dispatch(message, messageType, packet.From);
    }
}