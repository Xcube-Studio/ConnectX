using System.Net;
using System.Net.Sockets;
using Hive.Common.Shared.Helpers;
using Hive.Network.Abstractions.Session;
using Hive.Network.Tcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ConnectX.Client.P2P.LinkMaker;

public class TcpSingleToManyLinkMaker(
    IServiceProvider serviceProvider,
    ILogger<TcpSingleToManyLinkMaker> logger,
    long startTimeTick,
    Guid partnerId,
    int selfSocketCount,
    int[] targetPredictPort,
    IPAddress targetIp,
    int selfPort,
    CancellationToken cancellationToken)
    : SingleToManyLinkMaker(serviceProvider, logger,
        startTimeTick, partnerId, selfSocketCount, targetPredictPort, targetIp,
        selfPort, cancellationToken)
{
    private IPEndPoint? _remoteIpEndPoint;

    public override IPEndPoint? RemoteIpEndPoint => _remoteIpEndPoint;

    public override async Task<ISession?> BuildLinkAsync()
    {
        var connector = ServiceProvider.GetRequiredService<IConnector<TcpSession>>();
        var unusedPredictPort = new Queue<int>(TargetPredictPort);
        var handshakeTokenSource = new CancellationTokenSource();
        TcpSession? link = null;

        await Task.Run(
            async () =>
            {
                Logger.LogInformation(
                    "[TCP_S2M] {LocalPort} Started to try TCP connection with {RemoteIpe}",
                    SelfPort, TargetIp);
                Logger.LogInformation(
                    "[TCP_S2M] Start time {DateTime}",
                    new DateTime(StartTimeTick).ToLongTimeString());

                await TaskHelper.WaitUtil(() => !handshakeTokenSource.IsCancellationRequested &&
                                                DateTime.UtcNow.Ticks < StartTimeTick, handshakeTokenSource.Token);

                while (unusedPredictPort.Count > 0)
                {
                    var remotePort = unusedPredictPort.Dequeue();
                    var remoteIpe = new IPEndPoint(TargetIp, remotePort);
                    var tryTime = 10;
                    
                    Logger.LogInformation(
                        "[TCP_S2M] {LocalPort} Started to try TCP connection with {RemoteIpe}",
                        SelfPort, remoteIpe);

                    while (!handshakeTokenSource.IsCancellationRequested &&
                           tryTime > 0)
                    {
                        tryTime--;

                        try
                        {
                            using var cts = new CancellationTokenSource();
                            cts.CancelAfter(TimeSpan.FromSeconds(1));

                            link = await connector.ConnectAsync(remoteIpe, cts.Token);
                            
                            if (link == null)
                                throw new SocketException((int)SocketError.Fault, "Link is null");
                            
                            Logger.LogInformation(
                                "[TCP_S2M] {LocalPort} Succeed to connect with {RemoteIpe}",
                                SelfPort, remoteIpe);
                            
                            _remoteIpEndPoint = link.RemoteEndPoint;
                            InvokeOnConnected(link);
                            await handshakeTokenSource.CancelAsync();
                            break;
                        }
                        catch (SocketException)
                        {
                            Logger.LogError(
                                "[TCP_S2M] {LocalPort} Failed to connect with {RemoteIpe}, remaining try time {TryTime}",
                                SelfPort, remoteIpe, tryTime);
                        }
                    }
                }
            }, handshakeTokenSource.Token
        );


        return link;
    }
}