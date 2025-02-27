using Hive.Network.Shared;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Sockets;
using Hive.Network.Shared.Session;
using Socket = ZeroTier.Sockets.Socket;

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

        var len = Socket.Send(data.ToArray());

        if (len == 0)
            OnSocketError?.Invoke(this, SocketError.ConnectionReset);

        return ValueTask.FromResult(len);
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