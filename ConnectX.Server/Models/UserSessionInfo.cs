using ConnectX.Shared.Messages.Group;
using Hive.Network.Abstractions.Session;
using System.Net;

namespace ConnectX.Server.Models;

public class UserSessionInfo(BasicUserInfo basicUserInfo, IPEndPoint? relayServerAddress)
{
    public bool JoinP2PNetwork { get; } = basicUserInfo.JoinP2PNetwork;
    public string DisplayName { get; } = basicUserInfo.DisplayName;
    public ISession Session { get; } = basicUserInfo.Session;
    public Guid UserId { get; } = basicUserInfo.UserId;
    public string? NetworkNodeId { get; set; }
    public IPAddress[]? NetworkAddresses { get; set; }
    public IPEndPoint? RelayServerAddress { get; set; } = relayServerAddress;

    public static implicit operator UserInfo(UserSessionInfo user)
    {
        return new UserInfo
        {
            DisplayName = user.DisplayName,
            JoinP2PNetwork = user.JoinP2PNetwork,
            UserId = user.UserId,
            NetworkIpAddresses = user.NetworkAddresses,
            NetworkNodeId = user.NetworkNodeId,
            RelayServerAddress = user.RelayServerAddress
        };
    }
}