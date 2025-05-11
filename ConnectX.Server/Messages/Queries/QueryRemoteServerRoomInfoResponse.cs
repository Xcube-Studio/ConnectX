using Hive.Codec.Shared;
using MemoryPack;

namespace ConnectX.Server.Messages.Queries;

[MessageDefine]
[MemoryPackable]
public partial class QueryRemoteServerRoomInfoResponse(bool found)
{
    public bool Found { get; init; } = found;
}