﻿using System.Net;
using System.Net.Sockets;
using System.Text;
using ConnectX.Client.Managers;
using ConnectX.Client.Models;
using ConnectX.Client.Proxy.Message;
using ConnectX.Client.Route;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ConnectX.Client.Proxy;

public class FakeServerMultiCaster : BackgroundService
{
    private const string Prefix = "ConnectX";
    
    private readonly PartnerManager _partnerManager;
    private readonly ProxyManager _proxyManager;
    private readonly RouterPacketDispatcher _packetDispatcher;
    private readonly ILogger _logger;

    public FakeServerMultiCaster(
        PartnerManager partnerManager,
        ProxyManager channelManager,
        RouterPacketDispatcher packetDispatcher,
        ILogger<FakeServerMultiCaster> logger)
    {
        _partnerManager = partnerManager;
        _proxyManager = channelManager;
        _packetDispatcher = packetDispatcher;
        _logger = logger;

        _packetDispatcher.OnReceive<McMulticastMessage>(OnReceiveMcMulticastMessage);
    }
    
    public event Action<string, int>? OnListenedLanServer;

    private void OnReceiveMcMulticastMessage(McMulticastMessage message, PacketContext context)
    {
        _logger.LogDebug(
            "[MC_MULTI_CASTER] Received multicast message from {SenderId}, remote real port is {Port}, name is {Name}",
            context.SenderId, message.Port, message.Name);
        
        var proxy = _proxyManager.GetOrCreateAcceptor(context.SenderId, message.Port);
        if (proxy == null)
        {
            _logger.LogError("Proxy creation failed, sender ID: {SenderId}", context.SenderId);
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
        _logger.LogDebug("Multicast message to client, mapping port is {Port}, name is {Name}", proxy.LocalMappingPort,
            message.Name);
    }

    private void ListenedLanServer(string serverName, ushort port)
    {
        _logger.LogDebug("Lan server {ServerName}:{Port} is listened", serverName, port);
        OnListenedLanServer?.Invoke(serverName, port);
        foreach (var (id, _) in _partnerManager.Partners)
        {
            _logger.LogTrace(
                "[MC_MULTI_CASTER] Send lan server {ServerName}:{Port} to {Partner}",
                serverName, port, id);
            
            // 对每一个用户组播
            _packetDispatcher.Send(id, new McMulticastMessage
            {
                Port = port,
                Name = $"[{Prefix}]{serverName}"
            });
        }
    }

    private static async Task<CancellationTokenSource> CreateFakeServerAsync(string name, int port)
    {
        var cancellationTokenSource = new CancellationTokenSource();
        var token = cancellationTokenSource.Token;
        await Task.Run(() =>
        {
            var multicastSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

            var multicastAddress = IPAddress.Parse("224.0.2.60");
            var multicastOption = new MulticastOption(IPAddress.Parse("224.0.2.60"), IPAddress.Any);
            multicastSocket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, multicastOption);

            var mess = $"[MOTD]{name}[/MOTD][AD]{port}[/AD]";
            var buf = Encoding.Default.GetBytes(mess);

            while (!token.IsCancellationRequested)
            {
                var endPoint = new IPEndPoint(multicastAddress, 4445);

                multicastSocket.SendTo(buf, endPoint);
                Thread.Sleep(1500);
            }
        }, token);

        return cancellationTokenSource;
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("[MC_MULTI_CASTER] Stopping FakeMcServerMultiCaster");
        
        return base.StopAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Run(async () =>
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var multicastSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

                var multicastAddress = IPAddress.Parse("224.0.2.60");
                var multicastOption = new MulticastOption(IPAddress.Parse("224.0.2.60"), IPAddress.Any);
                multicastSocket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership,
                    multicastOption);
                multicastSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

                var multicastIpe = new IPEndPoint(multicastAddress, 4445);

                multicastSocket.Bind(new IPEndPoint(IPAddress.Any, 4445));

                var buffer = new byte[256];
                EndPoint remoteEp = multicastIpe;
                var len = multicastSocket.ReceiveFrom(buffer, ref remoteEp);

                var message = Encoding.UTF8.GetString(buffer, 0, len);
                var serverName = message[6..message.IndexOf("[/MOTD]", StringComparison.Ordinal)];
                var portStart = message.IndexOf("[AD]", StringComparison.Ordinal) + 4;
                var portEnd = message.IndexOf("[/AD]", StringComparison.Ordinal);
                var port = ushort.Parse(message[portStart..portEnd]);

                if (!serverName.StartsWith($"[{Prefix}]")) //如果[ConnectX]开头，则是自己发出的，忽略
                    ListenedLanServer(serverName, port);

                multicastSocket.Close();
                multicastSocket.Dispose();
                try
                {
                    await Task.Delay(3000, stoppingToken);
                }
                catch (TaskCanceledException)
                {
                    //ignore
                }
            }
        }, stoppingToken);
        _logger.LogInformation("Start listening LAN multicast");
    }
}