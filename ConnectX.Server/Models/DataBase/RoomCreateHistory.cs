using System.ComponentModel.DataAnnotations;

namespace ConnectX.Server.Models.DataBase;

public class RoomCreateHistory
{
    [Key] public Guid Id { get; init; } = Guid.NewGuid();

    public required Guid CreatedBy { get; init; }

    public required DateTime CreatedTime { get; init; }

    public required Guid RoomId { get; init; }

    [MaxLength(64)]
    public required string RoomName { get; init; }

    [MaxLength(32)]
    public required string UserDisplayName { get; init; }

    [MaxLength(256)]
    public required string? RoomDescription { get; init; }

    [MaxLength(128)]
    public required string? RoomPassword { get; init; }

    public required int MaxUserCount { get; init; }
}