using System.ComponentModel.DataAnnotations;

namespace ConnectX.Server.Models.DataBase;

public class RoomJoinHistory
{
    [Key] public Guid Id { get; init; } = Guid.NewGuid();

    public required Guid UserId { get; init; }

    public required Guid RoomId { get; init; }

    public required DateTime LogTime { get; init; }

    [MaxLength(64)]
    public required string RoomName { get; init; }

    [MaxLength(32)]
    public required string NetworkNodeId { get; init; }

    [MaxLength(512)]
    public required string UserPhysicalAddress { get; init; }
}