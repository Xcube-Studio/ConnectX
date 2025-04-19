﻿using Hive.Codec.Shared;
using MemoryPack;

namespace ConnectX.Shared.Messages.Group;

[MessageDefine]
[MemoryPackable]
public partial class JoinGroup
{
    public Guid GroupId { get; init; }
    public string? RoomShortId { get; init; }
    public string? RoomPassword { get; init; }
    public required bool UseRelayServer { get; init; }
}