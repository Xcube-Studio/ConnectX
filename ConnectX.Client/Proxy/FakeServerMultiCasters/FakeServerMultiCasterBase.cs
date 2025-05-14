using System.Collections.Frozen;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ConnectX.Client.Interfaces;
using ConnectX.Client.Managers;
using ConnectX.Client.Messages.Proxy;
using ConnectX.Client.Models;
using ConnectX.Client.Route;
using ConnectX.Client.Transmission;
using ConnectX.Client.Transmission.Connections;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ConnectX.Client.Proxy.FakeServerMultiCasters;

public abstract class FakeServerMultiCasterBase : BackgroundService
{
    protected const string Prefix = "ConnectX";

    private readonly RouterPacketDispatcher _packetDispatcher;
    private readonly PartnerManager _partnerManager;
    private readonly ProxyManager _proxyManager;

    protected readonly IRoomInfoManager RoomInfoManager;
    protected readonly ILogger Logger;

    protected FakeServerMultiCasterBase(
        PartnerManager partnerManager,
        ProxyManager channelManager,
        RouterPacketDispatcher packetDispatcher,
        RelayPacketDispatcher relayPacketDispatcher,
        IRoomInfoManager roomInfoManager,
        ILogger logger)
    {
        _partnerManager = partnerManager;
        _proxyManager = channelManager;
        _packetDispatcher = packetDispatcher;
        RoomInfoManager = roomInfoManager;
        Logger = logger;

        relayPacketDispatcher.OnReceive<McMulticastMessage>(OnReceiveMcMulticastMessage);
        _packetDispatcher.OnReceive<McMulticastMessage>(OnReceiveMcMulticastMessage);
    }

    public event Action<string, int>? OnListenedLanServer;

    protected abstract IPAddress MulticastAddress { get; }
    protected abstract int MulticastPort { get; }
    protected IPEndPoint MulticastIpe => new(MulticastAddress, MulticastPort);

    private static readonly FrozenSet<string> VirtualKeywords = FrozenSet.Create(
        "virtual", "vmware", "loopback",
        "pseudo", "tunneling", "tap",
        "container", "hyper-v", "bluetooth",
        "docker");

    private static IPAddress GetLocalIpAddress()
    {
        var candidates = new List<IPAddress>();

        var networkInterfaces = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(ni =>
                ni.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up &&
                ni.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback &&
                !VirtualKeywords.Contains(ni.Description.ToLowerInvariant()) &&
                !VirtualKeywords.Contains(ni.Name.ToLowerInvariant())
            );

        foreach (var ni in networkInterfaces)
        {
            var ipProps = ni.GetIPProperties();
            foreach (var address in ipProps.UnicastAddresses)
            {
                if (address.Address.AddressFamily == AddressFamily.InterNetwork)
                {
                    var ip = address.Address;
                    if (ip.ToString().StartsWith("192.168."))
                        return ip;

                    candidates.Add(ip);
                }
            }
        }

        if (candidates.Count > 0)
            return candidates[0];

        throw new Exception("No suitable IPv4 address found.");
    }

    protected static int GetIpv6MulticastInterfaceIndex()
    {
        var interfaces = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(ni => ni.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up);

        foreach (var ni in interfaces)
        {
            var props = ni.GetIPProperties();
            foreach (var address in props.UnicastAddresses)
            {
                if (address.Address is { AddressFamily: AddressFamily.InterNetworkV6, IsIPv6LinkLocal: false })
                {
                    return props.GetIPv6Properties()?.Index ?? 0;
                }
            }
        }

        return 0;
    }

    private void OnReceiveMcMulticastMessage(McMulticastMessage message, PacketContext context)
    {
        Logger.LogReceivedMulticastMessage(context.SenderId, message.Port, message.Name, message.IsIpv6);

        var proxy = _proxyManager.GetOrCreateAcceptor(context.SenderId, message.Port);
        if (proxy == null)
        {
            Logger.LogProxyCreationFailed(context.SenderId);
            return;
        }

        Socket? socket;

        if (message.IsIpv6)
        {
            socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp);
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.MulticastTimeToLive, 255);

            var index = GetIpv6MulticastInterfaceIndex();
            socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.MulticastInterface, index);
            socket.Bind(new IPEndPoint(IPAddress.IPv6Any, 0));

            Logger.LogSocketSetup("IPV6", $"(Interface Index: {index})");
        }
        else
        {
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);

            var localIp = GetLocalIpAddress();
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, localIp.GetAddressBytes());
            socket.Bind(new IPEndPoint(localIp, 0));

            Logger.LogSocketSetup("IPV4", localIp.ToString());
        }

        using var multicastSocket = socket;

        var mess = $"[MOTD]{message.Name}[/MOTD][AD]{proxy.LocalMappingPort}[/AD]";
        var buf = Encoding.Default.GetBytes(mess).AsSpan();
        var sentLen = 0;

        while (sentLen < buf.Length)
            sentLen += multicastSocket.SendTo(buf[sentLen..], MulticastIpe);

        Logger.LogMulticastMessageToClient(proxy.LocalMappingPort, message.Name);
    }


    protected void ListenedLanServer(string serverName, ushort port)
    {
        Logger.LogLanServerIsListened(serverName, port);

        OnListenedLanServer?.Invoke(serverName, port);

        foreach (var (id, partner) in _partnerManager.Partners)
        {
            var message = new McMulticastMessage
            {
                Port = port,
                Name = $"[{Prefix}]{serverName}",
                IsIpv6 = MulticastAddress.AddressFamily == AddressFamily.InterNetworkV6
            };

            if (partner.Connection is RelayConnection { IsConnected: true })
            {
                // Partner is connected through relay, send multicast using the relay connection
                partner.Connection.SendData(message);

                Logger.LogSendLanServerToPartner(serverName, port, id);

                continue;
            }

            // 对每一个用户组播
            _packetDispatcher.Send(id, message);

            Logger.LogSendLanServerToPartner(serverName, port, id);
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        Logger.LogStoppingFakeMcServerMultiCaster();

        return base.StopAsync(cancellationToken);
    }
}

internal static partial class FakeServerMultiCasterLoggers
{
    [LoggerMessage(LogLevel.Information,
        "[MC_MULTI_CASTER] Received multicast message from {SenderId}, remote real port is {Port}, name is {Name}, Is IPV6: {isIpv6}")]
    public static partial void
        LogReceivedMulticastMessage(this ILogger logger, Guid senderId, ushort port, string name, bool isIpv6);

    [LoggerMessage(LogLevel.Error, "Proxy creation failed, sender ID: {SenderId}")]
    public static partial void LogProxyCreationFailed(this ILogger logger, Guid senderId);

    [LoggerMessage(LogLevel.Debug, "Multicast message to client, mapping port is {Port}, name is {Name}")]
    public static partial void LogMulticastMessageToClient(this ILogger logger, ushort port, string name);

    [LoggerMessage(LogLevel.Debug, "Lan server {ServerName}:{Port} is listened")]
    public static partial void LogLanServerIsListened(this ILogger logger, string serverName, ushort port);

    [LoggerMessage(LogLevel.Trace, "[MC_MULTI_CASTER] Send lan server {ServerName}:{Port} to {Partner}")]
    public static partial void LogSendLanServerToPartner(this ILogger logger, string serverName, ushort port,
        Guid partner);

    [LoggerMessage(LogLevel.Information, "[MC_MULTI_CASTER] Stopping FakeMcServerMultiCaster")]
    public static partial void LogStoppingFakeMcServerMultiCaster(this ILogger logger);

    [LoggerMessage(LogLevel.Information, "Start listening LAN multicast")]
    public static partial void LogStartListeningLanMulticast(this ILogger logger);

    [LoggerMessage(LogLevel.Error, "Failed to receive multicast message.")]
    public static partial void LogFailedToReceiveMulticastMessage(this ILogger logger, Exception exception);

    [LoggerMessage(LogLevel.Debug, "Socket setup [{mode}], multicast address is {MulticastAddress}")]
    public static partial void LogSocketSetup(this ILogger logger, string mode, string multicastAddress);

    [LoggerMessage(LogLevel.Information, "Local game discovered, name [{name}], port [{port}]")]
    public static partial void LogLocalGameDiscovered(this ILogger logger, string name, ushort port);
}