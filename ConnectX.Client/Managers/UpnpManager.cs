using ConnectX.Shared.Helpers;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Open.Nat;

namespace ConnectX.Client.Managers;

public class UpnpManager : BackgroundService
{
    private int _idInc;
    private List<Mapping>? _mappings;
    private const string MappingPrefix = "ConnectX";
    
    private readonly ILogger _logger;
    
    public UpnpManager(ILogger<UpnpManager> logger)
    {
        _logger = logger;
    }
    
    public bool IsFetchingStatus { get; private set; }
    public bool IsUpnpAvailable { get; private set; }
    public NatDevice? Device { get; private set; }
    
    private async Task RemoveOldMappingsAsync(NatDevice device, IEnumerable<Mapping> mappings)
    {
        foreach (var mapping in mappings.Where(m => m.Description.StartsWith(MappingPrefix)))
        {
            try
            {
                await device.DeletePortMapAsync(mapping);
            }
            catch (Exception e)
            {
                _logger.LogFailedToRemoveOldMappings(e);
            }
        }
    }
    
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var cts = new CancellationTokenSource(5000);

        try
        {
            IsFetchingStatus = true;
            
            var discoverer = new NatDiscoverer();
            var device = await discoverer.DiscoverDeviceAsync(PortMapper.Upnp, cts);
            
            _mappings = (await device.GetAllMappingsAsync())?.ToList() ?? [];
            await RemoveOldMappingsAsync(device, _mappings);

            var externalIp = await device.GetExternalIPAsync();
            if (NetworkHelper.IsPrivateIpAddress(externalIp))
            {
                _logger.LogFailedToFetchUpnpStatusExternalIpIsPrivate();
                IsUpnpAvailable = false;
                return;
            }
            
            Device = device;
            IsUpnpAvailable = true;
        }
        catch (NatDeviceNotFoundException)
        {
            IsUpnpAvailable = false;
            _logger.LogFailedToFetchUpnpStatusUpnpIsNotAvailable();
        }
        catch (Exception e)
        {
            _logger.LogFailedToFetchUpnpStatus(e);
        }
        finally
        {
            IsFetchingStatus = false;
        }
    }
    
    public async Task<Mapping?> CreatePortMapAsync(Protocol protocol, int privatePort)
    {
        if (Device == null) return null;

        var publicPort = GetAvailablePublicPort();
        var mapping = new Mapping(protocol, privatePort, publicPort, 0, $"{MappingPrefix} {_idInc++}");
        await Device.CreatePortMapAsync(mapping);

        _mappings?.Add(mapping);

        return mapping;
    }

    public async Task DeleteMappingAsync(Mapping mapping)
    {
        if (Device == null)
            return;

        await Device?.DeletePortMapAsync(mapping)!;

        _mappings?.Remove(mapping);
    }

    private int GetAvailablePublicPort()
    {
        const int maxPort = 65535; //系统tcp/udp端口数最大是65535           
        const int beginPort = 5000; //从这个端口开始检测

        int port;
        do
        {
            port = Random.Shared.Next(beginPort, maxPort);
        } while (!PublicPortIsAvailable(port));

        return port;
    }

    private bool PublicPortIsAvailable(int port)
    {
        return _mappings?.All(m => m.PublicPort != port) ?? false;
    }
}

internal static partial class UpnpManagerLoggers
{
    [LoggerMessage(LogLevel.Error, "{ex} [UPNP_MANAGER] Failed to remove old mappings.")]
    public static partial void LogFailedToRemoveOldMappings(this ILogger logger, Exception ex);

    [LoggerMessage(LogLevel.Warning, "[UPNP_MANAGER] Failed to fetch UPnP status, external IP is private.")]
    public static partial void LogFailedToFetchUpnpStatusExternalIpIsPrivate(this ILogger logger);

    [LoggerMessage(LogLevel.Error, "[UPNP_MANAGER] Failed to fetch UPnP status, UPnP is not available.")]
    public static partial void LogFailedToFetchUpnpStatusUpnpIsNotAvailable(this ILogger logger);

    [LoggerMessage(LogLevel.Error, "{ex} [UPNP_MANAGER] Failed to fetch UPnP status.")]
    public static partial void LogFailedToFetchUpnpStatus(this ILogger logger, Exception ex);
}