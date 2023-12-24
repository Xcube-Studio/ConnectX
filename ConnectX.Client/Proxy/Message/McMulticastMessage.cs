using Hive.Codec.Shared;
using MemoryPack;

namespace ConnectX.Client.Proxy.Message;

[MessageDefine]
[MemoryPackable]
public partial class McMulticastMessage
{
    public required ushort Port { get; init; }
    public required string Name { get; init; }
}