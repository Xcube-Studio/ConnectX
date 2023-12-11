using Hive.Codec.Shared;
using MemoryPack;

namespace ConnectX.Shared.Messages.P2P;

[MessageDefine]
[MemoryPackable]
public partial class P2PInterconnect(Guid[] possibleUsers)
{
    public Guid[] PossibleUsers { get; init; } = possibleUsers;
}