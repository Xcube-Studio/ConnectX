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
using ZeroTier.Core;
using TaskHelper = Hive.Common.Shared.Helpers.TaskHelper;

namespace ConnectX.Client.Managers;

public class PeerManager : BackgroundService, IEnumerable<KeyValuePair<Guid, Peer>>
{
    private readonly ConcurrentDictionary<Guid, bool> _allPeers = new();
    private readonly ConcurrentDictionary<Guid, int> _bargainsDic = [];
    private readonly IClientSettingProvider _clientSettingProvider;
    private readonly ConcurrentDictionary<Guid, Peer> _connectedPeers = [];
    private readonly IRoomInfoManager _roomInfoManager;
    private readonly IDispatcher _dispatcher;
    private readonly ConcurrentDictionary<Guid, P2PConInitiator> _initiator = [];
    private readonly ILogger _logger;
    private readonly IServerLinkHolder _serverLinkHolder;
    private readonly IZeroTierNodeLinkHolder _zeroTierNodeLinkHolder;
    private readonly IServiceProvider _serviceProvider;
    private readonly ConcurrentDictionary<Guid, (DispatchableSession, CancellationToken)> _tmpLinkMakerDic = new();

    public PeerManager(
        IRoomInfoManager roomInfoManager,
        IDispatcher dispatcher,
        IServerLinkHolder serverLinkHolder,
        IClientSettingProvider clientSettingProvider,
        IServiceProvider serviceProvider,
        IZeroTierNodeLinkHolder zeroTierNodeLinkHolder,
        ILogger<PeerManager> logger)
    {
        _roomInfoManager = roomInfoManager;
        _dispatcher = dispatcher;
        _serverLinkHolder = serverLinkHolder;
        _clientSettingProvider = clientSettingProvider;
        _zeroTierNodeLinkHolder = zeroTierNodeLinkHolder;
        _serviceProvider = serviceProvider;
        _logger = logger;

        _zeroTierNodeLinkHolder.OnRouteInfoUpdated += ZeroTierNodeLinkHolderOnOnRouteInfoUpdated;

        _dispatcher.AddHandler<P2PConNotification>(OnReceivedP2PConNotification);
    }

    public Peer this[Guid userId]
    {
        get => _connectedPeers[userId];
        set => _connectedPeers[userId] = value;
    }

    public IEnumerator<KeyValuePair<Guid, Peer>> GetEnumerator()
    {
        return _connectedPeers.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    public event Action<Guid, Peer>? OnPeerAdded;
    public event Action<Guid, Peer>? OnPeerRemoved;

    private void ZeroTierNodeLinkHolderOnOnRouteInfoUpdated(RouteInfo[] routes)
    {
        if (_roomInfoManager.CurrentGroupInfo == null)
        {
            _logger.LogRoomInfoEmpty();
            return;
        }

        foreach (var routeInfo in routes)
        {
            var userInfo = _roomInfoManager.CurrentGroupInfo.Users
                .FirstOrDefault(u => u.IpAddresses.Contains(routeInfo.Target));

            if (userInfo == null)
            {
                _logger.LogUserWithAddressNotFound(routeInfo.Target);
                continue;
            }

            var userId = userInfo.UserId;

            if (userId == _serverLinkHolder.UserId)
                continue;
            if (_connectedPeers.ContainsKey(userId))
                continue;

            Task.Run(async () =>
            {
                _logger.LogTryingToEstablishP2PConnectionWithUser(userId);

                try
                {
                    var conResult = await RequestConnectPartnerAsync(userId, false, CancellationToken.None);

                    if (conResult)
                        _logger.LogSuccessfullyEstablishedP2PConnectionWithUser(userId);
                    else
                        _logger.LogFailedToEstablishP2PConnectionWithUser(userId);
                }
                catch (Exception e)
                {
                    _logger.LogFailedToEstablishP2PConnectionWithUser(e, userId);
                }
            });
        }
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
                _logger.LogGreaterBargain(message.Bargain, message.PartnerIds, value);
                return;
            }

            if (value == message.Bargain) //2^31-1的概率
                if (_serverLinkHolder.UserId.CompareTo(message.PartnerIds) < 0)
                {
                    _logger.LogSameBargain(message.Bargain, message.PartnerIds, value);
                    return;
                }
        }

        var partnerId = message.PartnerIds;
        var cts = new CancellationTokenSource();

        _logger.LogReceivedP2PConNotification(partnerId);

        Task.Run(async () =>
        {
            var tmpLink = await CreateTempServerLinkAsync(partnerId, cts.Token);

            if (tmpLink == null)
            {
                _logger.LogFailedToCreateATempLinkWithServerToConnectWithPartner(partnerId);

                return;
            }

            var conContext = await GetSelfConContextAsync(message.UseUdp, cts.Token);
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
            _logger.LogPeerIsDisconnected(guid);

            if (_connectedPeers.TryRemove(guid, out var peer))
                peer.StopHeartBeat();
        }
    }

    private async Task<DispatchableSession?> CreateTempServerLinkAsync(
        Guid partnerId,
        CancellationToken cancellationToken)
    {
        if (_tmpLinkMakerDic.TryRemove(partnerId, out var old))
        {
            _logger.LogCancelingOldTempLinkMakerForPartner(partnerId);

            var (oldSession, _) = old;

            oldSession.Dispose();
            oldSession.Session.Close();
        }

        var endPoint = new IPEndPoint(_clientSettingProvider.ServerAddress, _clientSettingProvider.ServerPort);
        var tmpSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        tmpSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

        try
        {
            _logger.LogCreatingATempLinkWithServerToConnectWithPartner(partnerId);

            await tmpSocket.ConnectAsync(endPoint, cancellationToken);
            await Task.Delay(150, cancellationToken);
        }
        catch (SocketException e)
        {
            _logger.LogFailedToCreateATempLinkWithServerToConnectWithPartner(e, partnerId);

            return null;
        }

        _logger.LogSuccessfullyConnectedToServerToCreateATempLinkWithPartner(partnerId);

        var tmpLink = ActivatorUtilities.CreateInstance<TcpSession>(
            _serviceProvider,
            0, tmpSocket);
        var dispatcher = ActivatorUtilities.CreateInstance<DefaultDispatcher>(_serviceProvider);
        var dispatchSession = new DispatchableSession(tmpLink, dispatcher, cancellationToken);
        var signin = new SigninMessage
        {
            Id = _serverLinkHolder.UserId,
            JoinP2PNetwork = false,
            DisplayName = _serverLinkHolder.UserId.ToString("N")
        };

        _logger.LogSendingASigninMessageToServerToCreateATempLinkWithPartner(partnerId);

        var signinSucceeded = await dispatchSession.Dispatcher.SendAndListenOnce<SigninMessage, SigninSucceeded>(
            dispatchSession.Session,
            signin,
            cancellationToken);

        if (signinSucceeded == null)
        {
            _logger.LogFailedToCreateATempLinkWithServerToConnectWithPartner(partnerId);
            return null;
        }

        _tmpLinkMakerDic.TryAdd(partnerId, (dispatchSession, cancellationToken));

        _logger.LogSuccessfullyCreatedATempLinkWithServerToConnectWithPartner(
            partnerId, dispatchSession.Session.LocalEndPoint!, dispatchSession.Session.RemoteEndPoint!);

        return dispatchSession;
    }

    private async Task<P2PConContextInit> GetSelfConContextAsync(
        bool useUdp,
        CancellationToken cancellationToken)
    {
        await TaskHelper.WaitUtil(_zeroTierNodeLinkHolder.IsNodeOnline, cancellationToken);

        var publicAddress = _zeroTierNodeLinkHolder.GetFirstAvailableV4Address();

        ArgumentNullException.ThrowIfNull(publicAddress);

        var conInfo = new P2PConContextInit
        {
            PublicPort = (ushort)Random.Shared.Next(20000, 65533),
            PublicAddress = publicAddress,
            UseUdp = useUdp
        };

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
                _logger.LogFailedToConnectToPartner(partnerId);
            else
                _logger.LogSuccessfullyConnectedToPartner(partnerId);

            OnP2PConProcessorDone(partnerId, conInitiator);
        }
        catch (Exception e)
        {
            _logger.LogFailedToConnectToPartner(e, partnerId);
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
    ///     Active connect to partner
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
            _logger.LogAlreadyTryingToConnectToPartner(partnerId);
            return false;
        }

        _logger.LogTryingToConnectToPartner(partnerId);

        var tmpLink = await CreateTempServerLinkAsync(partnerId, cancellationToken);

        if (tmpLink == null)
        {
            _logger.LogFailedToCreateATempLinkWithServerToConnectWithPartner(partnerId);

            return false;
        }

        var bargain = Random.Shared.Next();

        _bargainsDic.AddOrUpdate(partnerId, bargain, (_, _) => bargain);

        var endPoint = new IPEndPoint(_clientSettingProvider.ServerAddress, _clientSettingProvider.ServerPort);
        var conContext = await GetSelfConContextAsync(useUdp, cancellationToken);
        var conRequest = new P2PConRequest(
            bargain,
            partnerId,
            _serverLinkHolder.UserId,
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

            _logger.LogTryingToConnectToPartner(key);

            RequestConnectPartnerAsync(key, trying, cancellationToken).Forget();
            _allPeers.TryUpdate(key, true, false);
        }
    }
}

internal static partial class PeerManagerLoggers
{
    [LoggerMessage(LogLevel.Warning, "[Peer] Network is not ready yet, no public address found!")]
    public static partial void LogNetworkNotReady(this ILogger logger);

    [LoggerMessage(LogLevel.Warning, "[Peer] Room info is null, might be an internal error!")]
    public static partial void LogRoomInfoEmpty(this ILogger logger);

    [LoggerMessage(LogLevel.Warning, "[Peer] No matched user with address [{address}] not found, waiting for the next event...")]
    public static partial void LogUserWithAddressNotFound(this ILogger logger, IPAddress address);

    [LoggerMessage(LogLevel.Information, "[Peer] Server didn't return any P2P interconnect info")]
    public static partial void LogServerDidNotReturnAnyP2PInterconnectInfo(this ILogger logger);

    [LoggerMessage(LogLevel.Information, "[Peer] Trying to establish P2P connection with user {User}")]
    public static partial void LogTryingToEstablishP2PConnectionWithUser(this ILogger logger, Guid user);

    [LoggerMessage(LogLevel.Information, "[Peer] Successfully established P2P connection with user {User}")]
    public static partial void LogSuccessfullyEstablishedP2PConnectionWithUser(this ILogger logger, Guid user);

    [LoggerMessage(LogLevel.Warning, "[Peer] Failed to establish P2P connection with user {User}")]
    public static partial void LogFailedToEstablishP2PConnectionWithUser(this ILogger logger, Guid user);

    [LoggerMessage(LogLevel.Error, "[Peer] Failed to establish P2P connection with user {User}")]
    public static partial void LogFailedToEstablishP2PConnectionWithUser(this ILogger logger, Exception ex, Guid user);

    [LoggerMessage(LogLevel.Warning,
        "[Peer] Received P2PConNotification with bargain {Bargain} from {PartnerId}, but this machine has sent a P2PConRequest with bigger bargain {Bigger}, ignore this request")]
    public static partial void LogGreaterBargain(this ILogger logger, int bargain, Guid partnerId, int bigger);

    [LoggerMessage(LogLevel.Warning,
        "[Peer] Received P2PConNotification with bargain {Bargain} from {PartnerId}, but this machine has sent a P2PConRequest with same bargain {Bigger}, ignore this request")]
    public static partial void LogSameBargain(this ILogger logger, int bargain, Guid partnerId, int bigger);

    [LoggerMessage(LogLevel.Information, "[Peer] Received P2PConNotification from {PartnerId}")]
    public static partial void LogReceivedP2PConNotification(this ILogger logger, Guid partnerId);

    [LoggerMessage(LogLevel.Error,
        "[Peer] Failed to create a temp link with server to connect with partner {partnerId}")]
    public static partial void LogFailedToCreateATempLinkWithServerToConnectWithPartner(this ILogger logger,
        Guid partnerId);

    [LoggerMessage(LogLevel.Warning, "[Peer] Peer {PartnerId} is disconnected, removing it from connected peers")]
    public static partial void LogPeerIsDisconnected(this ILogger logger, Guid partnerId);

    [LoggerMessage(LogLevel.Warning, "[Peer] Canceling old temp link maker for partner {partnerId}")]
    public static partial void LogCancelingOldTempLinkMakerForPartner(this ILogger logger, Guid partnerId);

    [LoggerMessage(LogLevel.Information, "[Peer] Creating a temp link with server to connect with partner {partnerId}")]
    public static partial void LogCreatingATempLinkWithServerToConnectWithPartner(this ILogger logger, Guid partnerId);

    [LoggerMessage(LogLevel.Error,
        "[Peer] Failed to create a temp link with server to connect with partner {partnerId}")]
    public static partial void LogFailedToCreateATempLinkWithServerToConnectWithPartner(this ILogger logger,
        Exception ex, Guid partnerId);

    [LoggerMessage(LogLevel.Information,
        "[Peer] Successfully connected to server to create a temp link with partner {partnerId}")]
    public static partial void LogSuccessfullyConnectedToServerToCreateATempLinkWithPartner(this ILogger logger,
        Guid partnerId);

    [LoggerMessage(LogLevel.Information,
        "[Peer] Sending a signin message to server to create a temp link with partner {partnerId}, waiting for the signin response")]
    public static partial void LogSendingASigninMessageToServerToCreateATempLinkWithPartner(this ILogger logger,
        Guid partnerId);

    [LoggerMessage(LogLevel.Information,
        "[Peer] Successfully created a temp link with server to connect with partner {partnerId}, local endpoint: {LocalEndPoint}, remote endpoint: {RemoteEndPoint}")]
    public static partial void LogSuccessfullyCreatedATempLinkWithServerToConnectWithPartner(this ILogger logger,
        Guid partnerId, EndPoint localEndPoint, EndPoint remoteEndPoint);

    [LoggerMessage(LogLevel.Information, "[Peer] UPNP is available, using UPNP to connect to partner.")]
    public static partial void LogUpnpIsAvailableUsingUpnpToConnectToPartner(this ILogger logger);

    [LoggerMessage(LogLevel.Information,
        "[Peer] NAT's behavior is not symmetric, using the same port as the temp link.")]
    public static partial void LogNatsBehaviorIsNotSymmetricUsingTheSamePortAsTheTempLink(this ILogger logger);

    [LoggerMessage(LogLevel.Warning,
        "[Peer] NAT's behavior is symmetric, using port prediction to connect to partner.")]
    public static partial void LogNatsBehaviorIsSymmetricUsingPortPredictionToConnectToPartner(this ILogger logger);

    [LoggerMessage(LogLevel.Warning, "[Peer] Failed to connect to partner {partnerId}")]
    public static partial void LogFailedToConnectToPartner(this ILogger logger, Guid partnerId);

    [LoggerMessage(LogLevel.Information, "[Peer] Successfully connected to partner {partnerId}")]
    public static partial void LogSuccessfullyConnectedToPartner(this ILogger logger, Guid partnerId);

    [LoggerMessage(LogLevel.Error, "[Peer] Failed to connect to partner {partnerId}")]
    public static partial void LogFailedToConnectToPartner(this ILogger logger, Exception ex, Guid partnerId);

    [LoggerMessage(LogLevel.Warning, "[Peer] Already trying to connect to partner {partnerId}")]
    public static partial void LogAlreadyTryingToConnectToPartner(this ILogger logger, Guid partnerId);

    [LoggerMessage(LogLevel.Information, "[Peer] Trying to connect to partner {partnerId}")]
    public static partial void LogTryingToConnectToPartner(this ILogger logger, Guid partnerId);
}