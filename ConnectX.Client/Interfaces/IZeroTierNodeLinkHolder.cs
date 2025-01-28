using Microsoft.Extensions.Hosting;
using ZeroTier.Core;

namespace ConnectX.Client.Interfaces;

public interface IZeroTierNodeLinkHolder : IHostedService
{
    Node Node { get; }
}