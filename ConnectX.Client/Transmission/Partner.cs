using ConnectX.Shared.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ConnectX.Client.Transmission;

public class Partner
{
    private bool _isLastTimeConnected;
    private PingChecker? _pingChecker;

    private readonly Guid _selfId;
    private readonly Guid _partnerId;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger _logger;
    
    public P2PConnection Connection { get; }
    public int Latency { get; private set; }
    
    public Partner(
        Guid selfId,
        Guid partnerId,
        P2PConnection connection,
        IHostApplicationLifetime lifetime,
        IServiceProvider serviceProvider,
        ILogger<Partner> logger)
    {
        Connection = connection;
        
        _selfId = selfId;
        _partnerId = partnerId;
        _lifetime = lifetime;
        _serviceProvider = serviceProvider;
        _logger = logger;
        
        KeepConnectAsync().Forget();
    }
    
    public event Action<Partner>? OnDisconnected;
    public event Action<Partner>? OnConnected;

    private async Task KeepConnectAsync()
    {
        while (!_lifetime.ApplicationStopping.IsCancellationRequested)
        {
            if (!Connection.IsConnected)
            {
                if (_isLastTimeConnected)
                {
                    _logger.LogDebug(
                        "[PARTNER] Disconnected with {PartnerId}",
                        _partnerId);
                    
                    OnDisconnected?.Invoke(this);
                    
                    _isLastTimeConnected = false;
                    _pingChecker = null;
                }

                if (await Connection.ConnectAsync())
                {
                    _logger.LogDebug(
                        "[PARTNER] Connected with {PartnerId}",
                        _partnerId);
                    
                    _isLastTimeConnected = true;
                    
                    OnConnected?.Invoke(this);
                }
            }
            else
            {
                if (_isLastTimeConnected == false)
                {
                    _logger.LogDebug(
                        "[PARTNER] Connected with {PartnerId}",
                        _partnerId);
                    
                    _isLastTimeConnected = true;
                    
                    OnConnected?.Invoke(this);
                }

                _pingChecker ??= ActivatorUtilities.CreateInstance<PingChecker>(
                    _serviceProvider,
                    _selfId,
                    _partnerId,
                    Connection);
                Latency = await _pingChecker.CheckPingAsync();
            }

            await Task.Delay(TimeSpan.FromSeconds(10), _lifetime.ApplicationStopping);
        }
    }

    public void Disconnect()
    {
        Connection.Disconnect();
    }
}