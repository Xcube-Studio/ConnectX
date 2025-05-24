using System.Net;
using ConnectX.Relay.Interfaces;

namespace ConnectX.Relay;

public class DefaultServerSettingProvider : IServerSettingProvider
{
    public required IPAddress ServerAddress { get; init; }
    public ushort ServerPort { get; init; }

    public required IPAddress RelayServerAddress { get; init; }
    public ushort RelayServerPort { get; init; }

    public IPAddress? PublicListenAddress { get; init; }
    public ushort PublicListenPort { get; init; }

    public bool JoinP2PNetwork { get; init; }
    public Guid ServerId { get; init; }

    public required IPEndPoint EndPoint { get; init; }
    public required IPEndPoint RelayEndPoint { get; init; }

    public int MaxReferenceConnectionCount { get; init; }

    public uint ServerPriority { get; init; }
}