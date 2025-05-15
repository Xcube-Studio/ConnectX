using ConnectX.Client.Interfaces;
using ConnectX.Client.Managers;
using ConnectX.Client.Route;
using ConnectX.Client.Transmission;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Sockets;
using ConnectX.Client.Messages.Proxy;

namespace ConnectX.Client.Proxy.FakeServerMultiCasters;

public sealed class FakeServerMultiCasterV4(
    PartnerManager partnerManager,
    ProxyManager channelManager,
    RouterPacketDispatcher packetDispatcher,
    RelayPacketDispatcher relayPacketDispatcher,
    IRoomInfoManager roomInfoManager,
    ILogger<FakeServerMultiCasterV4> logger)
    : FakeServerMultiCasterBase(
        partnerManager, channelManager,
        packetDispatcher, relayPacketDispatcher,
        roomInfoManager, logger)
{
    protected override IPAddress MulticastAddress => IPAddress.Parse("224.0.2.60");
    protected override int MulticastPort => 4445;
    protected override IPEndPoint MulticastPacketReceiveAddress => new(IPAddress.Any, 0);

    protected override McMulticastMessage CreateMcMulticastMessage(string serverName, ushort port)
    {
        return new McMulticastMessage
        {
            Port = port,
            Name = $"[{Prefix}]{serverName}",
            IsIpv6 = false
        };
    }

    protected override Socket CreateMultiCastDiscoverySocket()
    {
        var multicastSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

        multicastSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        multicastSocket.Bind(new IPEndPoint(IPAddress.Any, MulticastPort));

        var multicastOption = new MulticastOption(MulticastAddress, IPAddress.Any);
        multicastSocket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, multicastOption);

        return multicastSocket;
    }
}