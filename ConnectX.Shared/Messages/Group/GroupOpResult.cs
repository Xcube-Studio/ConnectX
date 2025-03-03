using Hive.Codec.Shared;
using MemoryPack;

namespace ConnectX.Shared.Messages.Group;

[MessageDefine]
[MemoryPackable]
public partial class GroupOpResult(GroupCreationStatus status, string? errorMessage = null)
{
    public string? ErrorMessage { get; init; } = errorMessage;
    public Guid RoomId { get; init; }
    public GroupCreationStatus Status { get; init; } = status;
}