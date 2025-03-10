using Hive.Codec.Shared;
using MemoryPack;

namespace ConnectX.Shared.Messages.Relay;

[MessageDefine]
[MemoryPackable]
public partial class RelayServerRegisteredMessage(Guid serverId)
{
    public Guid ServerId { get; } = serverId;
}