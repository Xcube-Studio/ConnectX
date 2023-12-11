using System.Net;

namespace ConnectX.Server.Interfaces;

public interface IServerSettingProvider
{
    IPAddress ListenAddress { get; }
    ushort ListenPort { get; }
    IPEndPoint ListenIpEndPoint => new(ListenAddress, ListenPort);
}