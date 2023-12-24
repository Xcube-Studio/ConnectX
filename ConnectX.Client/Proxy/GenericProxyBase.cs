﻿using System.Buffers;
using System.Collections.Concurrent;
using System.Net.Sockets;
using ConnectX.Client.Proxy.Message;
using ConnectX.Shared.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.Threading;

namespace ConnectX.Client.Proxy;

public abstract class GenericProxyBase : IDisposable
{
    private const int RetryInterval = 500;
    private const int TryTime = 20;
    
    private readonly CancellationTokenSource _combinedTokenSource;
    private readonly CancellationTokenSource _internalTokenSource;
    
    protected readonly CancellationToken CancellationToken;
    protected readonly ConcurrentQueue<ForwardPacketCarrier> InwardBuffersQueue = [];
    protected readonly ILogger Logger;
    protected readonly ConcurrentQueue<ForwardPacketCarrier> OutwardBuffersQueue = [];

    public readonly List<Func<ForwardPacketCarrier, bool>> OutwardSenders = [];
    public readonly TunnelIdentifier TunnelIdentifier;
    private Socket? _innerSocket;
    
    public event Action<TunnelIdentifier, GenericProxyBase>? OnRealServerConnected;
    public event Action<TunnelIdentifier, GenericProxyBase>? OnRealServerDisconnected;

    protected GenericProxyBase(
        TunnelIdentifier tunnelIdentifier,
        CancellationToken cancellationToken,
        ILogger<GenericProxyBase> logger)
    {
        TunnelIdentifier = tunnelIdentifier;
        _internalTokenSource = new CancellationTokenSource();
        _combinedTokenSource = CancellationTokenSource.CreateLinkedTokenSource(_internalTokenSource.Token, cancellationToken);
        CancellationToken = _combinedTokenSource.Token;
        Logger = logger;
    }
    
    private ushort LocalServerPort => TunnelIdentifier.LocalRealPort;
    private ushort RemoteClientPort => TunnelIdentifier.RemoteRealPort;

    public virtual void Start()
    {
        Logger.LogInformation("Starting proxy:{@ProxyInfo}...", GetProxyInfoForLog());
        
        TplExtensions.Forget(OuterSendLoopAsync(CancellationToken));
        TplExtensions.Forget(InnerSendLoopAsync(CancellationToken));
        TplExtensions.Forget(InnerReceiveLoopAsync(CancellationToken));

        Logger.LogInformation("Proxy:{@ProxyInfo} started", GetProxyInfoForLog());
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
        Logger.LogTrace(
            "[{ProxyInfo}] Received packet with length [{Length}] from {RemoteClientPort}",
            GetProxyInfoForLog(), message.Data.Length, RemoteClientPort);
        
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
        ArgumentNullException.ThrowIfNull(_innerSocket);
        
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
                        Logger.LogTrace(
                            "[{ProxyInfo}] Currently remain {PacketLength} packet",
                            GetProxyInfoForLog(), InwardBuffersQueue.Count);
                        
                        var totalLen = packet.Data.Length;
                        var sentLen = 0;
                        var buffer = packet.Data;
                        
                        while (sentLen < totalLen)
                            sentLen += await _innerSocket.SendAsync(buffer[sentLen..totalLen],
                                SocketFlags.None, CancellationToken);
                        
                        Logger.LogTrace(
                            "[{ProxyInfo}] Sent {PacketLength} bytes to {LocalRealMcPort}",
                            GetProxyInfoForLog(), totalLen, LocalServerPort);
                    }
                }
                catch (SocketException ex)
                {
                    Logger.LogError(
                        ex, "[{ProxyInfo}] Failed to send packet to {LocalRealMcPort}",
                        GetProxyInfoForLog(), LocalServerPort);
                }
                catch (ObjectDisposedException ex)
                {
                    Logger.LogError(
                        ex, "[{ProxyInfo}] Failed to send packet to {LocalRealMcPort}",
                        GetProxyInfoForLog(), LocalServerPort);
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
                Logger.LogError(
                    e, "[{ProxyInfo}] Failed to init connection socket, error code: {ErrorCode}",
                    GetProxyInfoForLog(), e.ErrorCode);
                
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
        var bufferOwner = MemoryPool<byte>.Shared.Rent(1024);
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
                Logger.LogError(
                    "[{ProxyInfo}] Received 0 bytes from {LocalRealMcPort}",
                    GetProxyInfoForLog(), LocalServerPort);
                Logger.LogError(
                    "[{ProxyInfo}] Server {LocalRealMcPort} disconnected",
                    GetProxyInfoForLog(), LocalServerPort);
                
                _innerSocket?.Dispose();
                _innerSocket = null;
                InvokeRealServerDisconnected();
                break;
            }
            
            Logger.LogTrace(
                "[{ProxyInfo}] Received {Len} bytes from {McPort}",
                GetProxyInfoForLog(), len, LocalServerPort);

            var carrier = new ForwardPacketCarrier
            {
                Data = buffer[..len],
                LastTryTime = 0,
                TryCount = 0,
                SelfRealPort = LocalServerPort,
                TargetRealPort = RemoteClientPort
            };
            
            OutwardBuffersQueue.Enqueue(carrier);
        }
        
        bufferOwner.Dispose();
    }

    protected void InvokeRealServerDisconnected()
    {
        OnRealServerDisconnected?.Invoke(TunnelIdentifier, this);
    }

    protected void InvokeRealServerConnected()
    {
        OnRealServerConnected?.Invoke(TunnelIdentifier, this);
    }
    
    public void Dispose()
    {
        _internalTokenSource.Cancel();
        _combinedTokenSource.Dispose();
        _internalTokenSource.Dispose();
        
        _innerSocket?.Shutdown(SocketShutdown.Both);
        _innerSocket?.Close();
        
        Logger.LogInformation(
            "[{ProxyInfo}] Proxy disposed, local port: {LocalPort}",
            GetProxyInfoForLog(), LocalServerPort);
        
        GC.SuppressFinalize(this);
    }
}