using System.Net;
using System.Net.Sockets;
using Hive.Common.Shared.Helpers;
using Hive.Network.Abstractions.Session;
using Hive.Network.Shared.HandShake;
using Hive.Network.Udp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ConnectX.Client.P2P.LinkMaker;

public sealed class UdpSinglePortLinkMaker(
    long startTimeTick,
    Guid partnerId,
    IPAddress localAddress,
    ushort localPort,
    IPEndPoint remoteIpe,
    CancellationToken cancellationToken,
    IServiceProvider serviceProvider,
    ILogger<UdpSinglePortLinkMaker> logger)
    : SingleToSingleLinkMaker(
        serviceProvider,
        logger,
        startTimeTick,
        partnerId,
        localAddress,
        localPort,
        remoteIpe,
        cancellationToken)
{
    public override async Task<ISession?> BuildLinkAsync()
    {
        using var handshakeTokenSource = new CancellationTokenSource();

        UdpSession? link = null;

        Logger.LogStartedToTryUdpConnectionWithRemoteIpe(LocalPort, RemoteIpe);
        Logger.LogS2SStartTime(new DateTime(StartTimeTick).ToLongTimeString());

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

                    var receiveSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                    receiveSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    receiveSocket.Bind(new IPEndPoint(LocalAddress, LocalPort));
                    receiveSocket.Ttl = 32;

                    var shakeResult = await receiveSocket.HandShakeWith(RemoteIpe);

                    if (!shakeResult.HasValue) continue;

                    var sessionId = shakeResult.Value.SessionId;

                    link = ActivatorUtilities.CreateInstance<UdpSession>(
                        ServiceProvider,
                        sessionId, receiveSocket, RemoteIpe);

                    if (link == null)
                        throw new SocketException((int)SocketError.Fault, "Link is null");

                    InvokeOnConnected(link);
                }
                catch (SocketException e)
                {
                    Logger.LogConnectionFailed(e, LocalPort, RemoteIpe, tryTime);

                    if (tryTime == 0)
                    {
                        Logger.LogConnectionFailed(null, LocalPort, RemoteIpe, tryTime);
                        InvokeOnFailed();
                        break;
                    }

                    await Task.Delay(2, handshakeTokenSource.Token);
                    continue;
                }

                Logger.LogConnectionSucceeded(LocalPort, RemoteIpe);
                break;
            }
        }, handshakeTokenSource.Token);

        return link;
    }
}

internal static partial class UdpSinglePortLinkMakerLoggers
{
    [LoggerMessage(LogLevel.Information, "[UDP_S2S] {LocalPort} Started to try UDP connection with {RemoteIpe}")]
    public static partial void LogStartedToTryUdpConnectionWithRemoteIpe(this ILogger logger, ushort localPort,
        IPEndPoint remoteIpe);

    [LoggerMessage(LogLevel.Information, "[UDP_S2S] Start time {DateTime}")]
    public static partial void LogS2SStartTime(this ILogger logger, string dateTime);

    [LoggerMessage(LogLevel.Error,
        "[UDP_S2S] {LocalPort} Failed to connect with {RemoteIpe}, remaining try time {TryTime}")]
    public static partial void LogConnectionFailed(this ILogger logger, Exception? ex, ushort localPort,
        IPEndPoint remoteIpe, int tryTime);

    [LoggerMessage(LogLevel.Information, "[UDP_S2S] {LocalPort} Succeed to connect with {RemoteIpe}")]
    public static partial void LogConnectionSucceeded(this ILogger logger, ushort localPort, IPEndPoint remoteIpe);
}