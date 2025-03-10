using ConnectX.Shared.Messages;
using Hive.Network.Abstractions.Session;
using Hive.Network.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using ConnectX.Shared.Helpers;
using Hive.Both.General.Dispatchers;

namespace ConnectX.Relay.Managers;

public delegate void SessionDisconnectedHandler(SessionId sessionId);

public class ClientManager : BackgroundService
{
    private readonly IDispatcher _dispatcher;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<SessionId, WatchDog> _watchDogMapping = new();

    public ClientManager(
        IDispatcher dispatcher,
        ILogger<ClientManager> logger)
    {
        _dispatcher = dispatcher;
        _logger = logger;

        _dispatcher.AddHandler<ShutdownMessage>(OnReceivedShutdownMessage);
        _dispatcher.AddHandler<HeartBeat>(OnReceivedHeartBeat);
    }

    public event SessionDisconnectedHandler? OnSessionDisconnected;

    /// <summary>
    ///     Add the session to the session mapping.
    /// </summary>
    /// <param name="id"></param>
    /// <param name="session"></param>
    /// <returns>returns the assigned session id, if id is default(Guid), it means the process has failed</returns>
    public SessionId AttachSession(SessionId id, ISession session)
    {
        var watchDog = new WatchDog(session);

        if (!_watchDogMapping.TryAdd(id, watchDog))
        {
            _logger.LogFailedToAddSessionToSessionMapping(id);
            return default;
        }

        //session.BindTo(_dispatcher);
        _logger.LogSessionAttached(id);

        return id;
    }

    public bool IsSessionAttached(SessionId id)
    {
        return _watchDogMapping.ContainsKey(id);
    }

    private void OnReceivedShutdownMessage(MessageContext<ShutdownMessage> ctx)
    {
        if (!_watchDogMapping.TryRemove(ctx.FromSession.Id, out _)) return;

        _logger.LogReceivedShutdownMessage(ctx.FromSession.Id);

        OnSessionDisconnected?.Invoke(ctx.FromSession.Id);
    }

    private void OnReceivedHeartBeat(MessageContext<HeartBeat> ctx)
    {
        if (!_watchDogMapping.TryGetValue(ctx.FromSession.Id, out var watchDog))
        {
            _logger.LogReceivedHeartBeatFromUnattachedSession(ctx.FromSession.Id);

            ctx.Dispatcher.SendAsync(ctx.FromSession, new ShutdownMessage()).Forget();
            ctx.Dispatcher.RemoveHandler<HeartBeat>(OnReceivedHeartBeat);
            return;
        }

        ctx.Dispatcher.SendAsync(ctx.FromSession, new HeartBeat()).Forget();
        watchDog.Received();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogWatchDogStarted();

        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var (id, watchDog) in _watchDogMapping)
            {
                if (!watchDog.IsTimeoutExceeded()) continue;

                _logger.LogSessionTimeout(id);

                OnSessionDisconnected?.Invoke(id);
                _dispatcher.SendAsync(watchDog.Session, new ShutdownMessage(), CancellationToken.None).Forget();
                watchDog.Session.Close();

                _watchDogMapping.TryRemove(id, out _);
            }

            await Task.Delay(500, stoppingToken);
        }

        _logger.LogWatchDogStopped();
    }
}

internal static partial class ClientManagerLoggers
{
    [LoggerMessage(LogLevel.Error,
        "[CLIENT_MANAGER] Failed to add session to the session mapping, session id: {sessionId}")]
    public static partial void LogFailedToAddSessionToSessionMapping(this ILogger logger, SessionId sessionId);

    [LoggerMessage(LogLevel.Information, "[CLIENT_MANAGER] Session attached, session id: {sessionId}")]
    public static partial void LogSessionAttached(this ILogger logger, SessionId sessionId);

    [LoggerMessage(LogLevel.Information,
        "[CLIENT_MANAGER] Received shutdown message from session, session id: {sessionId}")]
    public static partial void LogReceivedShutdownMessage(this ILogger logger, SessionId sessionId);

    [LoggerMessage(LogLevel.Warning,
        "[CLIENT_MANAGER] Received heartbeat from unattached session, session id: {sessionId}")]
    public static partial void LogReceivedHeartBeatFromUnattachedSession(this ILogger logger, SessionId sessionId);

    [LoggerMessage(LogLevel.Information, "[CLIENT_MANAGER] Watchdog started.")]
    public static partial void LogWatchDogStarted(this ILogger logger);

    [LoggerMessage(LogLevel.Warning,
        "[CLIENT_MANAGER] Session timeout, session id: {sessionId}, removed from session mapping.")]
    public static partial void LogSessionTimeout(this ILogger logger, SessionId sessionId);

    [LoggerMessage(LogLevel.Information, "[CLIENT_MANAGER] Watchdog stopped.")]
    public static partial void LogWatchDogStopped(this ILogger logger);
}