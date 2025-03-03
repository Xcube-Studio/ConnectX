using System.Buffers;
using System.Collections.Concurrent;
using System.Net.Sockets;
using ConnectX.Client.Proxy.Message;
using ConnectX.Shared.Helpers;
using Microsoft.Extensions.Logging;

namespace ConnectX.Client.Proxy;

public abstract class GenericProxyBase : IDisposable
{
    private const int RetryInterval = 500;
    private const int TryTime = 20;

    private readonly CancellationTokenSource _combinedTokenSource;
    private readonly CancellationTokenSource _internalTokenSource;

    protected readonly CancellationToken CancellationToken;
    protected readonly ConcurrentQueue<ForwardPacketCarrier> InwardBuffersQueue = [];
    protected readonly ConcurrentQueue<ForwardPacketCarrier> OutwardBuffersQueue = [];

    public readonly List<Func<ForwardPacketCarrier, bool>> OutwardSenders = [];
    public readonly TunnelIdentifier TunnelIdentifier;
    private Socket? _innerSocket;

    protected readonly ILogger Logger;

    protected GenericProxyBase(
        TunnelIdentifier tunnelIdentifier,
        CancellationToken cancellationToken,
        ILogger<GenericProxyBase> logger)
    {
        TunnelIdentifier = tunnelIdentifier;
        _internalTokenSource = new CancellationTokenSource();
        _combinedTokenSource =
            CancellationTokenSource.CreateLinkedTokenSource(_internalTokenSource.Token, cancellationToken);
        CancellationToken = _combinedTokenSource.Token;
        Logger = logger;
    }

    private ushort LocalServerPort => TunnelIdentifier.LocalRealPort;
    private ushort RemoteClientPort => TunnelIdentifier.RemoteRealPort;

    public void Dispose()
    {
        _internalTokenSource.Cancel();
        _combinedTokenSource.Dispose();
        _internalTokenSource.Dispose();

        InwardBuffersQueue.Clear();
        OutwardBuffersQueue.Clear();
        OutwardSenders.Clear();

        _innerSocket?.Shutdown(SocketShutdown.Both);
        _innerSocket?.Close();
        _innerSocket?.Dispose();

        Logger.LogProxyDisposed(GetProxyInfoForLog(), LocalServerPort);

        GC.SuppressFinalize(this);
    }

    public event Action<TunnelIdentifier, GenericProxyBase>? OnRealServerConnected;
    public event Action<TunnelIdentifier, GenericProxyBase>? OnRealServerDisconnected;

    public virtual void Start()
    {
        Logger.LogStartingProxy(GetProxyInfoForLog());

        OuterSendLoopAsync(CancellationToken).Forget();

        Task.Run(async () => await InnerSendLoopAsync(CancellationToken), CancellationToken).Forget();
        Task.Run(async () => await InnerReceiveLoopAsync(CancellationToken), CancellationToken).Forget();

        Logger.LogProxyStarted(GetProxyInfoForLog());
    }

    protected async Task OuterSendLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (OutwardBuffersQueue.IsEmpty)
                await TaskHelper.WaitUntilAsync(() => !OutwardBuffersQueue.IsEmpty, cancellationToken);
            if (!OutwardBuffersQueue.TryDequeue(out var buffer))
                continue;

            if (Environment.TickCount - buffer.LastTryTime < RetryInterval)
            {
                OutwardBuffersQueue.Enqueue(buffer);
                continue;
            }

            var sent = false;

            buffer.LastTryTime = Environment.TickCount;

            foreach (var sender in OutwardSenders)
            {
                if (!sender(buffer)) continue;
                sent = true;
                break;
            }

            if (sent) continue;

            // If all return false, it means that it has not been sent.
            // If buffer.TryCount greater tha const value TryTime, drop it.
            buffer.TryCount++;

            if (buffer.TryCount > TryTime) continue;

            // Re-enqueue.
            OutwardBuffersQueue.Enqueue(buffer);
        }
    }

    /// <summary>
    ///     需要在GenericProxyManager里调用
    /// </summary>
    /// <param name="message"></param>
    public void OnReceiveMcPacketCarrier(ForwardPacketCarrier message)
    {
        Logger.LogReceivedPacket(GetProxyInfoForLog(), message.Payload.Length, RemoteClientPort);

        InwardBuffersQueue.Enqueue(message);
    }

    protected virtual object GetProxyInfoForLog()
    {
        return new
        {
            Type = "Client",
            LocalMcPort = LocalServerPort
        };
    }

    protected virtual async Task InnerSendLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                SpinWait.SpinUntil(() => !InwardBuffersQueue.IsEmpty);
                if (!CheckSocketValid()) continue;

                try
                {
                    while (InwardBuffersQueue.TryDequeue(out var packet))
                    {
                        Logger.LogCurrentlyRemainPacket(GetProxyInfoForLog(), InwardBuffersQueue.Count);

                        var totalLen = packet.Payload.Length;
                        var sentLen = 0;
                        var buffer = packet.Payload;

                        while (sentLen < totalLen)
                            sentLen += await _innerSocket!.SendAsync(
                                buffer[sentLen..totalLen],
                                SocketFlags.None,
                                CancellationToken);

                        Logger.LogSentPacket(GetProxyInfoForLog(), totalLen, LocalServerPort);
                    }
                }
                catch (SocketException ex)
                {
                    Logger.LogFailedToSendPacket(ex, GetProxyInfoForLog(), LocalServerPort);
                }
                catch (ObjectDisposedException ex)
                {
                    Logger.LogFailedToSendPacket(ex, GetProxyInfoForLog(), LocalServerPort);
                }
            }
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
            throw;
        }
    }

    private bool CheckSocketValid()
    {
        lock (this)
        {
            if (_innerSocket is { Connected: true }) return true;
            try
            {
                InitConnectionSocket();
                return true;
            }
            catch (SocketException e) //无法初始化，清除队列
            {
                InwardBuffersQueue.Clear();
                Logger.LogFailedToInitConnectionSocket(e, GetProxyInfoForLog(), e.SocketErrorCode);

                return false;
            }
            catch (ObjectDisposedException)
            {
                InwardBuffersQueue.Clear();
                return false;
            }
        }
    }

    private void InitConnectionSocket()
    {
        if (_innerSocket is { Connected: true })
            _innerSocket.Close();

        _innerSocket?.Dispose();
        _innerSocket = CreateSocket();
    }

    protected abstract Socket CreateSocket();

    protected virtual async Task InnerReceiveLoopAsync(CancellationToken cancellationToken)
    {
        using var bufferOwner = MemoryPool<byte>.Shared.Rent(1024);
        var buffer = bufferOwner.Memory;

        while (!cancellationToken.IsCancellationRequested)
        {
            if (_innerSocket is not { Connected: true })
            {
                if (!CheckSocketValid()) continue;
                Thread.Sleep(1);
                continue;
            }

            var len = await _innerSocket.ReceiveAsync(buffer, SocketFlags.None, CancellationToken);

            if (len == 0)
            {
                Logger.LogReceivedZeroBytes(GetProxyInfoForLog(), LocalServerPort);
                Logger.LogServerDisconnected(GetProxyInfoForLog(), LocalServerPort);

                _innerSocket?.Dispose();
                _innerSocket = null;
                InvokeRealServerDisconnected();
                break;
            }

            Logger.LogBytesReceived(GetProxyInfoForLog(), len, LocalServerPort);

            var carrier = new ForwardPacketCarrier
            {
                Payload = buffer[..len].ToArray(),
                LastTryTime = 0,
                TryCount = 0,
                SelfRealPort = LocalServerPort,
                TargetRealPort = RemoteClientPort
            };

            OutwardBuffersQueue.Enqueue(carrier);
        }
    }

    protected void InvokeRealServerDisconnected()
    {
        OnRealServerDisconnected?.Invoke(TunnelIdentifier, this);
    }

    protected void InvokeRealServerConnected()
    {
        OnRealServerConnected?.Invoke(TunnelIdentifier, this);
    }
}

internal static partial class GenericProxyBaseLoggers
{
    [LoggerMessage(LogLevel.Information, "[PROXY] Starting proxy: {ProxyInfo}")]
    public static partial void LogStartingProxy(this ILogger logger, object proxyInfo);

    [LoggerMessage(LogLevel.Information, "[PROXY] Proxy started: {ProxyInfo}")]
    public static partial void LogProxyStarted(this ILogger logger, object proxyInfo);

    [LoggerMessage(LogLevel.Trace, "[{ProxyInfo}] Received packet with length [{Length}] from {RemoteClientPort}")]
    public static partial void LogReceivedPacket(this ILogger logger, object proxyInfo, int length,
        ushort remoteClientPort);

    [LoggerMessage(LogLevel.Trace, "[{ProxyInfo}] Currently remain {PacketLength} packet")]
    public static partial void LogCurrentlyRemainPacket(this ILogger logger, object proxyInfo, int packetLength);

    [LoggerMessage(LogLevel.Trace, "[{ProxyInfo}] Sent {PacketLength} bytes to {LocalRealMcPort}")]
    public static partial void LogSentPacket(this ILogger logger, object proxyInfo, int packetLength,
        ushort localRealMcPort);

    [LoggerMessage(LogLevel.Error, "[{ProxyInfo}] Failed to send packet to {LocalRealMcPort}")]
    public static partial void LogFailedToSendPacket(this ILogger logger, Exception ex, object proxyInfo,
        ushort localRealMcPort);

    [LoggerMessage(LogLevel.Error, "[{ProxyInfo}] Failed to init connection socket, error code: {ErrorCode}")]
    public static partial void LogFailedToInitConnectionSocket(this ILogger logger, Exception ex, object proxyInfo,
        SocketError errorCode);

    [LoggerMessage(LogLevel.Error, "[{ProxyInfo}] Received 0 bytes from {LocalRealMcPort}")]
    public static partial void LogReceivedZeroBytes(this ILogger logger, object proxyInfo, ushort localRealMcPort);

    [LoggerMessage(LogLevel.Trace, "[{ProxyInfo}] Received {Len} bytes from {McPort}")]
    public static partial void LogBytesReceived(this ILogger logger, object proxyInfo, int len, ushort mcPort);

    [LoggerMessage(LogLevel.Error, "[{ProxyInfo}] Server {LocalRealMcPort} disconnected")]
    public static partial void LogServerDisconnected(this ILogger logger, object proxyInfo, ushort localRealMcPort);

    [LoggerMessage(LogLevel.Information, "[{ProxyInfo}] Proxy disposed, local port: {LocalPort}")]
    public static partial void LogProxyDisposed(this ILogger logger, object proxyInfo, ushort localPort);
}