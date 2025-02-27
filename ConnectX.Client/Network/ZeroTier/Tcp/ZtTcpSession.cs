using Hive.Network.Shared;
using Hive.Network.Shared.Session;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Sockets;
using Socket = ZeroTier.Sockets.Socket;

namespace ConnectX.Client.Network.ZeroTier.Tcp;

public sealed class ZtTcpSession : AbstractSession
{
    public ZtTcpSession(
        int sessionId,
        Socket socket,
        ILogger<ZtTcpSession> logger)
        : base(sessionId, logger)
    {
        Socket = socket;
        socket.SendBufferSize = NetworkSettings.DefaultSocketBufferSize;
        socket.ReceiveBufferSize = NetworkSettings.DefaultSocketBufferSize;
    }

    public Socket? Socket { get; private set; }

    public override IPEndPoint? LocalEndPoint => Socket?.LocalEndPoint as IPEndPoint;

    public override IPEndPoint? RemoteEndPoint => Socket?.RemoteEndPoint as IPEndPoint;

    public override bool CanSend => IsConnected;

    public override bool CanReceive => IsConnected;

    public override bool IsConnected => Socket is { Connected: true };

    public event EventHandler<SocketError>? OnSocketError;

    public override ValueTask<int> SendOnce(ArraySegment<byte> data, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(Socket);

        Logger.LogInformation("Send Once! {length}", data.Count);

        var len = Socket.Send(data.ToArray());

        if (len == 0)
            OnSocketError?.Invoke(this, SocketError.ConnectionReset);

        return ValueTask.FromResult(len);
    }

    public override ValueTask<int> ReceiveOnce(ArraySegment<byte> buffer, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(Socket);

        var len = Socket.Receive(buffer.Array);

        Logger.LogInformation("Receive Once! {length}", len);

        return ValueTask.FromResult(len);
    }

    public override void Close()
    {
        IsConnected = false;
        Socket?.Close();
        Socket = null;
    }
}