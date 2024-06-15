using ConnectX.Shared.Interfaces;
using ConnectX.Shared.Messages.Group;
using Hive.Network.Abstractions.Session;
using STUN.Enums;

namespace ConnectX.Server.Models;

public record User : IRequireAssignedUserId
{
    public required bool JoinP2PNetwork { get; init; }
    public required string DisplayName { get; set; }
    public required ISession Session { get; init; }
    public required BindingTestResult BindingTestResult { get; init; }
    public required MappingBehavior MappingBehavior { get; init; }
    public required FilteringBehavior FilteringBehavior { get; init; }
    public required Guid UserId { get; init; }

    public static implicit operator UserInfo(User user)
    {
        return new UserInfo
        {
            BindingTestResult = user.BindingTestResult,
            DisplayName = user.DisplayName,
            FilteringBehavior = user.FilteringBehavior,
            JoinP2PNetwork = user.JoinP2PNetwork,
            MappingBehavior = user.MappingBehavior,
            UserId = user.UserId
        };
    }
}