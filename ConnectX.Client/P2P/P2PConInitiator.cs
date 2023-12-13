using System.Net;
using ConnectX.Client.P2P.LinkMaker;
using ConnectX.Client.P2P.LinkMaker.ManyToMany;
using ConnectX.Client.P2P.LinkMaker.ManyToSingle;
using ConnectX.Shared.Helpers;
using ConnectX.Shared.Messages.P2P;
using ConnectX.Shared.Models;
using Hive.Both.General.Dispatchers;
using Hive.Common.Shared.Helpers;
using Hive.Network.Abstractions.Session;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ConnectX.Client.P2P;

/// <summary>
/// A class that to help to establish P2P connection
/// </summary>
public class P2PConInitiator : IDisposable
{
    private readonly Guid _partnerId;
    private readonly DispatchableSession _tmpLinkToServer;
    private readonly IPEndPoint _localEndPoint;
    private readonly P2PConContext _selfContext;
    private readonly ILogger<P2PConInitiator> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly CancellationTokenSource _cancellationTokenSource;
    private TaskCompletionSource<ISession?>? _completionSource;
    
    public bool IsSucceeded { get; private set; }
    public bool IsConnecting { get; private set; } = true;
    public ISession? EstablishedConnection { get; private set; }
    public IPEndPoint? RemoteEndPoint { get; private set; }
    
    public P2PConInitiator(
        IServiceProvider serviceProvider,
        Guid partnerId,
        DispatchableSession tmpLinkToServer,
        IPEndPoint localEndPoint,
        P2PConContext selfContext,
        ILogger<P2PConInitiator> logger)
    {
        _serviceProvider = serviceProvider;
        _partnerId = partnerId;
        _tmpLinkToServer = tmpLinkToServer;
        _localEndPoint = localEndPoint;
        _selfContext = selfContext;
        _logger = logger;
        _cancellationTokenSource = new CancellationTokenSource();

        tmpLinkToServer.Dispatcher.AddHandler<P2PConReady>(OnP2PConReadyReceived);
    }
    
    public Task<ISession?> StartAsync()
    {
        _completionSource = new TaskCompletionSource<ISession?>();
        _tmpLinkToServer.Dispatcher.SendAsync(_tmpLinkToServer.Session, _selfContext).Forget();
        
        return _completionSource.Task;
    }

    private async Task<ISession?> CreateDirectLinkToPartnerAsync(
        P2PLinkMaker linkMaker,
        DispatchableSession? serverTmpSocket = null)
    {
        var directLink = await linkMaker.BuildLinkAsync();
        
        if (serverTmpSocket != null)
        {
            serverTmpSocket.Session.Close();
            serverTmpSocket.Dispose();

            _logger.LogInformation(
                "[P2P_CONN_INIT] {LocalEndPoint} has closed the temp connection with server",
                serverTmpSocket.Session.LocalEndPoint);
        }

        return directLink;
    }
    
    private void OnP2PConReadyReceived(MessageContext<P2PConReady> ctx)
    {
        _logger.LogInformation(
            "[P2P_CONN_INIT] Received P2PConReady from {PartnerId}",
            _partnerId);

        var message = ctx.Message;
        
        if (message.RecipientId != _partnerId) return;
        var connectionMaker = CreateLinkMaker(
            _partnerId,
            message.Time,
            message,
            _selfContext,
            _cancellationTokenSource.Token);

        Task.Run(async () =>
        {
            try
            {
                _logger.LogInformation(
                    "[P2P_CONN_INIT] Trying to create direct link to {partnerId}, local endpoint: {localEndPoint}, remote endpoint: {remoteAddr}:{remotePort}",
                    _partnerId, _tmpLinkToServer.Session.LocalEndPoint, message.PublicAddress, message.PublicPort);
                
                EstablishedConnection = await CreateDirectLinkToPartnerAsync(connectionMaker, _tmpLinkToServer);
                RemoteEndPoint = EstablishedConnection?.RemoteEndPoint;

                IsSucceeded = EstablishedConnection != null;
                IsConnecting = false;
                
                _completionSource!.TrySetResult(EstablishedConnection);
            }
            catch (Exception e)
            {
                _completionSource!.SetException(e);
            }
        }, _cancellationTokenSource.Token).CatchException();
    }
    
    private int[] ProducePredictPortArray(P2PConContext context)
    {
        List<int> targetPredictPort = [];
        var portPredictResult = PortPredictResult.FromP2PConContext(context);
        
        switch (portPredictResult.Law)
        {
            case ChangeLaws.Increase:
            {
                for (var port = context.CurrentUsedPort + context.Diff;
                     port <= context.PublicPortUpper;
                     port +=
                         context.Diff)
                    targetPredictPort.Add(port);
                break;
            }
            case ChangeLaws.Decrease:
            {
                for (var port = context.CurrentUsedPort + context.Diff;
                     port >= context.PublicPortLower;
                     port +=
                         context.Diff)
                    targetPredictPort.Add(port);
                break;
            }
            case ChangeLaws.Random:
            default:
            {
                for (var port = context.PublicPortLower; port >= context.PublicPortLower; port++)
                    targetPredictPort.Add(port);
                break;
            }
        }
        
        _logger.LogTrace("[P2P_CONN_INIT] Smallest predict port is {Min}", targetPredictPort.Min());
        _logger.LogTrace("[P2P_CONN_INIT] Biggest predict port is {Max}", targetPredictPort.Max());
        _logger.LogTrace("[P2P_CONN_INIT] Predict port count is {Count}", targetPredictPort.Count);

        return targetPredictPort.ToArray();
    }

    private P2PLinkMaker CreateLinkMaker(
        Guid partnerId,
        long time,
        P2PConContext targetContext,
        P2PConContext selfContext,
        CancellationToken token)
    {
        P2PLinkMaker linkMaker;

        _logger.LogDebug("Create P2PLinkMaker, {@P2PConContext}",
            selfContext);

        if (targetContext.PortDeterminationMode is
            PortDeterminationMode.UseTempLinkPort or PortDeterminationMode.Upnp)
        {
            var remoteIpe = new IPEndPoint(targetContext.PublicAddress, targetContext.PublicPort);

            if (selfContext.PortDeterminationMode is
                PortDeterminationMode.UseTempLinkPort or PortDeterminationMode.Upnp)
            {
                if (selfContext.UseUdp)
                    linkMaker = ActivatorUtilities.CreateInstance<UdpSinglePortLinkMaker>(
                        _serviceProvider,
                        time,
                        partnerId,
                        selfContext.PublicPort,
                        remoteIpe,
                        token);
                else
                    linkMaker = ActivatorUtilities.CreateInstance<TcpSinglePortLinkMaker>(
                        _serviceProvider,
                        time,
                        partnerId,
                        selfContext.PublicPort,
                        remoteIpe,
                        token);
            }
            else
            {
                var selfPredictPort = ProducePredictPortArray(selfContext);
                linkMaker = ActivatorUtilities.CreateInstance<TcpManyToSingleLinkMaker>(
                    _serviceProvider,
                    time,
                    partnerId,
                    remoteIpe,
                    selfPredictPort,
                    token);
            }
        }
        else //对方不能确认自己的端口，给了一个端口范围
        {
            var targetPredictPort = ProducePredictPortArray(targetContext);

            if (selfContext.PortDeterminationMode
                is PortDeterminationMode.UseTempLinkPort or PortDeterminationMode.Upnp)
            {
                linkMaker = ActivatorUtilities.CreateInstance<TcpSingleToManyLinkMaker>(
                    _serviceProvider,
                    time,
                    partnerId,
                    1,
                    targetPredictPort.ToArray(),
                    targetContext.PublicAddress,
                    selfContext.PublicPort,
                    token
                );
            }
            else //自己也是无法确认IP
            {
                var selfPredictPort = ProducePredictPortArray(selfContext);
                // 寄了，但还是要尝试一下的
                linkMaker = ActivatorUtilities.CreateInstance<TcpManyToManyLinkMaker>(
                    _serviceProvider,
                    time,
                    partnerId,
                    targetContext.PublicAddress,
                    selfPredictPort,
                    targetPredictPort,
                    token);
            }
        }

        _logger.LogDebug("Created link maker {@LinkMaker}", linkMaker);
        return linkMaker;
    }

    public void Dispose()
    {
        _tmpLinkToServer.Session.Close();
        _tmpLinkToServer?.Dispose();
        _cancellationTokenSource?.Dispose();
    }
}