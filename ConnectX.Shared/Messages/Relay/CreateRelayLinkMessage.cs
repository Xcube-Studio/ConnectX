using Hive.Codec.Shared;
using MemoryPack;

namespace ConnectX.Shared.Messages.Relay;

[MessageDefine]
[MemoryPackable]
public partial class CreateRelayLinkMessage
{
    public Guid UserId { get; init; }
    public Guid RoomId { get; init; }
}