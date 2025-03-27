using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace ConnectX.Client.Proxy;

public class GenericProxyAcceptor : IDisposable
{
    private readonly CancellationToken _cancellationToken;
    private readonly Guid _id;
    private readonly ILogger _logger;

    private Socket? _acceptSocket;
    private bool _socketAcceptLoopIsRunning;

    public bool IsRunning => _socketAcceptLoopIsRunning;

    public GenericProxyAcceptor(
        Guid id,
        ushort remoteRealPort,
        ushort fakePort,
        CancellationToken cancellationToken,
        ILogger<GenericProxyAcceptor> logger)
    {
        _logger = logger;
        _id = id;
        RemoteRealPort = remoteRealPort;
        LocalMappingPort = fakePort;
        _cancellationToken = cancellationToken;
    }

    public ushort LocalMappingPort { get; }
    private ushort RemoteRealPort { get; }

    public void Dispose()
    {
        _acceptSocket?.Dispose();

        _logger.LogProxyAcceptorDisposed(_id, LocalMappingPort, RemoteRealPort, GetProxyInfoForLog());
    }

    public event Action<GenericProxyAcceptor, Socket>? OnRealClientConnected;

    public Task StartAcceptAsync()
    {
        if (_socketAcceptLoopIsRunning) return Task.CompletedTask;

        return Task.Run(async () =>
        {
            try
            {
                _acceptSocket = new Socket(AddressFamily.InterNetwork,
                    SocketType.Stream, ProtocolType.Tcp);

                var ipe = new IPEndPoint(IPAddress.Any, LocalMappingPort);

                _acceptSocket.Bind(ipe);
                _acceptSocket.Listen(1000);
                _socketAcceptLoopIsRunning = true;

                while (!_cancellationToken.IsCancellationRequested)
                {
                    var tmp = await _acceptSocket.AcceptAsync(_cancellationToken);

                    tmp.NoDelay = true;

                    if (tmp.RemoteEndPoint is not IPEndPoint remoteEndPoint) continue;

                    var clientPort = remoteEndPoint.Port;

                    _logger.LogClientConnected(_id, LocalMappingPort, RemoteRealPort, (ushort)clientPort,
                        GetProxyInfoForLog());

                    InvokeOnClientConnected(tmp);
                }
            }
            catch (SocketException e)
            {
                _logger.LogSocketError(e, _id, LocalMappingPort, RemoteRealPort, GetProxyInfoForLog());
            }
            finally
            {
                _socketAcceptLoopIsRunning = false;
            }
        }, _cancellationToken);
    }

    private object GetProxyInfoForLog()
    {
        return new
        {
            Type = "Acceptor",
            FakePort = LocalMappingPort,
            RemteRealPort = RemoteRealPort
        };
    }

    private void InvokeOnClientConnected(Socket socket)
    {
        OnRealClientConnected?.Invoke(this, socket);
    }
}

internal static partial class GenericProxyAcceptorLoggers
{
    [LoggerMessage(LogLevel.Information,
        "[PROXY_ACCEPTOR] Client connected. (Id: {Id}), Mapping: {FakePort} -> {RemoteRealPort}, ClientPort: {ClientPort}, ProxyInfo: {ProxyInfo}")]
    public static partial void LogClientConnected(this ILogger logger, Guid id, ushort fakePort, ushort remoteRealPort,
        ushort clientPort, object proxyInfo);

    [LoggerMessage(LogLevel.Information,
        "[PROXY_ACCEPTOR] Proxy acceptor disposed. (Id: {Id}), Mapping: {FakePort} -> {RemoteRealPort}, ProxyInfo: {ProxyInfo}")]
    public static partial void LogProxyAcceptorDisposed(this ILogger logger, Guid id, ushort fakePort,
        ushort remoteRealPort, object proxyInfo);

    [LoggerMessage(LogLevel.Error,
        "[PROXY_ACCEPTOR] Socket error. (Id: {Id}), Mapping: {FakePort} -> {RemoteRealPort}, ProxyInfo: {ProxyInfo}")]
    public static partial void LogSocketError(this ILogger logger, Exception ex, Guid id, ushort fakePort,
        ushort remoteRealPort, object proxyInfo);
}