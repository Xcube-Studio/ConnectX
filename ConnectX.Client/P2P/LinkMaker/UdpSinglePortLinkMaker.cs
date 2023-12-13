using System.Net;
using System.Net.Sockets;
using Hive.Common.Shared.Helpers;
using Hive.Network.Abstractions.Session;
using Hive.Network.Udp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ConnectX.Client.P2P.LinkMaker;

public class UdpSinglePortLinkMaker(
    IServiceProvider serviceProvider,
    ILogger<UdpSinglePortLinkMaker> logger,
    long startTimeTick,
    Guid partnerId,
    int localPort,
    IPEndPoint remoteIpe,
    CancellationToken cancellationToken)
    : SingleToSingleLinkMaker(serviceProvider, logger, startTimeTick, partnerId, localPort, remoteIpe,
        cancellationToken)
{
    public override async Task<ISession?> BuildLinkAsync()
    {
        using var handshakeTokenSource = new CancellationTokenSource();
        var connector = ServiceProvider.GetRequiredService<IConnector<UdpSession>>();
        UdpSession? link = null;
        
        Logger.LogInformation(
            "[UDP_S2S] {LocalPort} Started to try UDP connection with {RemoteIpe}",
            LocalPort, RemoteIpe);
        Logger.LogInformation(
            "[UDP_S2S] Start time {DateTime}",
            new DateTime(StartTimeTick).ToLongTimeString());

        var tryTime = 900;

        await Task.Run(async () =>
        {
            await TaskHelper.WaitUtil(() => !handshakeTokenSource.IsCancellationRequested &&
                                            DateTime.UtcNow.Ticks < StartTimeTick, handshakeTokenSource.Token);

            while (!handshakeTokenSource.IsCancellationRequested &&
                   tryTime > 0)
            {
                try
                {
                    tryTime--;
                    link = await connector.ConnectAsync(RemoteIpe, handshakeTokenSource.Token);
                    
                    if (link == null)
                        throw new SocketException((int)SocketError.Fault, "Link is null");

                    InvokeOnConnected(link);
                }
                catch (SocketException)
                {
                    Logger.LogWarning(
                        "[UDP_S2S] {LocalPort} Failed to connect with {RemoteIpe}, remaining try time {TryTime}",
                        LocalPort, RemoteIpe, tryTime);
                    
                    if (tryTime == 0)
                    {
                        Logger.LogError(
                            "[UDP_S2S] {LocalPort} Failed to connect with {RemoteIpe}, remaining try time {TryTime}",
                            LocalPort, RemoteIpe, tryTime);
                        InvokeOnFailed();
                        break;
                    }

                    await Task.Delay(2, handshakeTokenSource.Token);
                    continue;
                }

                Logger.LogInformation(
                    "[UDP_S2S] {LocalPort} Succeed to connect with {RemoteIpe}",
                    LocalPort, RemoteIpe);
                break;
            }
        }, handshakeTokenSource.Token);

        return link;
    }
}