using ConnectX.Shared.Models;
using Hive.Codec.Shared;
using MemoryPack;

namespace ConnectX.Shared.Messages.Relay;

[MessageDefine]
[MemoryPackable]
public partial class UpdateRelayUserRoomMappingMessage
{
    public Guid UserId { get; init; }
    public Guid RoomId { get; init; }
    public GroupUserStates State { get; init; }
}