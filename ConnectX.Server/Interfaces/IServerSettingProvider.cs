using System.Net;

namespace ConnectX.Server.Interfaces;

public interface IServerSettingProvider
{
    string ServerName { get; }
    string ServerMotd { get; }

    IPEndPoint ServerPublicEndPoint { get; }

    IPAddress ListenAddress { get; }
    ushort ListenPort { get; }
    IPEndPoint ListenIpEndPoint => new(ListenAddress, ListenPort);

    Guid ServerId { get; }
}