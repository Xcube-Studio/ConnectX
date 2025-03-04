using Hive.Codec.Shared;
using MemoryPack;

namespace ConnectX.Shared.Messages.Identity;

[MessageDefine]
[MemoryPackable]
public partial class UpdateDisplayNameMessage
{
    public required string DisplayName { get; init; }
}