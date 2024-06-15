using ConnectX.Shared.Interfaces;
using Hive.Codec.Shared;
using MemoryPack;

namespace ConnectX.Shared.Messages.Group;

[MessageDefine]
[MemoryPackable]
public partial class LeaveGroup : IRequireAssignedUserId
{
    public required Guid GroupId { get; init; }
    public required Guid UserId { get; init; }
}