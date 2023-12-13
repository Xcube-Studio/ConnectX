using System.Net;
using System.Net.Sockets;
using Hive.Network.Abstractions.Session;
using Hive.Network.Udp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ConnectX.Client.P2P.LinkMaker.ManyToMany;

public class UdpManyToManyPortLinkMaker(
    IServiceProvider serviceProvider,
    ILogger<UdpManyToManyPortLinkMaker> logger,
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
        var tokenSource = new CancellationTokenSource();
        var connector = ServiceProvider.GetRequiredService<IConnector<UdpSession>>();
        UdpSession? succeedLink = null;

        await Task.Run(async () =>
        {
            var times = 888;

            await WaitUntilStartTimeAsync(tokenSource);

            while (!tokenSource.IsCancellationRequested &&
                   times > 0)
            {
                times--;

                for (var i = 0; i < SelfSocketCount; i++)
                {
                    foreach (var targetPort in TargetPredictPort)
                    {
                        try
                        {
                            var remoteIp = new IPEndPoint(TargetIp, targetPort);

                            succeedLink = await connector.ConnectAsync(remoteIp, tokenSource.Token);

                            Logger.LogInformation(
                                "[UDP_S2M] {LocalPort} Started to try UDP connection with {RemoteIpe}",
                                SelfPort, remoteIp);
                            
                            if (succeedLink == null)
                                throw new SocketException((int)SocketError.Fault, "Link is null");

                            _remoteIpEndPoint = succeedLink.RemoteEndPoint;
                        }
                        catch (SocketException e)
                        {
                            Logger.LogError(
                                "[UDP_S2M] {LocalPort} Failed to try UDP connection with {RemoteIpe}, {Exception}",
                                SelfPort, RemoteIpEndPoint, e);
                        }
                    }
                }
            }

        }, tokenSource.Token);

        return succeedLink;
    }
}