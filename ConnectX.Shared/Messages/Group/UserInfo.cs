using System.Net;
using ConnectX.Shared.Interfaces;
using Hive.Codec.Shared;
using MemoryPack;

namespace ConnectX.Shared.Messages.Group;

[MessageDefine]
[MemoryPackable]
public partial class UserInfo : IRequireAssignedUserId, IEquatable<UserInfo>
{
    public required bool JoinP2PNetwork { get; init; }
    public required string DisplayName { get; init; }
    public required Guid UserId { get; init; }
    public required string NodeId { get; init; }
    public required IPAddress[] IpAddresses { get; init; }

    public bool Equals(UserInfo? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return JoinP2PNetwork == other.JoinP2PNetwork &&
               DisplayName == other.DisplayName &&
               UserId.Equals(other.UserId) &&
               NodeId.Equals(other.NodeId) &&
               IpAddresses.SequenceEqual(other.IpAddresses);
    }

    public override bool Equals(object? obj)
    {
        if (obj is null) return false;
        if (ReferenceEquals(this, obj)) return true;
        if (obj.GetType() != GetType()) return false;

        return Equals((UserInfo)obj);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(JoinP2PNetwork, DisplayName, UserId, NodeId, IpAddresses);
    }
}