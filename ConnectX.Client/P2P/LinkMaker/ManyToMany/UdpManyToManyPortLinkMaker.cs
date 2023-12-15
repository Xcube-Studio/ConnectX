using System.Net;
using System.Net.Sockets;
using ConnectX.Shared.Helpers;
using Hive.Network.Abstractions.Session;
using Hive.Network.Shared.HandShake;
using Hive.Network.Udp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ConnectX.Client.P2P.LinkMaker.ManyToMany;

public class UdpManyToManyPortLinkMaker(
    long startTimeTick,
    Guid partnerId,
    int selfSocketCount,
    int[] targetPredictPort,
    IPAddress targetIp,
    ushort selfPort,
    CancellationToken cancellationToken,
    IServiceProvider serviceProvider,
    ILogger<UdpManyToManyPortLinkMaker> logger)
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
                            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
                                { Ttl = 8 };
                            
                            Logger.LogInformation(
                                "[UDP_S2M] {LocalPort} Started to try UDP connection with {RemoteIpe}",
                                SelfPort, remoteIp);
                            
                            socket.Bind(new IPEndPoint(IPAddress.Any, NetworkHelper.GetAvailablePrivatePort()));
                            
                            var shakeResult = await socket.HandShakeWith(remoteIp);

                            if (!shakeResult.HasValue) continue;
                            
                            var sessionId = shakeResult.Value.SessionId;

                            succeedLink = ActivatorUtilities.CreateInstance<UdpClientSession>(
                                ServiceProvider,
                                sessionId, socket, remoteIp);
                            
                            if (succeedLink == null)
                                throw new SocketException((int)SocketError.Fault, "Link is null");

                            _remoteIpEndPoint = succeedLink.RemoteEndPoint;
                        }
                        catch (SocketException e)
                        {
                            Logger.LogError(
                                e, "[UDP_S2M] {LocalPort} Failed to try UDP connection with {RemoteIpe}",
                                SelfPort, RemoteIpEndPoint);
                        }
                    }
                }
            }

        }, tokenSource.Token);

        return succeedLink;
    }
}