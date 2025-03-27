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

namespace ConnectX.Client.Proxy;

public class FakeServerMultiCaster : BackgroundService
{
    private const string Prefix = "ConnectX";
    
    private readonly RouterPacketDispatcher _packetDispatcher;
    private readonly PartnerManager _partnerManager;
    private readonly ProxyManager _proxyManager;

    private readonly IRoomInfoManager _roomInfoManager;
    private readonly ILogger _logger;

    public FakeServerMultiCaster(
        PartnerManager partnerManager,
        ProxyManager channelManager,
        RouterPacketDispatcher packetDispatcher,
        RelayPacketDispatcher relayPacketDispatcher,
        IRoomInfoManager roomInfoManager,
        ILogger<FakeServerMultiCaster> logger)
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

    private void OnReceiveMcMulticastMessage(McMulticastMessage message, PacketContext context)
    {
        _logger.LogReceivedMulticastMessage(context.SenderId, message.Port, message.Name);

        var proxy = _proxyManager.GetOrCreateAcceptor(context.SenderId, message.Port);
        if (proxy == null)
        {
            _logger.LogProxyCreationFailed(context.SenderId);
            return;
        }

        var multicastSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        var multicastAddress = IPAddress.Parse("224.0.2.60");
        var multicastOption = new MulticastOption(IPAddress.Parse("224.0.2.60"), IPAddress.Any);

        multicastSocket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, multicastOption);

        //传递给客户端的是另一个端口，并非源端口。客户端连接此端口通过P2P.NET和源端口通信
        var mess = $"[MOTD]{message.Name}[/MOTD][AD]{proxy.LocalMappingPort}[/AD]";
        var buf = Encoding.Default.GetBytes(mess);

        var endPoint = new IPEndPoint(multicastAddress, 4445);

        multicastSocket.SendTo(buf, endPoint);
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

            if (partner.Connection is RelayConnection {IsConnected: true})
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
                // We are not in a room, so we don't need to listen to multicast
                await Task.Delay(3000, stoppingToken);
                continue;
            }

            using var multicastSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            var multicastOption = new MulticastOption(IPAddress.Parse("224.0.2.60"), IPAddress.Any);
            
            multicastSocket.SetSocketOption(
                SocketOptionLevel.IP,
                SocketOptionName.AddMembership,
                multicastOption);

            multicastSocket.SetSocketOption(
                SocketOptionLevel.Socket,
                SocketOptionName.ReuseAddress,
                true);

            var multicastAddress = IPAddress.Parse("224.0.2.60");
            var multicastIpe = new IPEndPoint(multicastAddress, 4445);

            multicastSocket.Bind(new IPEndPoint(IPAddress.Any, 4445));
            
            EndPoint remoteEp = multicastIpe;

            var len = (await multicastSocket.ReceiveFromAsync(buffer, remoteEp)).ReceivedBytes;
            var message = Encoding.UTF8.GetString(buffer, 0, len);
            var serverName = message[6..message.IndexOf("[/MOTD]", StringComparison.Ordinal)];
            var portStart = message.IndexOf("[AD]", StringComparison.Ordinal) + 4;
            var portEnd = message.IndexOf("[/AD]", StringComparison.Ordinal);
            var port = ushort.Parse(message[portStart..portEnd]);

            if (!serverName.StartsWith($"[{Prefix}]")) //如果[ConnectX]开头，则是自己发出的，忽略
                ListenedLanServer(serverName, port);

            multicastSocket.Close();
            multicastSocket.Dispose();

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
}