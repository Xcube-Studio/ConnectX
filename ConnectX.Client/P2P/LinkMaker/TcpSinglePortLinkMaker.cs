using System.Net;
using System.Net.Sockets;
using Hive.Common.Shared.Helpers;
using Hive.Network.Abstractions.Session;
using Hive.Network.Tcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ConnectX.Client.P2P.LinkMaker;

public class TcpSinglePortLinkMaker(
    long startTimeTick,
    Guid partnerId,
    ushort localPort,
    IPEndPoint remoteIpe,
    CancellationToken cancellationToken,
    IServiceProvider serviceProvider,
    ILogger<TcpSinglePortLinkMaker> logger)
    : SingleToSingleLinkMaker(serviceProvider, logger, startTimeTick, partnerId, localPort, remoteIpe,
        cancellationToken)
{
    public override async Task<ISession?> BuildLinkAsync()
    {
        TcpSession? link = null;
        var receiveSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        receiveSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

        Socket conSocket;
        await Task.Run(async () =>
        {
            receiveSocket.Bind(new IPEndPoint(IPAddress.Any, LocalPort));
            var tryTime = 10;

            Logger.LogStartedToTryTcpConnectionWithRemoteIpe(LocalPort, RemoteIpe);
            Logger.LogStartTime(new DateTime(StartTimeTick).ToLongTimeString());

            await TaskHelper.WaitUtil(() => !Token.IsCancellationRequested &&
                                            DateTime.UtcNow.Ticks < StartTimeTick, Token);

            while (!Token.IsCancellationRequested &&
                   tryTime > 0)
            {
                try
                {
                    tryTime--;

                    await receiveSocket.ConnectAsync(RemoteIpe, Token);

                    conSocket = receiveSocket;
                    link = ActivatorUtilities.CreateInstance<TcpSession>(ServiceProvider,
                        0, conSocket);

                    if (link == null)
                        throw new SocketException((int)SocketError.Fault, "Link is null");

                    InvokeOnConnected(link);
                }
                catch (SocketException e)
                {
                    Logger.LogFailedToConnectToRemoteIpe(e, LocalPort, RemoteIpe, tryTime);

                    if (tryTime == 0)
                    {
                        Logger.LogFailedToConnectToRemoteIpe(RemoteIpEndPoint);
                        InvokeOnFailed();

                        break;
                    }

                    continue;
                }

                Logger.LogSucceedToConnectToRemoteIpe(LocalPort, RemoteIpe);
                break;
            }
        }, Token);

        return link;
    }
}

internal static partial class TcpSinglePortLinkMakerLoggers
{
    [LoggerMessage(LogLevel.Information, "[TCP_S2S] {LocalPort} Started to try TCP connection with {RemoteIpe}")]
    public static partial void LogStartedToTryTcpConnectionWithRemoteIpe(this ILogger logger, int localPort,
        IPEndPoint remoteIpe);

    [LoggerMessage(LogLevel.Information, "[TCP_S2S] Start time {DateTime}")]
    public static partial void LogStartTime(this ILogger logger, string dateTime);

    [LoggerMessage(LogLevel.Error,
        "[TCP_S2S] {LocalPort} Failed to connect with {RemoteIpe}, remaining try time {TryTime}")]
    public static partial void LogFailedToConnectToRemoteIpe(this ILogger logger, Exception ex, int localPort,
        IPEndPoint remoteIpe, int tryTime);

    [LoggerMessage(LogLevel.Error,
        "[TCP_S2S] Failed to connect with {RemoteIpe}, maybe the network is special, or the other party has dropped")]
    public static partial void LogFailedToConnectToRemoteIpe(this ILogger logger, IPEndPoint remoteIpe);

    [LoggerMessage(LogLevel.Information, "[TCP_S2S] {LocalPort} Succeed to connect with {RemoteIpe}")]
    public static partial void LogSucceedToConnectToRemoteIpe(this ILogger logger, int localPort, IPEndPoint remoteIpe);
}