using Hive.Network.Shared;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Sockets;
using Hive.Network.Shared.Session;
using Socket = ZeroTier.Sockets.Socket;
using System.IO.Pipelines;
using System.Runtime.InteropServices;

namespace ConnectX.Client.Network.ZeroTier.Tcp;

public sealed class ZtTcpSession : AbstractSession
{
    private bool _closed;
    private readonly bool _isAcceptedSocket;

    public ZtTcpSession(
        int sessionId,
        bool isAcceptedSocket,
        Socket socket,
        ILogger<ZtTcpSession> logger)
        : base(sessionId, logger)
    {
        _isAcceptedSocket = isAcceptedSocket;

        Socket = socket;
        socket.SendBufferSize = NetworkSettings.DefaultSocketBufferSize;
        socket.ReceiveBufferSize = NetworkSettings.DefaultSocketBufferSize;
    }

    public Socket? Socket { get; private set; }

    public override IPEndPoint? LocalEndPoint => Socket?.LocalEndPoint as IPEndPoint;

    public override IPEndPoint? RemoteEndPoint => Socket?.RemoteEndPoint as IPEndPoint;

    public override bool CanSend => IsConnected;

    public override bool CanReceive => IsConnected;

    public override bool IsConnected => (_isAcceptedSocket && !_closed) || Socket is { Connected: true };

    public event EventHandler<SocketError>? OnSocketError;

    public override ValueTask<int> SendOnce(ArraySegment<byte> data, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(Socket);

        var len = Socket.Send([.. data]);

        if (len == 0)
            OnSocketError?.Invoke(this, SocketError.ConnectionReset);

        return ValueTask.FromResult(len);
    }

    protected override async Task FillReceivePipeAsync(PipeWriter writer, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(Socket);

        while (!token.IsCancellationRequested)
        {
            if (!Socket.Poll(100, SelectMode.SelectRead))
            {
                await Task.Delay(10, token);
                continue;
            }

            var memory = writer.GetMemory(NetworkSettings.DefaultBufferSize);

            if (!MemoryMarshal.TryGetArray<byte>(memory, out var segment))
                throw new InvalidOperationException(
                    "Failed to create ArraySegment<byte> from ReadOnlyMemory<byte>!");

            var receiveLen = await ReceiveOnce(segment, token);

            if (receiveLen == 0) break;
            if (receiveLen == -1)
            {
                // Payload is not ready yet, wait for a while and try again
                await Task.Delay(10, token);
                continue;
            }

            Logger.LogDataReceived(RemoteEndPoint!, receiveLen);

            writer.Advance(receiveLen);

            var flushResult = await writer.FlushAsync(token);

            if (flushResult.IsCompleted) break;
        }
    }

    public override ValueTask<int> ReceiveOnce(ArraySegment<byte> buffer, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(Socket);

        var len = Socket.Receive(buffer.Array);

        return ValueTask.FromResult(len);
    }

    public override void Close()
    {
        _closed = true;
        IsConnected = false;
        Socket?.Close();
        Socket = null;
    }
}

internal static partial class ZtTcpSessionLoggers
{
    [LoggerMessage(LogLevel.Trace, "Payload received from [{endPoint}] with length [{length}]")]
    public static partial void LogDataReceived(this ILogger logger, IPEndPoint endPoint, int length);
}