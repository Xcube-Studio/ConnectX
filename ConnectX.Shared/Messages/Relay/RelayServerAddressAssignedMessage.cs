using Hive.Codec.Shared;
using MemoryPack;
using System.Net;

namespace ConnectX.Shared.Messages.Relay;

[MessageDefine]
[MemoryPackable]
public partial class RelayServerAddressAssignedMessage(Guid userId, IPEndPoint serverAddress)
{
    public Guid UserId { get; init; } = userId;

    [MemoryPackAllowSerialize] public IPEndPoint ServerAddress { get; init; } = serverAddress;
}