using Hive.Codec.Shared;
using MemoryPack;

namespace ConnectX.Shared.Messages.Relay.Datagram;

[MessageDefine]
[MemoryPackable]
public readonly partial struct UnwrappedRelayDatagram(Guid from, ReadOnlyMemory<byte> payload)
{
    public readonly Guid From = from;
    public readonly ReadOnlyMemory<byte> Payload = payload;
}