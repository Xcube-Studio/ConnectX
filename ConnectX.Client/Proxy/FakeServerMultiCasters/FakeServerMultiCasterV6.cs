using ConnectX.Client.Interfaces;
using ConnectX.Client.Managers;
using ConnectX.Client.Messages.Proxy;
using ConnectX.Client.Route;
using ConnectX.Client.Transmission;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Sockets;

namespace ConnectX.Client.Proxy.FakeServerMultiCasters;

public sealed class FakeServerMultiCasterV6(
    PartnerManager partnerManager,
    ProxyManager channelManager,
    RouterPacketDispatcher packetDispatcher,
    RelayPacketDispatcher relayPacketDispatcher,
    IRoomInfoManager roomInfoManager,
    ILogger<FakeServerMultiCasterV6> logger)
    : FakeServerMultiCasterBase(partnerManager, channelManager, packetDispatcher, relayPacketDispatcher,
        roomInfoManager, logger)
{
    protected override IPAddress MulticastAddress => IPAddress.Parse("FF75:230::60");
    protected override int MulticastPort => 4445;
    protected override IPEndPoint MulticastPacketReceiveAddress => new(IPAddress.IPv6Any, 0);

    protected override McMulticastMessage CreateMcMulticastMessage(string serverName, ushort port)
    {
        return new McMulticastMessage
        {
            Port = port,
            Name = $"[{Prefix}]{serverName}",
            IsIpv6 = true
        };
    }

    protected override Socket? CreateMultiCastDiscoverySocket()
    {
        if (!Socket.OSSupportsIPv6)
        {
            Logger.LogMultiCasterDoesNotSupportIpv6();
            return null;
        }

        var multicastSocket = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp);

        multicastSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        multicastSocket.Bind(new IPEndPoint(IPAddress.IPv6Any, MulticastPort));

        var index = GetIpv6MulticastInterfaceIndex();
        var multicastOption = new IPv6MulticastOption(MulticastAddress, index);

        multicastSocket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.AddMembership, multicastOption);

        return multicastSocket;
    }
}

internal static partial class FakeServerMultiCasterV6Loggers
{
    [LoggerMessage(LogLevel.Warning, "Looks like system does not support IPv6 multicast. Stopping IPV6 multi-caster...")]
    public static partial void LogMultiCasterDoesNotSupportIpv6(this ILogger logger);
}