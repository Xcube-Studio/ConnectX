using System.Collections.Concurrent;
using ConnectX.Shared.Helpers;
using ConnectX.Server.Interfaces;
using ConnectX.Shared.Messages;
using ConnectX.Shared.Messages.Identity;
using Hive.Both.General.Dispatchers;
using Hive.Network.Abstractions;
using Hive.Network.Abstractions.Session;
using Hive.Network.Tcp;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ConnectX.Server;

public class Server : BackgroundService
{
    private long _currentSessionCount;
    private const int MaxSessionLoginTimeout = 10;

    private readonly CancellationTokenSource _cts = new ();
    private readonly IDispatcher _dispatcher;
    private readonly IAcceptor<TcpSession> _acceptor;
    private readonly IServerSettingProvider _serverSettingProvider;
    private readonly ClientManager _clientManager;
    private readonly ILogger _logger;

    private readonly ConcurrentDictionary<SessionId, (DateTime AddTime, ISession Session)>
        _tempSessionMapping = new();

    public Server(
        IDispatcher dispatcher,
        IAcceptor<TcpSession> acceptor,
        IServerSettingProvider serverSettingProvider,
        ClientManager clientManager,
        ILogger<Server> logger)
    {
        _dispatcher = dispatcher;
        _acceptor = acceptor;
        _serverSettingProvider = serverSettingProvider;
        _clientManager = clientManager;
        _logger = logger;
        
        _acceptor.BindTo(_dispatcher);
        _dispatcher.AddHandler<SigninMessage>(OnSigninMessageReceived);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[SERVER] Starting server...");
        
        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var (id, (add, session)) in _tempSessionMapping)
            {
                var currentTime = DateTime.UtcNow;
                if (!((currentTime - add).TotalSeconds > MaxSessionLoginTimeout)) continue;
                
                _logger.LogWarning(
                    "[CLIENT] Session [{id}] login timeout, disconnecting...",
                    session.Id.Id);
                _logger.LogInformation(
                    "[SERVER] Current online [{count}]",
                    Interlocked.Read(ref _currentSessionCount));

                await _dispatcher.SendAsync(session, new ShutdownMessage());
                _tempSessionMapping.TryRemove(id, out _);
            }

            await Task.Delay(1000, stoppingToken);
        }
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await _acceptor.SetupAsync(_serverSettingProvider.ListenIpEndPoint, cancellationToken);

        _acceptor.StartAcceptLoop(cancellationToken);
        _acceptor.OnSessionCreated += AcceptorOnOnSessionCreated;
        
        _clientManager.StartWatchDog(cancellationToken);
        
        _logger.LogInformation(
            "Server started on endpoint [{endPoint}]",
            _serverSettingProvider.ListenIpEndPoint);

        await base.StartAsync(cancellationToken);
    }

    private void AcceptorOnOnSessionCreated(IAcceptor acceptor, SessionId id, TcpSession session)
    {
        var newVal = Interlocked.Increment(ref _currentSessionCount);
        var currentTime = DateTime.UtcNow;

        session.StartAsync(CancellationToken.None).Forget();

        _tempSessionMapping.AddOrUpdate(
            id,
            _ => (currentTime, session),
            (_, old) =>
            {
                old.Session.Close();
                return (currentTime, session);
            });

        _logger.LogInformation(
            "[CLIENT] New Session joined, EndPoint [{endPoint}] ID [{id}], wait for signin message.",
            session.RemoteEndPoint, id.Id);
        _logger.LogInformation(
            "[SERVER] Current online [{count}]",
            newVal);
    }

    private void OnSigninMessageReceived(MessageContext<SigninMessage> ctx)
    {
        var session = ctx.FromSession;

        // Remove temp session mapping
        if (!_tempSessionMapping.TryRemove(session.Id, out _))
            return;

        _logger.LogInformation(
            "[CLIENT] SigninMessage received from [{endPoint}] ID [{id}]",
            session.RemoteEndPoint,
            session.Id.Id);
        
        _clientManager.AttachSession(session.Id, session);
        _dispatcher.SendAsync(session, new SigninSucceeded()).Forget();
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await _cts.CancelAsync();
        await _acceptor.TryCloseAsync(cancellationToken);

        _logger.LogInformation("Server stopped.");

        await base.StopAsync(cancellationToken);
    }
}