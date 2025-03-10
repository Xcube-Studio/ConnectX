using System.Net;
using ConnectX.Relay.Interfaces;

namespace ConnectX.Relay;

public class DefaultServerSettingProvider : IServerSettingProvider
{
    public required IPAddress ServerAddress { get; init; }
    public ushort ServerPort { get; init; }
    public bool JoinP2PNetwork { get; init; }
    public Guid ServerId { get; init; }
    public IPEndPoint EndPoint { get; init; }
}