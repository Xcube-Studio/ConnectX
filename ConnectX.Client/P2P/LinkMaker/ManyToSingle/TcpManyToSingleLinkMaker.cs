using System.Net;
using System.Net.Sockets;
using Hive.Common.Shared.Helpers;
using Hive.Network.Abstractions.Session;
using Hive.Network.Tcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ConnectX.Client.P2P.LinkMaker.ManyToSingle;

public class TcpManyToSingleLinkMaker(
    long startTimeTick,
    Guid partnerId,
    IPEndPoint remoteIpe,
    int[] selfPredictPorts,
    CancellationToken cancellationToken,
    IServiceProvider serviceProvider,
    ILogger<TcpManyToSingleLinkMaker> logger)
    : ManyToSinglePortLinkMaker(serviceProvider, logger, startTimeTick, partnerId, remoteIpe, selfPredictPorts,
        cancellationToken)
{
    public override async Task<ISession?> BuildLinkAsync()
    {
        var handshakeTokenSource = new CancellationTokenSource();
        TcpSession? link = null;

        await Task.Run(
            () =>
            {
                List<Task> allConnectTasks = [];
                foreach (var port in SelfPredictPorts)
                {
                    Socket conSocket;

                    var connectTask = Task.Run(async () =>
                    {
                        var receiveSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

                        receiveSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                        receiveSocket.Bind(new IPEndPoint(IPAddress.Any, port));

                        var tryTime = 100;

                        Logger.LogStartedToTryTcpConnectionWithRemoteIpe(port, RemoteIpEndPoint!);
                        Logger.LogStartTime(new DateTime(StartTimeTick).ToLongTimeString());

                        await TaskHelper.WaitUtil(() => !handshakeTokenSource.IsCancellationRequested &&
                                                        DateTime.UtcNow.Ticks < StartTimeTick,
                            handshakeTokenSource.Token);

                        var succeedThisConnect = false;

                        while (!handshakeTokenSource.IsCancellationRequested &&
                               tryTime > 0)
                        {
                            tryTime--;

                            try
                            {
                                await receiveSocket.ConnectAsync(RemoteIpEndPoint!, handshakeTokenSource.Token);
                                await handshakeTokenSource.CancelAsync();

                                conSocket = receiveSocket;
                                link = ActivatorUtilities.CreateInstance<TcpSession>(
                                    ServiceProvider,
                                    0, conSocket);

                                if (link == null)
                                    throw new SocketException((int)SocketError.Fault, "Link is null");

                                InvokeOnConnected(link);

                                succeedThisConnect = true;
                            }
                            catch (SocketException e)
                            {
                                Logger.LogFailedToConnectToRemoteIpe(e, port, RemoteIpEndPoint!, tryTime);

                                if (tryTime == 0)
                                {
                                    Logger.LogFailedToConnectToRemoteIpe(port, RemoteIpEndPoint!);
                                    break;
                                }

                                continue;
                            }

                            Logger.LogConnectedToRemoteIpe(port, RemoteIpEndPoint!);
                            break;
                        }

                        if (!succeedThisConnect)
                            Logger.LogFailedToConnectToRemoteIpe(port, remoteIpe);
                    }, handshakeTokenSource.Token);

                    allConnectTasks.Add(connectTask);
                }

                Task.WaitAll([.. allConnectTasks], handshakeTokenSource.Token);
            }, handshakeTokenSource.Token
        );

        return link;
    }
}

internal static partial class TcpManyToSingleLinkMakerLoggers
{
    [LoggerMessage(LogLevel.Information, "[TCP_M2S] {LocalPort} Started to try TCP connection with {RemoteIpe}")]
    public static partial void LogStartedToTryTcpConnectionWithRemoteIpe(this ILogger logger, int localPort,
        IPEndPoint remoteIpe);

    [LoggerMessage(LogLevel.Information, "[TCP_M2S] Start time {DateTime}")]
    public static partial void LogStartTime(this ILogger logger, string dateTime);

    [LoggerMessage(LogLevel.Error,
        "[TCP_M2S] {LocalPort} Failed to connect {RemoteEndPoint}, remaining try time {TryTime}")]
    public static partial void LogFailedToConnectToRemoteIpe(this ILogger logger, Exception ex, int localPort,
        IPEndPoint remoteEndPoint, int tryTime);

    [LoggerMessage(LogLevel.Error, "[TCP_M2S] {LocalPort} Failed to connect {RemoteIpEndPoint}")]
    public static partial void LogFailedToConnectToRemoteIpe(this ILogger logger, int localPort,
        IPEndPoint remoteIpEndPoint);

    [LoggerMessage(LogLevel.Information, "[TCP_M2S] {LocalPort} Connected to {RemoteIpEndPoint}")]
    public static partial void LogConnectedToRemoteIpe(this ILogger logger, int localPort, IPEndPoint remoteIpEndPoint);
}