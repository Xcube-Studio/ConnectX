using Hive.Codec.Shared;
using MemoryPack;
using System.Net;

namespace ConnectX.Shared.Messages.Group;

[MessageDefine]
[MemoryPackable]
public partial class UpdateRoomMemberNetworkInfo
{
    public required Guid UserId { get; init; }

    public required Guid GroupId { get; init; }

    public required string NetworkNodeId { get; init; }

    public required IPAddress[] NetworkIpAddresses { get; init; }
}