using Hive.Codec.Shared;
using MemoryPack;

namespace ConnectX.Client.Messages.Proxy;

[MessageDefine]
[MemoryPackable]
public partial class McMulticastMessage
{
    public required ushort Port { get; init; }
    public required string Name { get; init; }
    public required bool IsIpv6 { get; init; }
}