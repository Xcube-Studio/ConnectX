using System.Collections.Concurrent;
using System.Net;
using ConnectX.Client.Messages;
using ConnectX.Shared.Helpers;
using ConnectX.Shared.Interfaces;
using Hive.Both.General.Dispatchers;
using Microsoft.Extensions.Logging;

namespace ConnectX.Client;

public class PingChecker
{
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<uint, Pong> _pongPackets = new();
    private readonly Guid _selfId;
    private readonly IDispatchableSession _session;
    private readonly Guid _targetId;

    private uint _lastPingId;

    public PingChecker(
        Guid selfId,
        Guid targetId,
        IDispatchableSession session,
        ILogger<PingChecker> logger)
    {
        _selfId = selfId;
        _targetId = targetId;
        _session = session;
        _logger = logger;

        session.Dispatcher.AddHandler<Ping>(OnPingReceived);
        session.Dispatcher.AddHandler<Pong>(OnPongReceived);
    }

    private void OnPingReceived(MessageContext<Ping> ctx)
    {
        _logger.LogPingReceived(ctx.FromSession.RemoteEndPoint, DateTime.Now.Ticks);

        var ping = ctx.Message;
        var pong = new Pong
        {
            From = _selfId,
            To = ping.From,
            SeqId = ping.SeqId,
            Ttl = 32
        };

        _logger.LogSendPong(ctx.FromSession.RemoteEndPoint);

        _session.Dispatcher.SendAsync(_session.Session, pong).Forget();
    }

    private void OnPongReceived(MessageContext<Pong> ctx)
    {
        _logger.LogPongReceived(ctx.FromSession.RemoteEndPoint);

        var pong = ctx.Message;
        pong.SelfReceiveTime = DateTime.Now.Ticks;

        if (_pongPackets.ContainsKey(pong.SeqId))
            _pongPackets.TryRemove(pong.SeqId, out _);

        _pongPackets.TryAdd(pong.SeqId, pong);
    }

    public async Task<int> CheckPingAsync()
    {
        _logger.LogCheckPing(_session.Session.RemoteEndPoint);

        var pingId = Interlocked.Increment(ref _lastPingId) - 1;
        var ping = new Ping
        {
            From = _selfId,
            To = _targetId,
            SendTime = DateTime.Now.Ticks,
            SeqId = pingId,
            Ttl = 32
        };

        await _session.Dispatcher.SendAsync(_session.Session, ping);

        _logger.LogSendPing(_session.Session.RemoteEndPoint);

        await TaskHelper.WaitUntilAsync(() => _pongPackets.ContainsKey(pingId));

        var result = int.MaxValue;
        if (_pongPackets.TryRemove(pingId, out var receivedPong))
            result = TimeSpan.FromTicks(receivedPong.SelfReceiveTime - ping.SendTime).Milliseconds;

        _logger.LogPingResult(_session.Session.RemoteEndPoint, result);

        return result;
    }
}

internal static partial class PingCheckerLoggers
{
    [LoggerMessage(LogLevel.Debug, "[PING_CHECKER] Ping received from {From}, receive time: {ReceiveTime}")]
    public static partial void LogPingReceived(this ILogger logger, IPEndPoint? from, long receiveTime);

    [LoggerMessage(LogLevel.Debug, "[PING_CHECKER] Send Pong to {To}")]
    public static partial void LogSendPong(this ILogger logger, IPEndPoint? to);

    [LoggerMessage(LogLevel.Debug, "[PING_CHECKER] Pong received from {From}")]
    public static partial void LogPongReceived(this ILogger logger, IPEndPoint? from);

    [LoggerMessage(LogLevel.Debug, "[PING_CHECKER] Check ping to {To}")]
    public static partial void LogCheckPing(this ILogger logger, IPEndPoint? to);

    [LoggerMessage(LogLevel.Debug, "[PING_CHECKER] Send Ping to {To}")]
    public static partial void LogSendPing(this ILogger logger, IPEndPoint? to);

    [LoggerMessage(LogLevel.Debug, "[PING_CHECKER] Ping to {To} result: {Result}")]
    public static partial void LogPingResult(this ILogger logger, IPEndPoint? to, int result);
}