using ConnectX.Shared.Interfaces;
using Hive.Network.Abstractions.Session;

namespace ConnectX.Server.Models;

public class BasicUserInfo : IRequireAssignedUserId
{
    public required bool JoinP2PNetwork { get; init; }
    public required string DisplayName { get; set; }
    public required ISession Session { get; init; }
    public required Guid UserId { get; init; }
}