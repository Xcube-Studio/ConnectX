using ConnectX.Server.Models.ZeroTier;
using Microsoft.Extensions.Hosting;

namespace ConnectX.Server.Interfaces;

public interface IZeroTierNodeInfoService : IHostedService
{
    NodeStatusModel? NodeStatus { get; }
}