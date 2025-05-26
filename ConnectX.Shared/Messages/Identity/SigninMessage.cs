using Hive.Codec.Shared;
using MemoryPack;

namespace ConnectX.Shared.Messages.Identity;

[MessageDefine]
[MemoryPackable]
public partial class SigninMessage
{
    public required string DisplayName { get; init; }

    public required bool JoinP2PNetwork { get; init; }

    public required int LinkProtocolMajor { get; init; }

    public required int LinkProtocolMinor { get; init; }
}