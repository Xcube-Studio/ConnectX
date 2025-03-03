using ConnectX.Server.Models.ZeroTier;

namespace ConnectX.Server.Interfaces;

public interface IZeroTierApiService
{
    Task<NodeStatusModel?> GetNodeStatusAsync(CancellationToken cancellationToken);

    Task<string[]> ListNetworkIds(CancellationToken cancellationToken);

    Task<NetworkDetailsModel?> CreateOrUpdateNetwork(string networkId, NetworkDetailsReqModel details, CancellationToken cancellationToken);

    Task<NetworkDetailsModel?> GetNetworkDetailsAsync(string networkId, CancellationToken cancellationToken);

    Task<NetworkDetailsModel?> DeleteNetworkAsync(string networkId, CancellationToken cancellationToken);

    Task<NetworkDetailsModel?> DeleteNetworkMemberAsync(string networkId, string nodeId, CancellationToken cancellationToken);

    Task<NetworkPeerModel[]?> GetNetworkPeersAsync(CancellationToken cancellationToken);
}