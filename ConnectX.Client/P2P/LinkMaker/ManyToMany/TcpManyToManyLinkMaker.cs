using System.Net;
using System.Net.Sockets;
using Hive.Common.Shared.Helpers;
using Hive.Network.Abstractions.Session;
using Hive.Network.Tcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ConnectX.Client.P2P.LinkMaker.ManyToMany;

public class TcpManyToManyLinkMaker(
    long startTimeTick,
    Guid partnerId,
    IPAddress targetIp,
    int[] selfPredictPorts,
    int[] targetPredictPorts,
    CancellationToken cancellationToken,
    IServiceProvider serviceProvider,
    ILogger<TcpManyToManyLinkMaker> logger)
    : ManyToManyPortLinkMaker(serviceProvider, logger, startTimeTick,
        partnerId, targetIp, selfPredictPorts, targetPredictPorts, cancellationToken)
{
    private IPEndPoint? _remoteIpEndPoint;

    public override IPEndPoint? RemoteIpEndPoint => _remoteIpEndPoint;


    public override async Task<ISession?> BuildLinkAsync()
    {
        var unusedPredictPort = new Queue<int>(TargetPredictPorts);
        var handshakeTokenSource = new CancellationTokenSource();

        TcpSession? link = null;

        await Task.Run(
            () =>
            {
                List<Task> allConnectTasks = [];
                while (unusedPredictPort.Count > 0)
                {
                    var remotePort = unusedPredictPort.Dequeue();
                    var receiveSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                    
                    receiveSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    
                    Socket conSocket;
                    
                    var connectTask = Task.Run(async () =>
                    {
                        var localPort = Shared.Helpers.NetworkHelper.GetAvailablePrivatePort();
                        var remoteIpe = new IPEndPoint(TargetIp, remotePort);
                        var tryTime = 10;
                        
                        Logger.LogStartedToTryTcpConnectionWithRemoteIpe(localPort, remoteIpe);
                        Logger.LogStartTime(new DateTime(StartTimeTick).ToLongTimeString());

                        await TaskHelper.WaitUtil(() => !handshakeTokenSource.IsCancellationRequested &&
                                                        DateTime.UtcNow.Ticks < StartTimeTick, handshakeTokenSource.Token);

                        var succeedThisConnect = false;

                        while (!handshakeTokenSource.IsCancellationRequested &&
                               tryTime > 0)
                        {
                            tryTime--;

                            try
                            {
                                await receiveSocket.ConnectAsync(remoteIpe, handshakeTokenSource.Token);
                                await handshakeTokenSource.CancelAsync();

                                conSocket = receiveSocket;
                                link = ActivatorUtilities.CreateInstance<TcpSession>(
                                    ServiceProvider,
                                    0, conSocket);

                                if (link == null)
                                    throw new SocketException((int)SocketError.Fault, "Link is null");

                                InvokeOnConnected(link);
                                succeedThisConnect = true;
                                _remoteIpEndPoint = link.RemoteEndPoint;
                                
                                Logger.LogConnectedToRemoteIpe(localPort, remoteIpe);

                                break;
                            }
                            catch (SocketException e)
                            {
                                Logger.LogFailedToConnectToRemoteIpe(e, localPort, remoteIpe, tryTime);
                                
                                if (tryTime != 0) continue;

                                Logger.LogFailedToConnectToRemoteIpe(localPort, remoteIpe);
                                break;
                            }
                        }

                        if (!succeedThisConnect)
                            Logger.LogFailedToConnectToRemoteIpe(localPort, remoteIpe);
                    }, handshakeTokenSource.Token);
                    
                    allConnectTasks.Add(connectTask);
                }

                Task.WaitAll([.. allConnectTasks], handshakeTokenSource.Token);
            }, handshakeTokenSource.Token
        );

        return link;
    }
}

internal static partial class TcpManyToManyLinkMakerLoggers
{
    [LoggerMessage(LogLevel.Information, "[TCP_M2M] {LocalPort} Started to try TCP connection with {RemoteIpe}")]
    public static partial void LogStartedToTryTcpConnectionWithRemoteIpe(this ILogger logger, int localPort, IPEndPoint remoteIpe);

    [LoggerMessage(LogLevel.Information, "[TCP_M2M] Start time {DateTime}")]
    public static partial void LogStartTime(this ILogger logger, string dateTime);

    [LoggerMessage(LogLevel.Information, "[TCP_M2M] {LocalPort} Connected to {RemoteIpe}")]
    public static partial void LogConnectedToRemoteIpe(this ILogger logger, int localPort, IPEndPoint remoteIpe);

    [LoggerMessage(LogLevel.Error, "{ex} [TCP_M2M] {LocalPort} Failed to connect {RemoteIpe}, remaining try time {TryTime}")]
    public static partial void LogFailedToConnectToRemoteIpe(this ILogger logger, Exception ex, int localPort, IPEndPoint remoteIpe, int tryTime);

    [LoggerMessage(LogLevel.Error, "[TCP_M2M] {LocalPort} Failed to connect {RemoteIpe}")]
    public static partial void LogFailedToConnectToRemoteIpe(this ILogger logger, int localPort, IPEndPoint remoteIpe);
}