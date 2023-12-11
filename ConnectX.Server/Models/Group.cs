using ConnectX.Shared.Helpers;
using ConnectX.Shared.Messages.Group;

namespace ConnectX.Server.Models;

public class Group(
    string roomName,
    string? roomPassword,
    List<User> users)
{
    public Guid RoomId { get; } = Guid.NewGuid();
    public string RoomShortId { get; } = RandomHelper.GetRandomString();
    public bool IsPrivate { get; set; }
    public string RoomName { get; set; } = roomName;
    public string? RoomDescription { get; set; }
    public string? RoomPassword { get; } = roomPassword;
    public required int MaxUserCount { get; init; }
    public List<User> Users { get; } = users;

    public static implicit operator GroupInfo(Group group)
    {
        return new GroupInfo
        {
            IsPrivate = group.IsPrivate,
            CurrentUserCount = group.Users.Count,
            MaxUserCount = group.MaxUserCount,
            RoomDescription = group.RoomDescription,
            RoomId = group.RoomId,
            RoomName = group.RoomName,
            RoomShortId = group.RoomShortId,
            Users = group.Users.Select(x => (UserInfo) x).ToArray()
        };
    }
}