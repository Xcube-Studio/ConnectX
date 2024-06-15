using ConnectX.Shared.Interfaces;
using Hive.Codec.Shared;
using MemoryPack;
using STUN.Enums;

namespace ConnectX.Shared.Messages.Group;

[MessageDefine]
[MemoryPackable]
public partial class UserInfo : IRequireAssignedUserId
{
    public required bool JoinP2PNetwork { get; init; }
    public required string DisplayName { get; init; }
    public required BindingTestResult BindingTestResult { get; init; }
    public required MappingBehavior MappingBehavior { get; init; }
    public required FilteringBehavior FilteringBehavior { get; init; }
    public required Guid UserId { get; init; }
}