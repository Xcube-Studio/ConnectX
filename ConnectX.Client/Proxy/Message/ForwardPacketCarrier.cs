using Hive.Codec.Shared;
using MemoryPack;

namespace ConnectX.Client.Proxy.Message;

[MessageDefine]
[MemoryPackable]
public partial class ForwardPacketCarrier
{
    public ushort TargetRealPort { get; set; }
    public ushort SelfRealPort { get; set; }
    public ReadOnlyMemory<byte> Data { get; set; }

    [MemoryPackIgnore] public int TryCount { get; set; } = 0;
    [MemoryPackIgnore] public int LastTryTime { get; set; } = 0;
}