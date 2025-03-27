using Hive.Codec.Shared;
using MemoryPack;

namespace ConnectX.Shared.Messages.Relay.Datagram;

[MessageDefine]
[MemoryPackable]
public readonly partial struct RelayDatagram(Guid from, Guid to, ReadOnlyMemory<byte> payload)
{
    public readonly Guid From = from;
    public readonly Guid To = to;
    public readonly ReadOnlyMemory<byte> Payload = payload;
}