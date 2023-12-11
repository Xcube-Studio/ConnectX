using ConnectX.Shared.Models;
using Hive.Codec.Shared;
using MemoryPack;

namespace ConnectX.Shared.Messages.Group;

[MessageDefine]
[MemoryPackable]
public partial class GroupUserStateChanged(GroupUserStates state, UserInfo? userInfo)
{
    public GroupUserStates State { get; init; } = state;
    public UserInfo? UserInfo { get; init; } = userInfo;
}