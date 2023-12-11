using ConnectX.Shared.Helpers;

namespace ConnectX.Server.Models;

public class Group(string roomName, string? roomPassword)
{
    public Guid RoomId { get; } = Guid.NewGuid();
    public string RoomShortId { get; } = RandomHelper.GetRandomString();
    public bool IsPrivate { get; set; }
    public string RoomName { get; set; } = roomName;
    public string? RoomDescription { get; set; }
    public string? RoomPassword { get; } = roomPassword;
    public int MaxUserCount { get; set; }
    public List<User> Users { get; } = [];
}