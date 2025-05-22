using ConnectX.Shared.Messages.Relay;
using Hive.Both.General.Dispatchers;
using Hive.Network.Abstractions;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace ConnectX.Server.Managers;

public class RelayLoadManager
{
    private readonly ClientManager _clientManager;

    private readonly IDispatcher _dispatcher;
    private readonly ILogger _logger;

    private readonly ConcurrentDictionary<SessionId, double> _relayAvailabilityMapping = [];

    public RelayLoadManager(
        ClientManager clientManager,
        IDispatcher dispatcher,
        ILogger<RelayLoadManager> logger)
    {
        _dispatcher = dispatcher;
        _logger = logger;
        _clientManager = clientManager;

        clientManager.OnSessionDisconnected += OnSessionDisconnected;

        _dispatcher.AddHandler<RelayServerLoadInfoMessage>(OnRelayServerLoadInfoMessageRecevied);
    }

    public bool TryGetMostAvailableRelaySession([NotNullWhen(true)] out SessionId? sessionId)
    {
        sessionId = !_relayAvailabilityMapping.IsEmpty
            ? _relayAvailabilityMapping.MaxBy(x => x.Value).Key
            : null;

        return !_relayAvailabilityMapping.IsEmpty;
    }

    private void OnSessionDisconnected(SessionId sessionId)
    {
        _relayAvailabilityMapping.TryRemove(sessionId, out _);
    }

    private void OnRelayServerLoadInfoMessageRecevied(MessageContext<RelayServerLoadInfoMessage> ctx)
    {
        if (!_clientManager.IsSessionAttached(ctx.FromSession.Id))
            return;

        double availability = CalculateRelayServerAvailability(ctx.Message);

        if (!_relayAvailabilityMapping.TryAdd(ctx.FromSession.Id, availability))
            _relayAvailabilityMapping[ctx.FromSession.Id] = availability;

        _logger.LogRelayServerLoadReceviced(ctx.FromSession.Id, availability);
    }

    private static double CalculateRelayServerAvailability(RelayServerLoadInfoMessage relayServerLoad)
    {
        return
            ((relayServerLoad.MaxReferenceConnectionCount - relayServerLoad.CurrentConnectionCount) / (double)relayServerLoad.MaxReferenceConnectionCount) * 0.4 +
            (relayServerLoad.Priority / (double)100) * 0.6;
    }
}

internal static partial class RelayLoadManagerLoggers 
{
    [LoggerMessage(LogLevel.Debug, "[RELAY_LOAD_MANAGER] Relay server {SessionId} reported its load: [Availability: {Availability}]")]
    public static partial void LogRelayServerLoadReceviced(this ILogger logger, SessionId sessionId, double availability);
}