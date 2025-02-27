using System.Net.Http.Json;
using ConnectX.Server.Interfaces;
using ConnectX.Server.Models.ZeroTier;
using Microsoft.Extensions.Logging;

namespace ConnectX.Server;

public class ZeroTierApiService(
    HttpClient httpClient,
    ILogger<ZeroTierApiService> logger) : IZeroTierApiService
{
    public async Task<NodeStatusModel?> GetNodeStatusAsync(CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/status");
        using var res = await httpClient.SendAsync(req, cancellationToken);

        res.EnsureSuccessStatusCode();

        var result = await res.Content.ReadFromJsonAsync(ZeroTierModelContext.Default.NodeStatusModel, cancellationToken);

        return result;
    }

    public async Task<string[]> ListNetworkIds(CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/controller/network");
        using var res = await httpClient.SendAsync(req, cancellationToken);

        res.EnsureSuccessStatusCode();

        var result = await res.Content.ReadFromJsonAsync(ZeroTierModelContext.Default.StringArray, cancellationToken);

        return result ?? [];
    }

    public async Task<NetworkDetailsModel?> CreateOrUpdateNetwork(string networkId, NetworkDetailsReqModel details, CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"/controller/network/{networkId}");

        req.Content = JsonContent.Create(details, ZeroTierModelContext.Default.NetworkDetailsReqModel);

        using var res = await httpClient.SendAsync(req, cancellationToken);

        res.EnsureSuccessStatusCode();

        var result = await res.Content.ReadFromJsonAsync(ZeroTierModelContext.Default.NetworkDetailsModel, cancellationToken);

        return result;
    }

    public async Task<NetworkDetailsModel?> GetNetworkDetailsAsync(string networkId, CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"/controller/network/{networkId}");
        using var res = await httpClient.SendAsync(req, cancellationToken);

        res.EnsureSuccessStatusCode();

        var result = await res.Content.ReadFromJsonAsync(ZeroTierModelContext.Default.NetworkDetailsModel, cancellationToken);

        return result;
    }

    public async Task<NetworkDetailsModel?> DeleteNetworkAsync(string networkId, CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Delete, $"/controller/network/{networkId}");
        using var res = await httpClient.SendAsync(req, cancellationToken);

        res.EnsureSuccessStatusCode();

        var result = await res.Content.ReadFromJsonAsync(ZeroTierModelContext.Default.NetworkDetailsModel, cancellationToken);

        return result;
    }

    public async Task<NetworkDetailsModel?> DeleteNetworkMemberAsync(string networkId, string nodeId, CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Delete, $"/controller/network/{networkId}/member/{nodeId}");
        using var res = await httpClient.SendAsync(req, cancellationToken);

        res.EnsureSuccessStatusCode();

        var result = await res.Content.ReadFromJsonAsync(ZeroTierModelContext.Default.NetworkDetailsModel, cancellationToken);

        return result;
    }
}

static partial class ZeroTierApiServiceLoggers
{

}