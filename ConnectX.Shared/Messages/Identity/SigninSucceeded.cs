using ConnectX.Shared.Interfaces;
using Hive.Codec.Shared;
using MemoryPack;

namespace ConnectX.Shared.Messages.Identity;

[MessageDefine]
[MemoryPackable]
public partial class SigninSucceeded(Guid userId) : IRequireAssignedUserId
{
    public Guid UserId { get; } = userId;
}