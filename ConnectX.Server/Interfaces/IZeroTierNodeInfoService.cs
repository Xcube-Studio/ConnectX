using ConnectX.Server.Models.ZeroTier;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ConnectX.Server.Interfaces;

public interface IZeroTierNodeInfoService : IHostedService
{
    NodeStatusModel? NodeStatus { get; }

    public static AsyncServiceScope CreateZtApi(IServiceScopeFactory scopeFactory, out IZeroTierApiService apiService)
    {
        var scope = scopeFactory.CreateAsyncScope();

        apiService = scope.ServiceProvider.GetRequiredService<IZeroTierApiService>();

        return scope;
    }
}