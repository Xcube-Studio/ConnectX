using System.Net;
using System.Net.Sockets;
using ConnectX.Client.Messages.Proxy;
using ConnectX.Shared.Helpers;
using ConnectX.Shared.Interfaces;
using Hive.Both.General.Dispatchers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ConnectX.Client.Proxy;

public abstract class GenericProxyManager : BackgroundService
{
    //使用一个二元组确定一个连接
    private readonly Dictionary<TunnelIdentifier, GenericProxyPair> _proxies = [];
    private readonly Dictionary<TunnelIdentifier, Socket> _acceptedSockets = [];
    private readonly Dictionary<ValueTuple<Guid, ushort>, GenericProxyAcceptor> _acceptors = [];

    private readonly IHostApplicationLifetime _lifetime;
    private readonly IServiceProvider _serviceProvider;
    protected readonly ILogger Logger;

    protected GenericProxyManager(
        IHostApplicationLifetime lifetime,
        IServiceProvider serviceProvider,
        ILogger logger)
    {
        _lifetime = lifetime;
        _serviceProvider = serviceProvider;

        Logger = logger;
    }

    public override void Dispose()
    {
        foreach (var (_, value) in _acceptors) value.Dispose();
        foreach (var proxyPair in _proxies) proxyPair.Value.Dispose();

        foreach (var acceptedSocket in _acceptedSockets)
            acceptedSocket.Value.Close();

        base.Dispose();
        GC.SuppressFinalize(this);
    }

    public void ReceivedProxyConnectReq(
        MessageContext<ProxyConnectReq> ctx,
        ISender sender)
    {
        var connectReq = ctx.Message;

        if (connectReq.IsResponse)
        {
            //客户端侧收到服务器侧的连接响应
            Logger.LogClientReceivedConnectResponse(connectReq);

            //收到回复，启动服务端代理
            if (_acceptors.ContainsKey((connectReq.ClientId, connectReq.ServerRealPort)))
            {
                var key = new TunnelIdentifier(
                    connectReq.ClientId,
                    connectReq.ClientRealPort,
                    connectReq.ServerRealPort);

                if (_acceptedSockets.Remove(key, out var socket))
                    CreateServerProxy(
                        connectReq.ClientId,
                        connectReq.ServerRealPort,
                        connectReq.ClientRealPort,
                        socket,
                        ctx.Dispatcher,
                        sender);
                else
                    Logger.LogErrorCanNotFindSocket(key.PartnerId, key.LocalRealPort);
            }
            else
            {
                Logger.LogErrorCanNotFindAcceptor(connectReq.ClientId, connectReq.ServerRealPort);
            }
        }
        else
        {
            CreateClientProxy(
                connectReq.ClientId,
                connectReq.ClientRealPort,
                connectReq.ServerRealPort,
                ctx.Dispatcher,
                sender);

            var mcConnectReq = new ProxyConnectReq
            {
                IsResponse = true,
                ClientId = connectReq.ClientId,
                ClientRealPort = connectReq.ClientRealPort,
                ServerRealPort = connectReq.ServerRealPort
            };

            sender.SendData(mcConnectReq);

            Logger.LogDebugClientSentConnectResponse(mcConnectReq);
        }
    }

    public virtual GenericProxyPair CreateClientProxy(
        Guid partnerId,
        ushort clientRealPort,
        ushort serverRealMcPort,
        IDispatcher dispatcher,
        ISender sender)
    {
        // 在真实服务端一侧，创建一个客户端代理，serverRealMcPort是localRealPort, clientRealPort是remoteRealPort
        var key = new TunnelIdentifier(partnerId, serverRealMcPort, clientRealPort);

        if (_proxies.Remove(key, out var prevProxy)) prevProxy.Dispose();

        var proxy = ActivatorUtilities.CreateInstance<GenericProxyClient>(
            _serviceProvider,
            key,
            _lifetime.ApplicationStopping);
        proxy.OnRealServerDisconnected += OnProxyDisconnected;

        var pair = new GenericProxyPair(
            partnerId,
            proxy,
            serverRealMcPort,
            clientRealPort,
            dispatcher,
            sender);

        _proxies.Add(key, pair);

        Logger.LogCreateProxy(key);

        proxy.Start(); //对于客户端，直接启动

        return pair;
    }

    public GenericProxyServer? CreateServerProxy(
        Guid partnerId,
        ushort serverRealPort,
        ushort clientRealPort,
        Socket socket,
        IDispatcher dispatcher,
        ISender sender)
    {
        var key = new TunnelIdentifier(partnerId, clientRealPort, serverRealPort);

        if (_proxies.Remove(key, out var prevProxy))
        {
            Logger.LogErrorProxyPairWithSameKey(partnerId, clientRealPort, serverRealPort);

            prevProxy.Dispose();
        }

        var proxy = ActivatorUtilities.CreateInstance<GenericProxyServer>(
            _serviceProvider,
            socket,
            key,
            _lifetime.ApplicationStopping);
        proxy.OnRealServerDisconnected += OnProxyDisconnected;

        var pair = new GenericProxyPair(
            partnerId,
            proxy,
            clientRealPort,
            serverRealPort,
            dispatcher,
            sender);

        _proxies.Add(key, pair);
        proxy.Start();

        return proxy;
    }

    private void OnProxyDisconnected(TunnelIdentifier id, GenericProxyBase obj)
    {
        obj.Dispose();
        _proxies.Remove(id);

        Logger.LogProxyDisconnected(id);
    }

    public bool HasAcceptor(Guid id, ushort port)
    {
        return _acceptors.ContainsKey((id, port));
    }

    public virtual GenericProxyAcceptor CreateAcceptor(
        Guid partnerId,
        ushort localMapPort,
        ushort remoteRealServerPort,
        ISender sender)
    {
        if (!NetworkHelper.PortIsAvailable(localMapPort))
            throw new InvalidOperationException($"Port {localMapPort} is not available");

        var key = (partnerId, remoteRealServerPort);

        if (_acceptors.Remove(key, out var oldAcceptor))
        {
            if (oldAcceptor.IsRunning)
            {
                _acceptors.Add(key, oldAcceptor);
                throw new InvalidOperationException($"There has been a acceptor with same key: {partnerId}-{remoteRealServerPort}");
            }

            oldAcceptor.Dispose();
        }

        var acceptor = ActivatorUtilities.CreateInstance<GenericProxyAcceptor>(
            _serviceProvider,
            partnerId,
            remoteRealServerPort,
            localMapPort,
            _lifetime.ApplicationStopping);

        _acceptors.Add((partnerId, remoteRealServerPort), acceptor);

        // 当有真客户端连接的时候，发送一个McConnectReq，当收到回复后启动Proxy（启动的逻辑在OnReceive里）
        acceptor.OnRealClientConnected += (_, socket) =>
        {
            if (socket.RemoteEndPoint is not IPEndPoint remoteEndPoint)
            {
                Logger.LogErrorCanNotGetRemoteEndPoint();
                return;
            }

            var id = new TunnelIdentifier(
                partnerId,
                (ushort)remoteEndPoint.Port,
                remoteRealServerPort);

            _acceptedSockets.Add(id, socket);

            sender.SendData(new ProxyConnectReq
            {
                IsResponse = false,
                ClientId = partnerId,
                ClientRealPort = (ushort)remoteEndPoint.Port,
                ServerRealPort = remoteRealServerPort
            });
        };

        Logger.LogCreateAcceptor(key);

        Hive.Common.Shared.Helpers.TaskHelper.FireAndForget(acceptor.StartAcceptAsync);

        return acceptor;
    }

    public GenericProxyAcceptor GetOrCreateAcceptor(
        Guid partnerId,
        Func<ushort> localMapPortGetter,
        ushort remoteRealServerPort,
        ISender sender)
    {
        var key = (partnerId, remoteRealServerPort);

        return _acceptors.TryGetValue(key, out var value) && value.IsRunning
            ? value
            : CreateAcceptor(partnerId, localMapPortGetter(), remoteRealServerPort, sender);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;

    public virtual void RemoveAllProxies()
    {
        foreach (var (_, value) in _acceptors)
        {
            try
            {
                value.Dispose();
            }
            catch (Exception)
            {
                // ignored
            }
        }
        _acceptors.Clear();

        foreach (var proxyPair in _proxies)
        {
            try
            {
                proxyPair.Value.Dispose();
            }
            catch (Exception)
            {
                // ignored
            }
        }
        _proxies.Clear();

        foreach (var acceptedSocket in _acceptedSockets)
        {
            try
            {
                acceptedSocket.Value.Close();
            }
            catch (Exception)
            {
                // ignored
            }
        }
        _acceptedSockets.Clear();

        Logger.LogGenProxiesCleared();
    }
}

internal static partial class GenericProxyManagerLoggers
{
    [LoggerMessage(LogLevel.Information, "[GEN_PROXY_MANAGER] Proxies cleared.")]
    public static partial void LogGenProxiesCleared(this ILogger logger);

    [LoggerMessage(LogLevel.Debug, "[GEN_PROXY_MANAGER] Client received connect response {Packet}")]
    public static partial void LogClientReceivedConnectResponse(this ILogger logger, ProxyConnectReq packet);

    [LoggerMessage(LogLevel.Error, "[GEN_PROXY_MANAGER] Can not find socket with id: {Id}, port: {Port}")]
    public static partial void LogErrorCanNotFindSocket(this ILogger logger, Guid id, ushort port);

    [LoggerMessage(LogLevel.Error, "[GEN_PROXY_MANAGER] Can not find acceptor with id: {Id}, port: {Port}")]
    public static partial void LogErrorCanNotFindAcceptor(this ILogger logger, Guid id, ushort port);

    [LoggerMessage(LogLevel.Debug, "[GEN_PROXY_MANAGER] Client sent connect response {Packet}")]
    public static partial void LogDebugClientSentConnectResponse(this ILogger logger, ProxyConnectReq packet);

    [LoggerMessage(LogLevel.Information, "[GEN_PROXY_MANAGER] Create client proxy {Key}")]
    public static partial void LogCreateProxy(this ILogger logger, TunnelIdentifier key);

    [LoggerMessage(LogLevel.Error,
        "[GEN_PROXY_MANAGER] There has been a proxy pair with same key: {ID}-{ClientRealPort}-{RemotePort}")]
    public static partial void LogErrorProxyPairWithSameKey(this ILogger logger, Guid id, ushort clientRealPort,
        ushort remotePort);

    [LoggerMessage(LogLevel.Information, "[GEN_PROXY_MANAGER] Proxy {Key} disconnected")]
    public static partial void LogProxyDisconnected(this ILogger logger, TunnelIdentifier key);

    [LoggerMessage(LogLevel.Error, "[GEN_PROXY_MANAGER] Can not get remote endpoint")]
    public static partial void LogErrorCanNotGetRemoteEndPoint(this ILogger logger);

    [LoggerMessage(LogLevel.Information, "[GEN_PROXY_MANAGER] Create acceptor {Key}")]
    public static partial void LogCreateAcceptor(this ILogger logger, (Guid, ushort) key);
}