using System.Net;
using System.Net.Sockets;
using Hive.Common.Shared.Helpers;
using Hive.Network.Abstractions.Session;
using Hive.Network.Tcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ConnectX.Client.P2P.LinkMaker.ManyToSingle;

public class TcpManyToSingleLinkMaker(
    IServiceProvider serviceProvider,
    ILogger<TcpManyToSingleLinkMaker> logger,
    long startTimeTick,
    Guid partnerId,
    IPEndPoint remoteIpe,
    int[] selfPredictPorts,
    CancellationToken cancellationToken)
    : ManyToSinglePortLinkMaker(serviceProvider, logger, startTimeTick, partnerId, remoteIpe, selfPredictPorts,
        cancellationToken)
{
    public override async Task<ISession?> BuildLinkAsync()
    {
        var connector = ServiceProvider.GetRequiredService<IConnector<TcpSession>>();
        var handshakeTokenSource = new CancellationTokenSource();
        TcpSession? link = null;

        await Task.Run(
            () =>
            {
                List<Task> allConnectTasks = [];
                foreach (var port in SelfPredictPorts)
                {
                    var connectTask = Task.Run(async () =>
                    {
                        var tryTime = 100;
                        
                        Logger.LogInformation(
                            "[TCP_M2S] {LocalPort} Started to try TCP connection with {RemoteIpe}",
                            port, RemoteIpEndPoint);
                        Logger.LogInformation(
                            "[TCP_M2S] Start time {DateTime}",
                            new DateTime(StartTimeTick).ToLongTimeString());

                        await TaskHelper.WaitUtil(() => !handshakeTokenSource.IsCancellationRequested &&
                                                        DateTime.UtcNow.Ticks < StartTimeTick, handshakeTokenSource.Token);

                        var succeedThisConnect = false;

                        while (!handshakeTokenSource.IsCancellationRequested &&
                               tryTime > 0)
                        {
                            tryTime--;

                            try
                            {
                                link = await connector.ConnectAsync(RemoteIpEndPoint!, handshakeTokenSource.Token);
                                await handshakeTokenSource.CancelAsync();
                                
                                if (link == null)
                                    throw new SocketException((int)SocketError.Fault, "Link is null");

                                InvokeOnConnected(link);

                                succeedThisConnect = true;
                            }
                            catch (SocketException)
                            {
                                Logger.LogWarning(
                                    "[TCP_M2S] {LocalPort} Failed to connect {RemoteIpEndPoint}, remaining try time {TryTime}",
                                    port, RemoteIpEndPoint, tryTime);

                                if (tryTime == 0)
                                {
                                    Logger.LogError(
                                        "[TCP_M2S] {LocalPort} Failed to connect {RemoteIpEndPoint}",
                                        port, RemoteIpEndPoint);
                                    break;
                                }

                                continue;
                            }
                            
                            Logger.LogInformation(
                                "[TCP_M2S] {LocalPort} Connected to {RemoteIpEndPoint}",
                                port, RemoteIpEndPoint);
                            break;
                        }

                        if (!succeedThisConnect)
                            Logger.LogError(
                                "[TCP_M2S] {LocalPort} Failed to connect {RemoteIpEndPoint}",
                                port, remoteIpe);
                    }, handshakeTokenSource.Token);

                    allConnectTasks.Add(connectTask);
                }

                Task.WaitAll([.. allConnectTasks], handshakeTokenSource.Token);
            }, handshakeTokenSource.Token
        );

        return link;
    }
}