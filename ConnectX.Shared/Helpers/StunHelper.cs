using System.Net;
using ConnectX.Shared.Models;
using DnsClient;
using STUN;
using STUN.Client;
using STUN.Enums;
using STUN.StunResult;

namespace ConnectX.Shared.Helpers;

public static class StunHelper
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
}