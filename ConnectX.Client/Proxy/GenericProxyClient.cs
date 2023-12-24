using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace ConnectX.Client.Proxy;

public sealed class GenericProxyClient : GenericProxyBase
{
    public GenericProxyClient(
        TunnelIdentifier tunnelIdentifier,
        CancellationToken cancellationToken,
        ILogger<GenericProxyClient> logger)
        : base(tunnelIdentifier, cancellationToken, logger)
    {
    }

    private ushort LocalServerPort => TunnelIdentifier.LocalRealPort;
    private ushort RemoteClientPort => TunnelIdentifier.RemoteRealPort;

    protected override object GetProxyInfoForLog()
    {
        return new
        {
            Type = "Client",
            LocalMcPort = LocalServerPort
        };
    }

    protected override Socket CreateSocket()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

        socket.Connect(new IPEndPoint(IPAddress.Loopback,
            LocalServerPort));
        
        Logger.LogInformation(
            "[PROXY_CLIENT] Connected to MC. Mapping: {McPort}, ProxyInfo: {ProxyInfo}",
            LocalServerPort, GetProxyInfoForLog());

        InvokeRealServerConnected();
        
        return socket;
    }
}