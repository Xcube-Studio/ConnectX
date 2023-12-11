using ConnectX.Shared.Helpers;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ConnectX.Server;

public class NatTestService : BackgroundService
{
    private readonly ILogger _logger;
    
    public NatTestService(
        ILogger<NatTestService> logger)
    {
        _logger = logger;
    }
    
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[NAT] Starting NAT test service...");

        try
        {
            var testResult = await StunHelper.GetNatTypeAsync(cancellationToken: stoppingToken);
            _logger.LogInformation("[NAT] NAT type: {testResult}", testResult);
            _logger.LogInformation(
                "[NAT] ConnectX NAT type: {natType}",
                StunHelper.ToNatTypes(testResult));
        }
        catch (Exception e)
        {
            _logger.LogError(e, "[NAT] Failed to get NAT type.");
        }
    }
}