using Hive.Network.Abstractions.Session;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Sockets;
using ConnectX.Client.Network.ZeroTier.Tcp;
using Hive.Common.Shared.Helpers;
using Socket = ZeroTier.Sockets.Socket;
using SocketException = ZeroTier.Sockets.SocketException;

namespace ConnectX.Client.P2P.LinkMaker;

public class ZtTcpSinglePortLinkMaker(
    long startTimeTick,
    Guid partnerId,
    IPAddress localAddress,
    ushort localPort,
    IPEndPoint remoteIpe,
    CancellationToken cancellationToken,
    IServiceProvider serviceProvider,
    ILogger<ZtTcpSinglePortLinkMaker> logger)
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
        const int defaultTryTime = 10;

        var receiveSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

        receiveSocket.Bind(new IPEndPoint(IPAddress.Any, LocalPort));
        receiveSocket.Listen(defaultTryTime);

        ZtTcpSession? connectLink = null;
        ZtTcpSession? acceptedLink = null;

        Socket conSocket;

        Logger.LogStartTime(new DateTime(StartTimeTick).ToLongTimeString());

        var acceptTask = Task.Run(async () =>
        {
            var tryTime = defaultTryTime;

            logger.LogStartedToAcceptP2PConnection();

            while (!Token.IsCancellationRequested && tryTime > 0)
            {
                try
                {
                    tryTime--;

                    conSocket = receiveSocket.Accept();
                    connectLink = ActivatorUtilities.CreateInstance<ZtTcpSession>(ServiceProvider,
                        0, conSocket);

                    ArgumentNullException.ThrowIfNull(connectLink);
                }
                catch (ArgumentNullException) { continue; }
                catch (SocketException e)
                {
                    Logger.LogFailedToAcceptConnection(e, tryTime);

                    if (tryTime == 0)
                    {
                        Logger.LogFailedToAcceptConnectionWithoutRetry();
                        InvokeOnFailed();
                    }

                    await Task.Delay(3000, Token);

                    continue;
                }

                logger.LogConnectionAccepted((IPEndPoint)conSocket.RemoteEndPoint);
                break;
            }
        });

        var connectTask = Task.Run(async () =>
        {
            var tryTime = defaultTryTime;

            Logger.LogStartedToTryTcpConnectionWithRemoteIpe(LocalPort, RemoteIpe);

            await TaskHelper.WaitUtil(() => !Token.IsCancellationRequested &&
                                            DateTime.UtcNow.Ticks < StartTimeTick, Token);
            
            while (!Token.IsCancellationRequested && tryTime > 0)
            {
                try
                {
                    tryTime--;

                    receiveSocket.Connect(RemoteIpe);

                    conSocket = receiveSocket;
                    connectLink = ActivatorUtilities.CreateInstance<ZtTcpSession>(ServiceProvider,
                        0, conSocket);

                    ArgumentNullException.ThrowIfNull(connectLink);
                }
                catch (ArgumentNullException){ continue; }
                catch (SocketException e)
                {
                    Logger.LogFailedToConnectToRemoteIpe(e, LocalPort, RemoteIpe, tryTime);

                    if (tryTime == 0)
                    {
                        Logger.LogFailedToConnectToRemoteIpe(RemoteIpEndPoint);
                        InvokeOnFailed();

                        break;
                    }

                    await Task.Delay(3000, Token);

                    continue;
                }

                Logger.LogSucceedToConnectToRemoteIpe(LocalPort, RemoteIpe);
                break;
            }
        }, Token);

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));

        await Task.WhenAny(acceptTask, connectTask).WaitAsync(cts.Token);

        if (connectLink == null || acceptedLink == null)
        {
            var result = connectLink ?? acceptedLink;

            InvokeOnConnected(result!);

            return result;
        }

        var (linkToDispose, linkToKeep) = Random.Shared.Next(2) switch
        {
            0 => (connectLink, acceptedLink),
            _ => (acceptedLink, connectLink)
        };

        linkToDispose.Close();
        linkToDispose.Dispose();

        InvokeOnConnected(linkToKeep);

        return linkToKeep;
    }
}

internal static partial class ZtTcpSinglePortLinkMakerLoggers
{
    [LoggerMessage(LogLevel.Information, "[ZT_TCP_S2S] New connection accepted, remote endpoint: {remoteIpe}.")]
    public static partial void LogConnectionAccepted(this ILogger logger, IPEndPoint remoteIpe);

    [LoggerMessage(LogLevel.Error, "[ZT_TCP_S2S] Failed to accept new connection, no trials left.")]
    public static partial void LogFailedToAcceptConnectionWithoutRetry(this ILogger logger);

    [LoggerMessage(LogLevel.Error, "[ZT_TCP_S2S] Failed to accept new connection, remaining try time {tryTime}")]
    public static partial void LogFailedToAcceptConnection(this ILogger logger, Exception ex, int tryTime);

    [LoggerMessage(LogLevel.Information, "[ZT_TCP_S2S] Started to accept P2P connection...")]
    public static partial void LogStartedToAcceptP2PConnection(this ILogger logger);

    [LoggerMessage(LogLevel.Information, "[ZT_TCP_S2S] {localPort} Started to try TCP connection with {remoteIpe}")]
    public static partial void LogStartedToTryTcpConnectionWithRemoteIpe(
        this ILogger logger,
        int localPort,
        IPEndPoint remoteIpe);

    [LoggerMessage(LogLevel.Information, "[ZT_TCP_S2S] Start time {dateTime}")]
    public static partial void LogStartTime(this ILogger logger, string dateTime);

    [LoggerMessage(LogLevel.Error,
        "[ZT_TCP_S2S] {localPort} Failed to connect with {remoteIpe}, remaining try time {tryTime}")]
    public static partial void LogFailedToConnectToRemoteIpe(this ILogger logger, Exception ex, int localPort,
        IPEndPoint remoteIpe, int tryTime);

    [LoggerMessage(LogLevel.Error,
        "[ZT_TCP_S2S] Failed to connect with {remoteIpe}, maybe the network is special, or the other party has dropped")]
    public static partial void LogFailedToConnectToRemoteIpe(this ILogger logger, IPEndPoint remoteIpe);

    [LoggerMessage(LogLevel.Information, "[ZT_TCP_S2S] {LocalPort} Succeed to connect with {remoteIpe}")]
    public static partial void LogSucceedToConnectToRemoteIpe(this ILogger logger, int localPort, IPEndPoint remoteIpe);
}