using System.Net;
using System.Net.Sockets;
using Hive.Common.Shared.Helpers;
using Hive.Network.Abstractions.Session;
using Hive.Network.Tcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ConnectX.Client.P2P.LinkMaker.ManyToMany;

public class TcpManyToManyLinkMaker(
    IServiceProvider serviceProvider,
    ILogger<TcpManyToManyLinkMaker> logger,
    long startTimeTick,
    Guid partnerId,
    IPAddress targetIp,
    int[] selfPredictPorts,
    int[] targetPredictPorts,
    CancellationToken cancellationToken)
    : ManyToManyPortLinkMaker(serviceProvider, logger, startTimeTick,
        partnerId, targetIp, selfPredictPorts, targetPredictPorts, cancellationToken)
{
    private IPEndPoint? _remoteIpEndPoint;

    public override IPEndPoint? RemoteIpEndPoint => _remoteIpEndPoint;


    public override async Task<ISession?> BuildLinkAsync()
    {
        var unusedPredictPort = new Queue<int>(TargetPredictPorts);
        var handshakeTokenSource = new CancellationTokenSource();
        var connector = ServiceProvider.GetRequiredService<IConnector<TcpSession>>();

        TcpSession? link = null;

        await Task.Run(
            () =>
            {
                List<Task> allConnectTasks = [];
                while (unusedPredictPort.Count > 0)
                {
                    var remotePort = unusedPredictPort.Dequeue();
                    var connectTask = Task.Run(async () =>
                    {
                        var localPort = Shared.Helpers.NetworkHelper.GetAvailablePrivatePort();
                        var remoteIpe = new IPEndPoint(TargetIp, remotePort);

                        var tryTime = 10;
                        
                        Logger.LogInformation(
                            "[TCP_M2M] {LocalPort} Started to try TCP connection with {RemoteIpe}",
                            localPort, remoteIpe);
                        Logger.LogInformation(
                            "[TCP_M2M] Start time {DateTime}",
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
                                link = await connector.ConnectAsync(remoteIpe, handshakeTokenSource.Token);
                                await handshakeTokenSource.CancelAsync();

                                if (link == null)
                                    throw new SocketException((int)SocketError.Fault, "Link is null");

                                InvokeOnConnected(link);
                                succeedThisConnect = true;
                                _remoteIpEndPoint = link.RemoteEndPoint;
                                
                                Logger.LogInformation(
                                    "[TCP_M2M] {LocalPort} Connected to {RemoteIpe}",
                                    localPort, remoteIpe);

                                break;
                            }
                            catch (SocketException)
                            {
                                Logger.LogWarning(
                                    "[TCP_M2M] {LocalPort} Failed to connect {RemoteIpe}, remaining try time {TryTime}",
                                    localPort, remoteIpe, tryTime);
                                
                                if (tryTime != 0) continue;

                                Logger.LogError(
                                    "[TCP_M2M] {LocalPort} Failed to connect {RemoteIpe}",
                                    localPort, remoteIpe);
                                break;
                            }
                        }

                        if (!succeedThisConnect)
                            Logger.LogError(
                                "[TCP_M2M] {LocalPort} Failed to connect {RemoteIpe}",
                                localPort, remoteIpe);
                    }, handshakeTokenSource.Token);
                    
                    allConnectTasks.Add(connectTask);
                }

                Task.WaitAll([.. allConnectTasks], handshakeTokenSource.Token);
            }, handshakeTokenSource.Token
        );

        return link;
    }
}