using Hive.Codec.Shared;
using MemoryPack;

namespace ConnectX.Shared.Messages.Query.Response;

[MessageDefine]
[MemoryPackable]
public partial class PublicPortQueryResult
{
    public required ushort Port { get; init; }
}