using System.Net;

namespace ConnectX.Relay.Interfaces;

public interface IServerSettingProvider
{
    IPAddress ServerAddress { get; }
    ushort ServerPort { get; }
    bool JoinP2PNetwork { get; }
    Guid ServerId { get; }
    IPEndPoint EndPoint { get; }
}