using ConnectX.Client.Messages;
using MemoryPack;

namespace ConnectX.Client.Route.Packet;

[MemoryPackable]
[MemoryPackUnion(0, typeof(P2PPacket))]
[MemoryPackUnion(1, typeof(P2PTransmitErrorPacket))]
[MemoryPackUnion(2, typeof(Ping))]
[MemoryPackUnion(3, typeof(Pong))]
public abstract partial class RouteLayerPacket
{
    public required Guid From { get; init; }
    public required Guid To { get; init; }
    public required byte Ttl { get; set; }
}