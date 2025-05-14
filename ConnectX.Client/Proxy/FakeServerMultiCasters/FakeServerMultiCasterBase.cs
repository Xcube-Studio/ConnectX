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
    private const string Prefix = "ConnectX";

    private readonly RouterPacketDispatcher _packetDispatcher;
    private readonly PartnerManager _partnerManager;
    private readonly ProxyManager _proxyManager;
    private readonly IRoomInfoManager _roomInfoManager;
    private readonly ILogger _logger;

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
        _roomInfoManager = roomInfoManager;
        _logger = logger;

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
            foreach (var addr in ipProps.UnicastAddresses)
            {
                if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                {
                    var ip = addr.Address;
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

    private void OnReceiveMcMulticastMessage(McMulticastMessage message, PacketContext context)
    {
        _logger.LogReceivedMulticastMessage(context.SenderId, message.Port, message.Name);

        var proxy = _proxyManager.GetOrCreateAcceptor(context.SenderId, message.Port);
        if (proxy == null)
        {
            _logger.LogProxyCreationFailed(context.SenderId);
            return;
        }

        Socket? socket = null;

        if (OperatingSystem.IsWindows())
        {
            socket = new Socket(SocketType.Dgram, ProtocolType.Udp);

            var multicastOption = new MulticastOption(MulticastAddress, IPAddress.Any);
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, multicastOption);

            _logger.LogSocketSetupForWindows(MulticastAddress.ToString());
        }

        if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
        {
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

            var localIp = GetLocalIpAddress();

            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, localIp.GetAddressBytes());
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
            socket.Bind(new IPEndPoint(localIp, 0));

            _logger.LogSocketSetupForLinuxMacOs(localIp.ToString());
        }

        ArgumentNullException.ThrowIfNull(socket);

        using var multicastSocket = socket;

        var mess = $"[MOTD]{message.Name}[/MOTD][AD]{proxy.LocalMappingPort}[/AD]";
        var buf = Encoding.Default.GetBytes(mess).AsSpan();
        var sentLen = 0;

        while (sentLen < buf.Length)
            sentLen += multicastSocket.SendTo(buf[sentLen..], MulticastIpe);

        _logger.LogMulticastMessageToClient(proxy.LocalMappingPort, message.Name);
    }


    private void ListenedLanServer(string serverName, ushort port)
    {
        _logger.LogLanServerIsListened(serverName, port);

        OnListenedLanServer?.Invoke(serverName, port);

        foreach (var (id, partner) in _partnerManager.Partners)
        {
            var message = new McMulticastMessage
            {
                Port = port,
                Name = $"[{Prefix}]{serverName}"
            };

            if (partner.Connection is RelayConnection { IsConnected: true })
            {
                // Partner is connected through relay, send multicast using the relay connection
                partner.Connection.SendData(message);

                _logger.LogSendLanServerToPartner(serverName, port, id);

                continue;
            }

            // 对每一个用户组播
            _packetDispatcher.Send(id, message);

            _logger.LogSendLanServerToPartner(serverName, port, id);
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogStoppingFakeMcServerMultiCaster();

        return base.StopAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogStartListeningLanMulticast();

        var buffer = new byte[256];

        while (!stoppingToken.IsCancellationRequested)
        {
            if (_roomInfoManager.CurrentGroupInfo == null)
            {
                await Task.Delay(3000, stoppingToken);
                continue;
            }

            using var multicastSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

            // Allow multiple sockets to bind to same address (important for Linux/macOS)
            multicastSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

            // Bind to ANY address but the multicast port
            multicastSocket.Bind(new IPEndPoint(IPAddress.Any, MulticastPort));

            // Join multicast group
            var multicastOption = new MulticastOption(MulticastAddress, IPAddress.Any);
            multicastSocket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, multicastOption);

            try
            {
                var receiveFromResult = await multicastSocket.ReceiveFromAsync(buffer, SocketFlags.None,
                    new IPEndPoint(IPAddress.Any, 0), stoppingToken);
                var message = Encoding.UTF8.GetString(buffer, 0, receiveFromResult.ReceivedBytes);

                var serverName = message["[MOTD]".Length..message.IndexOf("[/MOTD]", StringComparison.Ordinal)];
                var portStart = message.IndexOf("[AD]", StringComparison.Ordinal) + 4;
                var portEnd = message.IndexOf("[/AD]", StringComparison.Ordinal);
                var port = ushort.Parse(message[portStart..portEnd]);

                if (!serverName.StartsWith($"[{Prefix}]"))
                {
                    ListenedLanServer(serverName, port);
                }
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.Interrupted)
            {
                // Expected during shutdown
            }
            catch (Exception ex)
            {
                _logger.LogFailedToReceiveMulticastMessage(ex);
            }

            await Task.Delay(3000, stoppingToken);
        }
    }
}

internal static partial class FakeServerMultiCasterLoggers
{
    [LoggerMessage(LogLevel.Debug,
        "[MC_MULTI_CASTER] Received multicast message from {SenderId}, remote real port is {Port}, name is {Name}")]
    public static partial void
        LogReceivedMulticastMessage(this ILogger logger, Guid senderId, ushort port, string name);

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

    [LoggerMessage(LogLevel.Debug, "Socket setup for Windows, multicast address is {MulticastAddress}")]
    public static partial void LogSocketSetupForWindows(this ILogger logger, string multicastAddress);

    [LoggerMessage(LogLevel.Debug, "Socket setup for Linux/MacOS, local IP is {LocalIp}")]
    public static partial void LogSocketSetupForLinuxMacOs(this ILogger logger, string localIp);
}