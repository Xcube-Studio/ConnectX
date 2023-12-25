using System.Net;
using System.Net.Sockets;
using ConnectX.Shared.Messages.Query;
using ConnectX.Shared.Messages.Query.Response;
using ConnectX.Shared.Models;
using DnsClient;
using Hive.Both.General.Dispatchers;
using Hive.Network.Tcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using STUN;
using STUN.Client;
using STUN.Enums;
using STUN.StunResult;

namespace ConnectX.Shared.Helpers;

public static partial class StunHelper
{
    public static readonly string[] StunServers =
    [
        "stunserver.stunprotocol.org",
        "stun.hot-chilli.net",
        "stun.fitauto.ru",
        "stun.syncthing.net",
        "stun.qq.com",
        "stun.miwifi.com"
    ];

    public static async Task<StunResult5389> GetNatTypeAsync(
        string? serverAddress = null,
        TransportType transportType = TransportType.Udp,
        bool useV6 = false,
        CancellationToken cancellationToken = default)
    {
        serverAddress ??= Random.Shared.GetItems(StunServers, 1)[0];

        var localEndPoint = useV6
            ? new IPEndPoint(IPAddress.IPv6Any, IPEndPoint.MinPort)
            : new IPEndPoint(IPAddress.Any, IPEndPoint.MinPort);
        var dnsClient = new LookupClient(new LookupClientOptions { UseCache = true });
        var port = transportType == TransportType.Tls
            ? StunServer.DefaultTlsPort
            : StunServer.DefaultPort;

        var queryResult = await dnsClient.QueryAsync(
            serverAddress,
            useV6 ? QueryType.AAAA : QueryType.A,
            cancellationToken: cancellationToken);

        if (queryResult.HasError)
            throw new InvalidOperationException(queryResult.ErrorMessage);

        var serverAddr = useV6
            ? queryResult.Answers.AaaaRecords().FirstOrDefault()?.Address
            : queryResult.Answers.ARecords().FirstOrDefault()?.Address;
        
        ArgumentNullException.ThrowIfNull(serverAddr);

        using var client = new StunClient5389UDP(
            new IPEndPoint(serverAddr, port),
            localEndPoint);

        await client.QueryAsync(cancellationToken);

        return client.State with { };
    }

    public static NatTypes ToNatTypes(StunResult5389 stun)
    {
        return stun switch
        {
            _ when stun.MappingBehavior is MappingBehavior.EndpointIndependent &&
                   stun.FilteringBehavior is FilteringBehavior.EndpointIndependent => NatTypes.Type1,
            _ when stun.MappingBehavior is MappingBehavior.EndpointIndependent &&
                   stun.FilteringBehavior is FilteringBehavior.AddressDependent => NatTypes.Type2,
            _ when stun.MappingBehavior is MappingBehavior.EndpointIndependent &&
                   stun.FilteringBehavior is FilteringBehavior.AddressAndPortDependent => NatTypes.Type3,
            
            _ when stun.MappingBehavior is MappingBehavior.AddressDependent &&
                   stun.FilteringBehavior is FilteringBehavior.EndpointIndependent => NatTypes.Type4,
            _ when stun.MappingBehavior is MappingBehavior.AddressDependent &&
                   stun.FilteringBehavior is FilteringBehavior.AddressDependent => NatTypes.Type5,
            _ when stun.MappingBehavior is MappingBehavior.AddressDependent &&
                   stun.FilteringBehavior is FilteringBehavior.AddressAndPortDependent => NatTypes.Type6,
            
            _ when stun.MappingBehavior is MappingBehavior.AddressAndPortDependent &&
                   stun.FilteringBehavior is FilteringBehavior.EndpointIndependent => NatTypes.Type7,
            _ when stun.MappingBehavior is MappingBehavior.AddressAndPortDependent &&
                   stun.FilteringBehavior is FilteringBehavior.AddressDependent => NatTypes.Type8,
            _ when stun.MappingBehavior is MappingBehavior.AddressAndPortDependent &&
                   stun.FilteringBehavior is FilteringBehavior.AddressAndPortDependent => NatTypes.Type9,
            
            _ when stun.MappingBehavior is MappingBehavior.Direct &&
                   stun.FilteringBehavior is FilteringBehavior.None => NatTypes.Direct,
            _ => NatTypes.Unknown
        };
    }

    [LoggerMessage(LogLevel.Debug, "Port {I} map to {Port}")]
    private static partial void LogPortMapToPort(this ILogger logger, int i, int port);

    [LoggerMessage(LogLevel.Error, "{ex} Error when querying port {I}")]
    private static partial void LogQueryingPortError(this ILogger logger, Exception ex, int i);

    public static async Task<PortPredictResult> PredictPublicPortAsync(
        IServiceProvider serviceProvider,
        ILogger logger,
        IPEndPoint serverEndPoint,
        CancellationToken ct)
    {
        const int sampleCount = 20;
        List<(int, int)> results = [];
        List<int> dist = [];

        int? prevPort = null;

        for (var i = 1000; i < 1000 + sampleCount && !ct.IsCancellationRequested; i++)
        {
            if (!NetworkHelper.PortIsAvailable(i))
                continue;
            try
            {
                var port = await QueryPublicPortAsync(serviceProvider, serverEndPoint, i, ct);

                logger.LogPortMapToPort(i, port);

                if (prevPort != null)
                {
                    dist.Add(port - prevPort.Value);
                    results.Add((i, port));
                }

                prevPort = port;
            }
            catch (Exception e)
            {
                logger.LogQueryingPortError(e, i);
            }
        }

        var distMean = dist.Average();
        var sameCount = 0; //统计privatePort和publicPort相同的次数
        foreach (var (privatePort, publicPort) in results) sameCount += privatePort == publicPort ? 1 : 0;

        var sameRate = (double)sameCount / results.Count;

        var changeLaw = distMean switch
        {
            <= 1 => ChangeLaws.Decrease,
            >= 1 => ChangeLaws.Increase,
            _ => ChangeLaws.Random
        };
        var testMin = results.Min(t => t.Item2);
        var testMax = results.Max(t => t.Item2);
        
        var lower = changeLaw switch
        {
            ChangeLaws.Decrease or ChangeLaws.Random => testMin - 500,
            ChangeLaws.Increase => testMin - 100,
            _ => throw new ArgumentOutOfRangeException()
        };

        var upper = changeLaw switch
        {
            ChangeLaws.Increase or ChangeLaws.Random => testMin + 500,
            ChangeLaws.Decrease => testMax + 100,
            _ => throw new ArgumentOutOfRangeException()
        };

        return new PortPredictResult(
            lower,
            upper,
            (int)distMean,
            changeLaw,
            sameRate > 0.5,
            results.Last().Item2
        );
    }


    public static async Task<int> QueryPublicPortAsync(
        IServiceProvider serviceProvider,
        IPEndPoint serverEndPoint,
        int privatePort,
        CancellationToken ct)
    {
        var tmpSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        tmpSocket.Bind(new IPEndPoint(IPAddress.Any, privatePort));

        await tmpSocket.ConnectAsync(serverEndPoint, ct);
        
        var session = ActivatorUtilities.CreateInstance<TcpSession>(serviceProvider, 0, tmpSocket);
        var dispatcher = ActivatorUtilities.CreateInstance<DefaultDispatcher>(serviceProvider);
        var dispatchSession = new DispatchableSession(session, dispatcher, ct);

        var query = new TempQuery(QueryOps.PublicPort);
        var result =
            await dispatchSession.Dispatcher.SendAndListenOnce<TempQuery, PublicPortQueryResult>(session, query, ct);

        if (result == null) return 0;
        
        session.Close();
        dispatchSession.Dispose();

        return result.Port;
    }
}