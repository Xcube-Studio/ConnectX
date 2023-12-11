using System.Collections.Concurrent;
using ConnectX.Shared.Messages;
using Hive.Both.General.Dispatchers;
using Hive.Network.Abstractions;
using Hive.Network.Abstractions.Session;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.Threading;

namespace ConnectX.Server;

public class ClientManager
{
    private readonly ConcurrentDictionary<SessionId, WatchDog> _watchDogMapping = new ();
    private readonly IDispatcher _dispatcher;
    private readonly ILogger _logger;

    public ClientManager(
        IDispatcher dispatcher,
        ILogger<ClientManager> logger)
    {
        _dispatcher = dispatcher;
        _logger = logger;

        _dispatcher.AddHandler<HeartBeat>(OnReceivedHeartBeat);
    }

    /// <summary>
    /// Add the session to the session mapping.
    /// </summary>
    /// <param name="id"></param>
    /// <param name="session"></param>
    /// <returns>returns the assigned session id, if id is default(Guid), it means the process has failed</returns>
    public SessionId AttachSession(SessionId id, ISession session)
    {
        var watchDog = new WatchDog(session);
        
        if (!_watchDogMapping.TryAdd(id, watchDog))
        {
            _logger.LogError(
                "[CLIENT_MANAGER] Failed to add session to the session mapping, session id: {sessionId}",
                id);
            return default;
        }

        session.BindTo(_dispatcher);
        _logger.LogInformation("[CLIENT_MANAGER] Session attached, session id: {sessionId}", id.Id);

        return id;
    }
    
    public bool IsSessionAttached(SessionId id)
    {
        return _watchDogMapping.ContainsKey(id);
    }

    private void OnReceivedHeartBeat(MessageContext<HeartBeat> ctx)
    {
        if (!_watchDogMapping.TryGetValue(ctx.FromSession.Id, out var watchDog))
        {
            _logger.LogWarning(
                "[CLIENT_MANAGER] Received heartbeat from unattached session, session id: {sessionId}",
                ctx.FromSession.Id.Id);
            
            ctx.Dispatcher.SendAsync(ctx.FromSession, new ShutdownMessage()).Forget();
            ctx.Dispatcher.RemoveHandler<HeartBeat>(OnReceivedHeartBeat);
            return;
        }
        
        ctx.Dispatcher.SendAsync(ctx.FromSession, new HeartBeat()).Forget();
        watchDog.Received();
    }
    
    public void StartWatchDog(CancellationToken token)
    {
        WatchDogCheckLoopAsync(token).Forget();
    }

    private async Task WatchDogCheckLoopAsync(CancellationToken token)
    {
        _logger.LogInformation("[CLIENT_MANAGER] Watchdog started.");
        
        while (!token.IsCancellationRequested)
        {
            foreach (var (id, watchDog) in _watchDogMapping)
            {
                if (!watchDog.IsTimeoutExceeded()) continue;
                
                _logger.LogWarning(
                    "[CLIENT_MANAGER] Session timeout, session id: {sessionId}, removed from session mapping.",
                    id);
                    
                await _dispatcher.SendAsync(watchDog.Session, new ShutdownMessage());
                watchDog.Session.Close();
                
                _watchDogMapping.TryRemove(id, out _);
            }

            await Task.Delay(500, token);
        }
        
        _logger.LogInformation("[CLIENT_MANAGER] Watchdog stopped.");
    }
}