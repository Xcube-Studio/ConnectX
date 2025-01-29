using Hive.Codec.Shared;
using MemoryPack;

namespace ConnectX.Shared.Messages.Group;

[MessageDefine]
[MemoryPackable]
public partial class RoomMemberInfoUpdated
{
    public required UserInfo UserInfo { get; init; }
}