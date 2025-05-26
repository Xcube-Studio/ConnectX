using System.Net;

namespace ConnectX.Relay.Interfaces;

public interface IServerSettingProvider
{
    IPAddress ServerAddress { get; }
    ushort ServerPort { get; }

    IPAddress RelayServerAddress { get; }
    ushort RelayServerPort { get; }

    IPAddress? PublicListenAddress { get; }
    ushort PublicListenPort { get; }

    bool JoinP2PNetwork { get; }
    Guid ServerId { get; }

    IPEndPoint EndPoint { get; }
    IPEndPoint RelayEndPoint { get; }

    int MaxReferenceConnectionCount { get; }

    // the value should be between 0-100
    uint ServerPriority { get; }
}