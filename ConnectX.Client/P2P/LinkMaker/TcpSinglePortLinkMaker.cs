using System.Net;
using System.Net.Sockets;
using Hive.Common.Shared.Helpers;
using Hive.Network.Abstractions.Session;
using Hive.Network.Tcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ConnectX.Client.P2P.LinkMaker;

public class TcpSinglePortLinkMaker(
    IServiceProvider serviceProvider,
    ILogger<TcpSinglePortLinkMaker> logger,
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
        var connector = ServiceProvider.GetRequiredService<IConnector<TcpSession>>();
        TcpSession? link = null;

        await Task.Run(async () =>
        {
            var tryTime = 10;
            
            Logger.LogInformation(
                "[TCP_S2S] {LocalPort} Started to try TCP connection with {RemoteIpe}",
                LocalPort, RemoteIpe);
            Logger.LogInformation(
                "[TCP_S2S] Start time {DateTime}",
                new DateTime(StartTimeTick).ToLongTimeString());

            await TaskHelper.WaitUtil(() => !Token.IsCancellationRequested &&
                                            DateTime.UtcNow.Ticks < StartTimeTick, Token);

            while (!Token.IsCancellationRequested &&
                   tryTime > 0)
            {
                try
                {
                    tryTime--;
                    link = await connector.ConnectAsync(RemoteIpe, Token);
                    
                    if (link == null)
                        throw new SocketException((int)SocketError.Fault, "Link is null");

                    InvokeOnConnected(link);
                }
                catch (SocketException)
                {
                    Logger.LogWarning(
                        "[TCP_S2S] {LocalPort} Failed to connect with {RemoteIpe}, remaining try time {TryTime}",
                        LocalPort, RemoteIpe, tryTime);

                    if (tryTime == 0)
                    {
                        Logger.LogError(
                            "[TCP_S2S] Failed to connect with {RemoteIpe}, maybe the network is special, or the other party has dropped",
                            RemoteIpEndPoint);
                        
                        InvokeOnFailed();
                        break;
                    }

                    continue;
                }

                Logger.LogInformation(
                    "[TCP_S2S] {LocalPort} Succeed to connect with {RemoteIpe}",
                    LocalPort, RemoteIpe);
                break;
            }
        }, Token);

        return link;
    }
}