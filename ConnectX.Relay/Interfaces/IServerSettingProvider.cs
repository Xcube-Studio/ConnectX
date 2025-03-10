using System.Net;

namespace ConnectX.Relay.Interfaces;

public interface IServerSettingProvider
{
    IPAddress ServerAddress { get; }
    ushort ServerPort { get; }

    IPAddress RelayServerAddress { get; }
    ushort RelayServerPort { get; }

    bool JoinP2PNetwork { get; }
    Guid ServerId { get; }

    IPEndPoint EndPoint { get; }
    IPEndPoint RelayEndPoint { get; }
}