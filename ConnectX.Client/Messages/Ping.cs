using ConnectX.Client.Route.Packet;
using Hive.Codec.Shared;
using MemoryPack;

namespace ConnectX.Client.Messages;

[MessageDefine]
[MemoryPackable]
public sealed partial class Ping : RouteLayerPacket
{
    public required long SendTime { get; init; }
    public required uint SeqId { get; init; }
}