using System.Net;
using Hive.Codec.Shared;
using MemoryPack;

namespace ConnectX.Shared.Messages.P2P;

[MessageDefine]
[MemoryPackable]
public partial class P2PConNotification
{
    public required Guid PartnerIds { get; init; }

    [MemoryPackAllowSerialize] public required IPEndPoint PartnerIp { get; init; }

    public bool UseUdp { get; init; }
    public bool UseUpnpIfAvailable { get; init; } = true;

    public required int Bargain { get; init; }
}