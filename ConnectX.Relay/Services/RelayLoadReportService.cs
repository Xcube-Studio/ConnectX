using ConnectX.Relay.Interfaces;
using ConnectX.Relay.Managers;
using Hive.Both.General.Dispatchers;
using Microsoft.Extensions.Hosting;

namespace ConnectX.Relay.Services;

public class RelayLoadReportService : BackgroundService
{
    private readonly IDispatcher _dispatcher;
    private readonly IServerLinkHolder _serverLinkHolder;

    private readonly RelayManager _relayManager;

    public RelayLoadReportService(
        IDispatcher dispatcher, 
        IServerLinkHolder serverLinkHolder, 
        RelayManager relayManager)
    {
        _dispatcher = dispatcher;
        _serverLinkHolder = serverLinkHolder;
        _relayManager = relayManager;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (_serverLinkHolder.ServerSession == null || !_serverLinkHolder.IsConnected || !_serverLinkHolder.IsSignedIn)
                return;

            await _dispatcher.SendAsync(_serverLinkHolder.ServerSession, _relayManager.GetRelayServerLoad(), stoppingToken);

            await Task.Delay(5000, stoppingToken);
        }
    }
}
