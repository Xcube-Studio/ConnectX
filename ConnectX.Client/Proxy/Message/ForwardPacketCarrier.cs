using Hive.Codec.Shared;
using MemoryPack;

namespace ConnectX.Client.Proxy.Message;

[MessageDefine]
[MemoryPackable]
public partial class ForwardPacketCarrier
{
    public ushort TargetRealPort { get; set; }
    public ushort SelfRealPort { get; set; }

    [BrotliFormatter<ReadOnlyMemory<byte>>]
    public ReadOnlyMemory<byte> Payload { get; set; }

    [MemoryPackIgnore] public int TryCount { get; set; }
    [MemoryPackIgnore] public int LastTryTime { get; set; }
}