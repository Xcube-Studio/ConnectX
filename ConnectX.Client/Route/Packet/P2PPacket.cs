using Hive.Codec.Shared;
using MemoryPack;

namespace ConnectX.Client.Route.Packet;

[MessageDefine]
[MemoryPackable]
public sealed partial class P2PPacket : RouteLayerPacket
{
    public required ReadOnlyMemory<byte> Payload { get; init; }
}