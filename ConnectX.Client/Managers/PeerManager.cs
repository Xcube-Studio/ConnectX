using System.Collections;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using ConnectX.Client.Interfaces;
using ConnectX.Client.P2P;
using ConnectX.Shared.Helpers;
using ConnectX.Shared.Messages.Identity;
using ConnectX.Shared.Messages.P2P;
using ConnectX.Shared.Models;
using Hive.Both.General.Dispatchers;
using Hive.Network.Abstractions.Session;
using Hive.Network.Tcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Open.Nat;
using STUN.Enums;
using TaskHelper = Hive.Common.Shared.Helpers.TaskHelper;

namespace ConnectX.Client.Managers;

public class PeerManager : BackgroundService, IEnumerable<KeyValuePair<Guid, Peer>>
{
    private readonly UpnpManager _upnpManager;
    private readonly IDispatcher _dispatcher;
    private readonly IConnector<TcpSession> _tcpConnector;
    private readonly IServerLinkHolder _serverLinkHolder;
    private readonly IClientSettingProvider _clientSettingProvider;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger _logger;
    
    private readonly ConcurrentDictionary<Guid, int> _bargainsDic = [];
    private readonly ConcurrentDictionary<Guid, Peer> _connectedPeers = [];
    private readonly ConcurrentDictionary<Guid, bool> _allPeers = new();
    private readonly ConcurrentDictionary<Guid, (ISession, CancellationTokenSource)> _tmpLinkMakerDic = new ();
    private readonly ConcurrentDictionary<Guid, P2PConInitiator> _initiator = [];
    
    public PeerManager(
        UpnpManager upnpManager,
        IDispatcher dispatcher,
        IConnector<TcpSession> tcpConnector,
        IServerLinkHolder serverLinkHolder,
        IClientSettingProvider clientSettingProvider,
        IServiceProvider serviceProvider,
        ILogger<PeerManager> logger)
    {
        _upnpManager = upnpManager;
        _dispatcher = dispatcher;
        _tcpConnector = tcpConnector;
        _serverLinkHolder = serverLinkHolder;
        _clientSettingProvider = clientSettingProvider;
        _serviceProvider = serviceProvider;
        _logger = logger;

        _dispatcher.AddHandler<P2PInterconnect>(OnReceivedP2PInterconnect);
        _dispatcher.AddHandler<P2PConNotification>(OnReceivedP2PConNotification);
    }
    
    public event Action<Guid, Peer>? OnPeerAdded;
    public event Action<Guid, Peer>? OnPeerRemoved;

    public Peer this[Guid userId]
    {
        get => _connectedPeers[userId];
        set => _connectedPeers[userId] = value;
    }
    
    private void OnReceivedP2PInterconnect(MessageContext<P2PInterconnect> ctx)
    {
        var message = ctx.Message;
        
        if (message.PossibleUsers.Length == 0)
        {
            _logger.LogInformation("[Peer] Server didn't return any P2P interconnect info");
            return;
        }

        Task.Run(async () =>
        {
            foreach (var user in message.PossibleUsers)
            {
                if (user == _serverLinkHolder.UserId)
                    continue;
                if (_connectedPeers.ContainsKey(user))
                    continue;
                
                _logger.LogInformation(
                    "[Peer] Trying to establish P2P connection with user {User}",
                    user);

                try
                {
                    var conResult = await RequestConnectPartnerAsync(user, false, CancellationToken.None);

                    if (conResult)
                    {
                        _logger.LogError(
                            "[Peer] Successfully established P2P connection with user {User}",
                            user);
                    }
                    else
                    {
                        _logger.LogWarning(
                            "[Peer] Failed to establish P2P connection with user {User}",
                            user);
                    }
                }
                catch (Exception e)
                {
                    _logger.LogError(
                        e, "[Peer] Failed to establish P2P connection with user {User}",
                        user);
                }
            }
        }).Forget();
    }

    private void OnReceivedP2PConNotification(MessageContext<P2PConNotification> ctx)
    {
        var message = ctx.Message;
        
        if (_bargainsDic.TryGetValue(message.PartnerIds, out var value))
        {
            //说明本机也发送了P2PConRequest给对方
            if (value > message.Bargain)
            {
                //本机发送的P2pConRequest的bargain值比对方的大，忽略这个P2PConNotification
                _logger.LogWarning(
                    "[Peer] Received P2PConNotification with bargain {Bargain} from {PartnerId}, but this machine has sent a P2PConRequest with bigger bargain {Bigger}, ignore this request",
                    message.Bargain, message.PartnerIds, value);
                return;
            }

            if (value == message.Bargain) //2^31-1的概率
                if (_serverLinkHolder.UserId.CompareTo(message.PartnerIds) < 0)
                {
                    _logger.LogWarning(
                        "[Peer] Received P2PConNotification with bargain {Bargain} from {PartnerId}, but this machine has sent a P2PConRequest with same bargain {Bigger}, ignore this request",
                        message.Bargain, message.PartnerIds, value);
                    return;
                }
        }

        var partnerId = message.PartnerIds;
        var cts = new CancellationTokenSource();

        _logger.LogInformation(
            "[Peer] Received P2PConNotification from {PartnerId}",
            partnerId);
        
        Task.Run(async () =>
        {
            var tmpLink = await CreateTempServerLinkAsync(partnerId, cts.Token);

            if (tmpLink == null)
            {
                _logger.LogError(
                    "[Peer] Failed to create a temp link with server to connect with partner {partnerId}",
                    partnerId);

                return;
            }

            var privatePort = tmpLink.Session.LocalEndPoint?.Port ?? NetworkHelper.GetAvailablePrivatePort();
            var conContext = await GetSelfConContextAsync(privatePort, message.UseUdp, cts.Token);
            var conAccept = new P2PConAccept(message.Bargain, _serverLinkHolder.UserId, conContext);
            var endPoint = new IPEndPoint(_clientSettingProvider.ServerAddress, _clientSettingProvider.ServerPort);
            var conInitiator =
                ActivatorUtilities.CreateInstance<P2PConInitiator>(
                    _serviceProvider,
                    partnerId,
                    tmpLink,
                    endPoint,
                    conAccept);
            
            if (_initiator.TryRemove(partnerId, out var initiator))
                initiator.Dispose();
            
            _initiator.AddOrUpdate(partnerId, conInitiator, (_, _) => conInitiator);
            
            await DoP2PConProcessorAsync(partnerId, conInitiator, cts.Token);
        }, cts.Token).Forget();
    }
    
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            CheckLinkState();
            EstablishLink(stoppingToken);
            
            await Task.Delay(3000, stoppingToken);
        }
    }

    private void CheckLinkState()
    {
        foreach (var (guid, _) in _connectedPeers.Where(p => !p.Value.IsConnected))
        {
            _logger.LogWarning(
                "[Peer] Peer {PartnerId} is disconnected, removing it from connected peers",
                guid);
            _connectedPeers.TryRemove(guid, out _);
        }
    }
    
    private async Task<DispatchableSession?> CreateTempServerLinkAsync(Guid partnerId, CancellationToken cancellationToken)
    {
        if (_tmpLinkMakerDic.TryRemove(partnerId, out var old))
        {
            _logger.LogWarning(
                "[Peer] Canceling old temp link maker for partner {partnerId}",
                partnerId);
            
            var (oldSession, oldCts) = old;

            await oldCts.CancelAsync();
            oldSession.Close();
            oldCts.Dispose();
        }
        
        var endPoint = new IPEndPoint(_clientSettingProvider.ServerAddress, _clientSettingProvider.ServerPort);
        var tmpSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        tmpSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

        try
        {
            _logger.LogInformation(
                "[Peer] Creating a temp link with server to connect with partner {partnerId}",
                partnerId);
            
            await tmpSocket.ConnectAsync(endPoint, cancellationToken);
        }
        catch (SocketException e)
        {
            _logger.LogError(
                e, "[Peer] Failed to create a temp link with server to connect with partner {partnerId}",
                partnerId);
            
            return null;
        }
        
        _logger.LogInformation(
            "[Peer] Successfully connected to server to create a temp link with partner {partnerId}",
            partnerId);
        
        var tmpLink = ActivatorUtilities.CreateInstance<TcpSession>(
            _serviceProvider,
            0, tmpSocket);
        var dispatcher = ActivatorUtilities.CreateInstance<DefaultDispatcher>(_serviceProvider);
        var dispatchSession = new DispatchableSession(tmpLink, dispatcher, cancellationToken);
        var signin = new SigninMessage
        {
            BindingTestResult = BindingTestResult.Unknown,
            FilteringBehavior = FilteringBehavior.Unknown,
            MappingBehavior = MappingBehavior.Unknown,
            JoinP2PNetwork = false,
            Id = _serverLinkHolder.UserId
        };
        var signinSucceeded = await dispatchSession.Dispatcher.SendAndListenOnce<SigninMessage, SigninSucceeded>(
            dispatchSession.Session,
            signin,
            cancellationToken);

        if (signinSucceeded == null)
        {
            _logger.LogWarning(
                "[Peer] Failed to create a temp link with server to connect with partner {partnerId}",
                partnerId);
            return null;
        }
        
        _logger.LogInformation(
            "[Peer] Successfully created a temp link with server to connect with partner {partnerId}, local endpoint: {LocalEndPoint}",
            partnerId, dispatchSession.Session.LocalEndPoint);
        
        return dispatchSession;
    }
    
    private async Task<P2PConContextInit> GetSelfConContextAsync(
        int privatePort,
        bool useUdp,
        CancellationToken cancellationToken)
    {
        await TaskHelper.WaitUtil(() => !_upnpManager.IsFetchingStatus, cancellationToken);
        
        var portMap = await _upnpManager.CreatePortMapAsync(Protocol.Tcp, privatePort);
        P2PConContextInit conInfo;
        
        // If UPNP is available, use UPNP
        if (portMap != null)
        {
            _logger.LogInformation("[Peer] UPNP is available, using UPNP to connect to partner.");
            conInfo = new P2PConContextInit
            {
                PortDeterminationMode = PortDeterminationMode.Upnp,
                PublicPort = (ushort)portMap.PublicPort
            };
        }
        // If NAT's behavior is not symmetric, use the same port as the temp link
        else if ((_serverLinkHolder.NatType?.MappingBehavior ?? MappingBehavior.Unknown)
                 is MappingBehavior.Direct or MappingBehavior.EndpointIndependent)
        {
            _logger.LogInformation("[Peer] NAT's behavior is not symmetric, using the same port as the temp link.");
            conInfo = new P2PConContextInit
            {
                PortDeterminationMode = PortDeterminationMode.UseTempLinkPort,
                PublicPort = (ushort)privatePort
            };
        }
        else
        {
            _logger.LogWarning("[Peer] NAT's behavior is symmetric, using port prediction to connect to partner.");
            var endPoint = new IPEndPoint(_clientSettingProvider.ServerAddress, _clientSettingProvider.ServerPort);
            var predictPorts =
                await StunHelper.PredictPublicPortAsync(_serviceProvider, _logger, endPoint, cancellationToken);
            
            conInfo = predictPorts.ToP2PConInfoInit() with {PortDeterminationMode = PortDeterminationMode.Predict};
        }

        conInfo = conInfo with {UseUdp = useUdp};
        return conInfo;
    }
    
    /// <summary>
    ///     添加指定ID的用户为Peer，PeerManager会在合适的时候与其建立Link
    /// </summary>
    /// <param name="userId"></param>
    /// <returns></returns>
    public bool AddLink(Guid userId)
    {
        return _allPeers.TryAdd(userId, false);
    }
    
    public bool AddLink(Peer peer)
    {
        var userId = peer.Id;
        var linkBase = peer.DirectLink;

        if (_connectedPeers.TryGetValue(userId, out var value))
        {
            if (value.DirectLink == linkBase) //如果相等，则没必要继续
                return true;

            if (!_connectedPeers.TryRemove(userId, out _)) //旧的移除失败
                return false;

            OnPeerRemoved?.Invoke(userId, peer);
        }

        if (!_connectedPeers.TryAdd(userId, peer))
            return false;

        OnPeerAdded?.Invoke(userId, peer);

        return true;
    }
    
    private void OnP2PConProcessorDone(
        Guid partnerId,
        P2PConInitiator conInitiator)
    {
        if (conInitiator.EstablishedConnection == null) return;

        var cts = new CancellationTokenSource();
        var dispatcher = ActivatorUtilities.CreateInstance<DefaultDispatcher>(_serviceProvider);
        var session = new DispatchableSession(conInitiator.EstablishedConnection, dispatcher, cts.Token);
        var peer = ActivatorUtilities.CreateInstance<Peer>(
            _serviceProvider,
            partnerId,
            conInitiator.RemoteEndPoint!,
            session,
            cts);
        
        peer.StartHeartBeat();

        AddLink(peer);
    }
    
    private async Task<bool> DoP2PConProcessorAsync(
        Guid partnerId,
        P2PConInitiator conInitiator,
        CancellationToken ct)
    {
        ISession? resultLink = null;
        
        try
        {
            resultLink = await conInitiator.StartAsync();

            if (resultLink == null)
                _logger.LogWarning(
                    "[Peer] Failed to connect to partner {partnerId}",
                    partnerId);
            else
                _logger.LogInformation(
                    "[Peer] Successfully connected to partner {partnerId}",
                    partnerId);
            
            OnP2PConProcessorDone(partnerId, conInitiator);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "[Peer] Failed to connect to partner {partnerId}", partnerId);
        }
        finally
        {
            if (_allPeers.ContainsKey(partnerId))
                _allPeers.TryUpdate(partnerId, false, true);
            
            _bargainsDic.TryRemove(partnerId, out _);
            _initiator.TryRemove(partnerId, out _);
            conInitiator.Dispose();
        }

        return resultLink != null;
    }
    
    public bool HasLink(Guid userId)
    {
        return _connectedPeers.ContainsKey(userId);
    }
    
    /// <summary>
    /// Active connect to partner
    /// </summary>
    /// <param name="partnerId"></param>
    /// <param name="useUdp"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    private async Task<bool> RequestConnectPartnerAsync(
        Guid partnerId,
        bool useUdp,
        CancellationToken cancellationToken)
    {
        if (_bargainsDic.ContainsKey(partnerId))
        {
            _logger.LogWarning(
                "[Peer] Already trying to connect to partner {partnerId}",
                partnerId);
            return false;
        }
        
        _logger.LogInformation(
            "[Peer] Trying to connect to partner {partnerId}",
            partnerId);
        
        var tmpLink = await CreateTempServerLinkAsync(partnerId, cancellationToken);

        if (tmpLink == null)
        {
            _logger.LogError(
                "[Peer] Failed to create a temp link with server to connect with partner {partnerId}",
                partnerId);
            
            return false;
        }

        var privatePort =
            tmpLink.Session.LocalEndPoint?.Port ?? NetworkHelper.GetAvailablePrivatePort();
        var bargain = Random.Shared.Next();
        
        _bargainsDic.AddOrUpdate(partnerId, bargain, (_, _) => bargain);

        var endPoint = new IPEndPoint(_clientSettingProvider.ServerAddress, _clientSettingProvider.ServerPort);
        var conContext = await GetSelfConContextAsync(privatePort, useUdp, cancellationToken);
        var conRequest = new P2PConRequest(
            bargain,
            partnerId,
            _serverLinkHolder.UserId,
            (ushort)privatePort,
            conContext);
        var conProcessor = ActivatorUtilities.CreateInstance<P2PConInitiator>(
            _serviceProvider,
            partnerId,
            tmpLink,
            endPoint,
            conRequest);
        
        return await DoP2PConProcessorAsync(partnerId, conProcessor, cancellationToken);
    }

    private void EstablishLink(CancellationToken cancellationToken)
    {
        foreach (var (key, trying) in _allPeers)
        {
            if (_connectedPeers.ContainsKey(key) || trying) continue;
            
            _logger.LogInformation(
                "[Peer] Trying to connect to partner {key}...",
                key);
            
            RequestConnectPartnerAsync(key, trying, cancellationToken).Forget();
            _allPeers.TryUpdate(key, true, false);
        }
    }

    public IEnumerator<KeyValuePair<Guid, Peer>> GetEnumerator() => _connectedPeers.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}