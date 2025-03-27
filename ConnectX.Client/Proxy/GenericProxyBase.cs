using System.Buffers;
using System.Diagnostics;
using System.Net.Sockets;
using System.Threading.Channels;
using ConnectX.Client.Messages.Proxy;
using Microsoft.Extensions.Logging;

namespace ConnectX.Client.Proxy;

public abstract class GenericProxyBase : IDisposable
{
    private const int DefaultReceiveBufferSize = 20480;
    private const int RetryInterval = 500;
    private const int TryTime = 20;

    private readonly CancellationTokenSource _combinedTokenSource;
    private readonly CancellationTokenSource _internalTokenSource;

    protected readonly CancellationToken CancellationToken;

    protected Channel<ForwardPacketCarrier> InwardBuffersQueue = Channel.CreateUnbounded<ForwardPacketCarrier>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });

    protected Channel<ForwardPacketCarrier> OutwardBuffersQueue = Channel.CreateUnbounded<ForwardPacketCarrier>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });

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

    private void ResetChannels()
    {
        InwardBuffersQueue = Channel.CreateUnbounded<ForwardPacketCarrier>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        OutwardBuffersQueue = Channel.CreateUnbounded<ForwardPacketCarrier>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
    }

    public void Dispose()
    {
        _internalTokenSource.Cancel();
        _combinedTokenSource.Dispose();
        _internalTokenSource.Dispose();

        InwardBuffersQueue.Writer.Complete();
        OutwardBuffersQueue.Writer.Complete();
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

        Hive.Common.Shared.Helpers.TaskHelper.FireAndForget(() => OuterSendLoopAsync(CancellationToken));
        Hive.Common.Shared.Helpers.TaskHelper.FireAndForget(() => InnerSendLoopAsync(CancellationToken));
        Hive.Common.Shared.Helpers.TaskHelper.FireAndForget(() => InnerReceiveLoopAsync(CancellationToken));

        Logger.LogProxyStarted(GetProxyInfoForLog());
    }

    protected async Task OuterSendLoopAsync(CancellationToken cancellationToken)
    {
        var reader = OutwardBuffersQueue.Reader;
        var writer = OutwardBuffersQueue.Writer;

        while (!cancellationToken.IsCancellationRequested)
        {
            while (await reader.WaitToReadAsync(cancellationToken))
            {
                while (reader.TryRead(out var packetCarrier))
                {
                    if (Environment.TickCount - packetCarrier.LastTryTime < RetryInterval)
                    {
                        ArgumentOutOfRangeException.ThrowIfEqual(writer.TryWrite(packetCarrier), false);
                        continue;
                    }

                    var sent = false;

                    packetCarrier.LastTryTime = Environment.TickCount;

                    foreach (var sender in OutwardSenders)
                    {
                        if (!sender(packetCarrier)) continue;
                        sent = true;
                        break;
                    }

                    if (sent)
                    {
                        packetCarrier.Dispose();
                        continue;
                    }

                    // If all return false, it means that it has not been sent.
                    // If buffer.TryCount greater tha const value TryTime, drop it.
                    packetCarrier.TryCount++;

                    if (packetCarrier.TryCount > TryTime)
                    {
                        packetCarrier.Dispose();
                        continue;
                    }

                    // Re-enqueue.
                    ArgumentOutOfRangeException.ThrowIfEqual(writer.TryWrite(packetCarrier), false);
                }
            }
        }
    }

    /// <summary>
    ///     需要在GenericProxyManager里调用
    /// </summary>
    /// <param name="message"></param>
    public void OnReceiveMcPacketCarrier(ForwardPacketCarrier message)
    {
        Logger.LogReceivedPacket(GetProxyInfoForLog(), message.Payload.Length, RemoteClientPort);

        ArgumentOutOfRangeException.ThrowIfEqual(InwardBuffersQueue.Writer.TryWrite(message), false);
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
        var reader = InwardBuffersQueue.Reader;

        while (!cancellationToken.IsCancellationRequested)
        {
            if (!CheckSocketValid()) continue;

            while (await reader.WaitToReadAsync(cancellationToken))
            {
                while (reader.TryRead(out var packetCarrier))
                {
                    try
                    {
                        Logger.LogCurrentlyRemainPacket(GetProxyInfoForLog(), reader.Count);

                        var totalLen = packetCarrier.Payload.Length;
                        var sentLen = 0;
                        var buffer = packetCarrier.Payload;

                        while (sentLen < totalLen)
                            sentLen += await _innerSocket!.SendAsync(
                                buffer[sentLen..],
                                SocketFlags.None,
                                CancellationToken);

                        packetCarrier.Dispose();

                        Logger.LogSentPacket(GetProxyInfoForLog(), totalLen, LocalServerPort);
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
                InwardBuffersQueue.Writer.Complete();
                ResetChannels();
                Logger.LogFailedToInitConnectionSocket(e, GetProxyInfoForLog(), e.SocketErrorCode);

                return false;
            }
            catch (ObjectDisposedException)
            {
                InwardBuffersQueue.Writer.Complete();
                ResetChannels();
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
        while (!cancellationToken.IsCancellationRequested)
        {
            if (_innerSocket is not { Connected: true })
            {
                if (!CheckSocketValid()) continue;
                await Task.Delay(1, cancellationToken);
                continue;
            }

            if (!_innerSocket.Poll(1000, SelectMode.SelectRead))
            {
                await Task.Delay(1, cancellationToken);
                continue;
            }

            var bufferOwner = MemoryPool<byte>.Shared.Rent(DefaultReceiveBufferSize);
            var buffer = bufferOwner.Memory;

            var startTime = Stopwatch.GetTimestamp();

            var len = await _innerSocket.ReceiveAsync(buffer, SocketFlags.None, CancellationToken);

            if (len == 0)
            {
                Logger.LogReceivedZeroBytes(GetProxyInfoForLog(), LocalServerPort);
                Logger.LogServerDisconnected(GetProxyInfoForLog(), LocalServerPort);

                bufferOwner.Dispose();

                _innerSocket?.Shutdown(SocketShutdown.Both);
                _innerSocket?.Dispose();
                _innerSocket = null;

                InvokeRealServerDisconnected();
                break;
            }

            Logger.LogCritical("[InnerReceiveLoop] {time:F} ms", Stopwatch.GetElapsedTime(startTime).TotalMilliseconds);

            Logger.LogBytesReceived(GetProxyInfoForLog(), len, LocalServerPort);

            var carrier = new ForwardPacketCarrier
            {
                PayloadOwner = bufferOwner,
                Payload = buffer[..len],
                LastTryTime = 0,
                TryCount = 0,
                SelfRealPort = LocalServerPort,
                TargetRealPort = RemoteClientPort
            };

            ArgumentOutOfRangeException.ThrowIfEqual(OutwardBuffersQueue.Writer.TryWrite(carrier), false);
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