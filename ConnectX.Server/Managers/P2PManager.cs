using Microsoft.Extensions.Logging;

namespace ConnectX.Server.Managers;

public class P2PManager
{
    private readonly ClientManager _clientManager;
    private readonly GroupManager _groupManager;
    private readonly ILogger _logger;
    
    public P2PManager(
        ClientManager clientManager,
        GroupManager groupManager,
        ILogger<P2PManager> logger)
    {
        _clientManager = clientManager;
        _groupManager = groupManager;
        _logger = logger;
    }
}