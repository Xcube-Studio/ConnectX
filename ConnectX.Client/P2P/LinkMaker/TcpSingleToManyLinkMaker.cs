using System.Net;
using System.Net.Sockets;
using Hive.Common.Shared.Helpers;
using Hive.Network.Abstractions.Session;
using Hive.Network.Tcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ConnectX.Client.P2P.LinkMaker;

public class TcpSingleToManyLinkMaker(
    long startTimeTick,
    Guid partnerId,
    int selfSocketCount,
    int[] targetPredictPort,
    IPAddress targetIp,
    ushort selfPort,
    CancellationToken cancellationToken,
    IServiceProvider serviceProvider,
    ILogger<TcpSingleToManyLinkMaker> logger)
    : SingleToManyLinkMaker(serviceProvider, logger,
        startTimeTick, partnerId, selfSocketCount, targetPredictPort, targetIp,
        selfPort, cancellationToken)
{
    private IPEndPoint? _remoteIpEndPoint;

    public override IPEndPoint? RemoteIpEndPoint => _remoteIpEndPoint;

    public override async Task<ISession?> BuildLinkAsync()
    {
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
                
                var receiveSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                
                receiveSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                receiveSocket.Bind(new IPEndPoint(IPAddress.Any, SelfPort));

                Socket conSocket;

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

                            await receiveSocket.ConnectAsync(remoteIpe, cts.Token);
                            
                            conSocket = receiveSocket;
                            link = ActivatorUtilities.CreateInstance<TcpSession>(
                                ServiceProvider,
                                0, conSocket);
                            
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
                        catch (SocketException e)
                        {
                            Logger.LogError(
                                e, "[TCP_S2M] {LocalPort} Failed to connect with {RemoteIpe}, remaining try time {TryTime}",
                                SelfPort, remoteIpe, tryTime);
                        }
                    }
                }
            }, handshakeTokenSource.Token
        );


        return link;
    }
}