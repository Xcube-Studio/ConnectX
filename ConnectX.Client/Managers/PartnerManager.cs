using System.Collections.Concurrent;
using ConnectX.Client.Interfaces;
using ConnectX.Client.Transmission;
using ConnectX.Shared.Messages.Group;
using ConnectX.Shared.Models;
using Hive.Both.General.Dispatchers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ConnectX.Client.Managers;

public class PartnerManager
{
    private readonly IDispatcher _dispatcher;
    private readonly ILogger _logger;
    private readonly PeerManager _peerManager;
    private readonly IServerLinkHolder _serverLinkHolder;
    private readonly IServiceProvider _serviceProvider;

    public PartnerManager(
        PeerManager peerManager,
        IDispatcher dispatcher,
        IServerLinkHolder serverLinkHolder,
        IServiceProvider serviceProvider,
        ILogger<PartnerManager> logger)
    {
        _peerManager = peerManager;
        _dispatcher = dispatcher;
        _serverLinkHolder = serverLinkHolder;
        _serviceProvider = serviceProvider;
        _logger = logger;

        _dispatcher.AddHandler<GroupUserStateChanged>(OnGroupUserStateChanged);
    }

    public ConcurrentDictionary<Guid, Partner> Partners { get; } = new();

    public event Action<Partner>? OnPartnerAdded;

    private void OnGroupUserStateChanged(MessageContext<GroupUserStateChanged> ctx)
    {
        var message = ctx.Message;

        _logger.LogRoomStateChanged(message.State, message.UserInfo?.UserId ?? Guid.Empty);

        switch (message.State)
        {
            case GroupUserStates.Dismissed:
                RemoveAllPartners();
                return;
            case GroupUserStates.Disconnected:
            case GroupUserStates.Kicked:
            case GroupUserStates.Left:
                RemovePartner(message.UserInfo!.UserId);
                return;
            case GroupUserStates.Joined:
                AddPartner(message.UserInfo!.UserId);
                return;
            default:
                throw new ArgumentOutOfRangeException();
        }
    }

    public bool AddPartner(Guid partnerId)
    {
        if (partnerId == _serverLinkHolder.UserId) return false;

        var dispatcher = ActivatorUtilities.CreateInstance<DefaultDispatcher>(_serviceProvider);
        var p2PConnection = ActivatorUtilities.CreateInstance<P2PConnection>(
            _serviceProvider,
            partnerId,
            dispatcher);

        if (Partners.ContainsKey(partnerId)) return false;

        var partner = ActivatorUtilities.CreateInstance<Partner>(
            _serviceProvider,
            _serverLinkHolder.UserId,
            partnerId,
            p2PConnection);

        if (!Partners.TryAdd(partnerId, partner)) return false;

        _peerManager.AddLink(partnerId);
        OnPartnerAdded?.Invoke(partner);

        _logger.LogPartnerAdded(partnerId);

        return true;
    }

    public bool RemovePartner(Guid partnerId)
    {
        if (!Partners.TryRemove(partnerId, out var partner)) return false;

        partner.Disconnect();
        // _peerManager.RemoveLink(partnerId);

        return true;
    }

    public void RemoveAllPartners()
    {
        foreach (var (_, partner) in Partners)
            partner.Disconnect();

        Partners.Clear();
    }
}

internal static partial class PartnerManagerLoggers
{
    [LoggerMessage(LogLevel.Information, "Room state changed for user [{userId}] with state [{groupState:G}]")]
    public static partial void LogRoomStateChanged(this ILogger logger, GroupUserStates groupState, Guid userId);

    [LoggerMessage(LogLevel.Information, "Partner added with user ID [{userId}]")]
    public static partial void LogPartnerAdded(this ILogger logger, Guid userId);
}