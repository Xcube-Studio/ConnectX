using ConnectX.Client.Interfaces;
using ConnectX.Client.Managers;
using ConnectX.Client.Route;
using ConnectX.Client.Transmission;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Sockets;
using System.Text;

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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Logger.LogStartListeningLanMulticast();
        var buffer = new byte[256];

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (RoomInfoManager.CurrentGroupInfo == null)
                {
                    await Task.Delay(3000, stoppingToken);
                    continue;
                }

                var multicastAddressFamily = MulticastAddress.AddressFamily;
                using var multicastSocket = new Socket(multicastAddressFamily, SocketType.Dgram, ProtocolType.Udp);

                multicastSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                multicastSocket.Bind(new IPEndPoint(IPAddress.IPv6Any, MulticastPort));

                var index = GetIpv6MulticastInterfaceIndex();
                var multicastOption = new IPv6MulticastOption(MulticastAddress, index);
                multicastSocket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.AddMembership,
                    multicastOption);

                try
                {
                    var receiveFromResult = await multicastSocket.ReceiveFromAsync(
                        buffer,
                        SocketFlags.None,
                        new IPEndPoint(IPAddress.IPv6Any, 0),
                        stoppingToken);

                    var message = Encoding.UTF8.GetString(buffer, 0, receiveFromResult.ReceivedBytes);
                    var serverName = message["[MOTD]".Length..message.IndexOf("[/MOTD]", StringComparison.Ordinal)];
                    var portStart = message.IndexOf("[AD]", StringComparison.Ordinal) + 4;
                    var portEnd = message.IndexOf("[/AD]", StringComparison.Ordinal);
                    var port = ushort.Parse(message[portStart..portEnd]);

                    if (!serverName.StartsWith($"[{Prefix}]"))
                    {
                        ListenedLanServer(serverName, port);
                        Logger.LogLocalGameDiscovered(serverName, port);
                    }
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.Interrupted)
                {
                    // Shutdown
                }
                catch (Exception ex)
                {
                    Logger.LogFailedToReceiveMulticastMessage(ex);
                }

                await Task.Delay(3000, stoppingToken);
            }
        }
        catch (SocketException ex)
        {
            Logger.LogLooksLikeSystemDoesNotSupportIpv6(ex);
        }
    }
}

internal static partial class FakeServerMultiCasterV6Loggers
{
    [LoggerMessage(LogLevel.Information, "Looks like system does not support IPv6 multicast. Stopping IPV6 multi-caster...")]
    public static partial void LogLooksLikeSystemDoesNotSupportIpv6(this ILogger logger, Exception exception);
}