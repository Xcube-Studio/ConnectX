using System.Net;
using Microsoft.Extensions.Hosting;
using ZeroTier.Core;

namespace ConnectX.Client.Interfaces;

public interface IZeroTierNodeLinkHolder : IHostedService
{
    Node? Node { get; }
    Task<bool> JoinNetworkAsync(ulong networkId, CancellationToken cancellationToken);
    Task LeaveNetworkAsync(CancellationToken cancellationToken);

    IPAddress[] GetIpAddresses();
    IPAddress? GetFirstAvailableV4Address();
    IPAddress? GetFirstAvailableV6Address();

    bool IsNodeOnline();
}