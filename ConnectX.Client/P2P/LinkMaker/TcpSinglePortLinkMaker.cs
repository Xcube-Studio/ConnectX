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
                    Logger.LogError(
                        e, "[TCP_S2S] {LocalPort} Failed to connect with {RemoteIpe}, remaining try time {TryTime}",
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