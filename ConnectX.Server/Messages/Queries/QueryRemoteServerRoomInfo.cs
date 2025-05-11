using ConnectX.Shared.Messages.Group;
using Hive.Codec.Shared;
using MemoryPack;

namespace ConnectX.Server.Messages.Queries;

[MessageDefine]
[MemoryPackable]
public partial class QueryRemoteServerRoomInfo
{
    public required JoinGroup JoinGroup { get; init; }
}