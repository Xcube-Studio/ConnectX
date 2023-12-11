using System.Net;
using ConnectX.Client.Interfaces;
using ConnectX.Shared.Helpers;
using ConnectX.Shared.Messages;
using ConnectX.Shared.Messages.Identity;
using Hive.Both.General.Dispatchers;
using Hive.Network.Abstractions.Session;
using Hive.Network.Tcp;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ConnectX.Client;

public class ServerLinkHolder : BackgroundService, IServerLinkHolder
{
    private readonly IDispatcher _dispatcher;
    private readonly IClientSettingProvider _settingProvider;
    private readonly IConnector<TcpSession> _tcpConnector;
    private readonly ILogger _logger;
    
    public ISession? ServerSession { get; private set; }
    public bool IsConnected { get; private set; }
    public bool IsSignedIn { get; private set; }
    
    public ServerLinkHolder(
        IDispatcher dispatcher,
        IClientSettingProvider settingProvider,
        IConnector<TcpSession> tcpConnector,
        ILogger<ServerLinkHolder> logger)
    {
        InitHelper.Init();
        
        _dispatcher = dispatcher;
        _settingProvider = settingProvider;
        _tcpConnector = tcpConnector;
        _logger = logger;

        _dispatcher.AddHandler<SigninSucceeded>(OnSigninSucceededReceived);
        _dispatcher.AddHandler<HeartBeat>(OnHeartBeatReceived);
        _dispatcher.AddHandler<ShutdownMessage>(OnShutdownMessageReceived);
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("[CLIENT] Starting server link holder...");

        await ConnectAsync(cancellationToken);
        await TaskHelper.WaitUntilAsync(() => IsConnected, cancellationToken);
        await base.StartAsync(cancellationToken);
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("[CLIENT] Stopping server link holder...");
        return base.StopAsync(cancellationToken);
    }

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("[CLIENT] Getting client NAT type...");

        var natType = await StunHelper.GetNatTypeAsync(cancellationToken: cancellationToken);
        
        _logger.LogInformation("[CLIENT] Client NAT type: {natType}", natType);
        _logger.LogInformation(
            "[CLIENT] ConnectX NAT type: {natType}",
            StunHelper.ToNatTypes(natType));
        
        _logger.LogInformation("[CLIENT] Connecting to server...");

        var endPoint = new IPEndPoint(_settingProvider.ServerAddress, _settingProvider.ServerPort);
        var session = await _tcpConnector.ConnectAsync(endPoint, cancellationToken);

        if (session == null)
        {
            _logger.LogError("[CLIENT] Failed to connect to server at endpoint {endPoint}", endPoint);
            return;
        }

        session.BindTo(_dispatcher);
        session.StartAsync(cancellationToken).Forget();
        
        _logger.LogInformation("[CLIENT] Sending signin message to server...");

        await _dispatcher.SendAsync(session, new SigninMessage
        {
            BindingTestResult = natType.BindingTestResult,
            FilteringBehavior = natType.FilteringBehavior,
            MappingBehavior = natType.MappingBehavior
        });
        await TaskHelper.WaitUntilAsync(() => IsSignedIn, cancellationToken);
        
        _logger.LogInformation("[CLIENT] Connected and signed to server at endpoint {endPoint}", endPoint);

        ServerSession = session;
        IsConnected = true;
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        if (!IsConnected || ServerSession == null) return;
        
        _logger.LogInformation("[CLIENT] Disconnecting from server...");

        await _dispatcher.SendAsync(ServerSession, new ShutdownMessage());
        ServerSession.Close();
        
        _logger.LogInformation("[CLIENT] Disconnected from server.");
        IsConnected = false;
    }
    
    private void OnSigninSucceededReceived(MessageContext<SigninSucceeded> obj)
    {
        IsSignedIn = true;
        _dispatcher.RemoveHandler<SigninSucceeded>(OnSigninSucceededReceived);
    }

    private void OnHeartBeatReceived(MessageContext<HeartBeat> ctx)
    {
        _logger.LogDebug("[CLIENT] Heartbeat received from server.");
    }
    
    private void OnShutdownMessageReceived(MessageContext<ShutdownMessage> ctx)
    {
        _logger.LogInformation("[CLIENT] Shutdown message received from server.");
        DisconnectAsync(CancellationToken.None).Forget();
    }
    
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[CLIENT] Start sending heartbeat...");
        
        while (!stoppingToken.IsCancellationRequested && IsConnected && ServerSession != null)
        {
            await _dispatcher.SendAsync(ServerSession, new HeartBeat());
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }
        
        _logger.LogInformation("[CLIENT] Stop sending heartbeat.");
    }
}