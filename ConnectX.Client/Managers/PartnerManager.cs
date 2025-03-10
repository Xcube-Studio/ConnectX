using System.Collections.Concurrent;
using System.Net;
using ConnectX.Client.Interfaces;
using ConnectX.Client.Transmission;
using ConnectX.Client.Transmission.Connections;
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
    private readonly IRoomInfoManager _roomInfoManager;

    public PartnerManager(
        PeerManager peerManager,
        IDispatcher dispatcher,
        IRoomInfoManager roomInfoManager,
        IServerLinkHolder serverLinkHolder,
        IServiceProvider serviceProvider,
        ILogger<PartnerManager> logger)
    {
        _peerManager = peerManager;
        _dispatcher = dispatcher;
        _roomInfoManager = roomInfoManager;
        _serverLinkHolder = serverLinkHolder;
        _serviceProvider = serviceProvider;
        _logger = logger;

        _roomInfoManager.OnMemberAddressInfoUpdated += UpdatePartnerInfo;

        _dispatcher.AddHandler<GroupUserStateChanged>(OnGroupUserStateChanged);
    }

    private void UpdatePartnerInfo(UserInfo[] userInfos)
    {
        if (_roomInfoManager.CurrentGroupInfo == null)
        {
            _logger.LogRoomInfoEmpty();
            return;
        }

        foreach (var userInfo in userInfos)
        {
            var userId = userInfo.UserId;

            if (userId == _serverLinkHolder.UserId)
                continue;

            AddPartner(userId);
        }
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
                if (message.UserInfo!.RelayServerAddress != null)
                {
                    AddRelayPartner(message.UserInfo.UserId, message.UserInfo!.RelayServerAddress);
                    return;
                }

                AddPartner(message.UserInfo!.UserId);
                return;
            case GroupUserStates.InfoUpdated:
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(message.State));
        }
    }

    private bool AddRelayPartner(Guid partnerId, IPEndPoint relayServerAddress)
    {
        if (partnerId == _serverLinkHolder.UserId) return false;

        var dispatcher = ActivatorUtilities.CreateInstance<DefaultDispatcher>(_serviceProvider);
        var p2pConnection = ActivatorUtilities.CreateInstance<RelayConnection>(
            _serviceProvider,
            partnerId,
            relayServerAddress,
            dispatcher);

        if (Partners.ContainsKey(partnerId)) return false;

        var partner = ActivatorUtilities.CreateInstance<Partner>(
            _serviceProvider,
            _serverLinkHolder.UserId,
            partnerId,
            p2pConnection);

        if (!Partners.TryAdd(partnerId, partner)) return false;

        _peerManager.AddLink(partnerId);
        OnPartnerAdded?.Invoke(partner);

        _logger.LogRelayPartnerAdded(relayServerAddress, partnerId);

        return true;
    }

    private bool AddPartner(Guid partnerId)
    {
        if (partnerId == _serverLinkHolder.UserId) return false;

        var dispatcher = ActivatorUtilities.CreateInstance<DefaultDispatcher>(_serviceProvider);
        var p2pConnection = ActivatorUtilities.CreateInstance<P2PConnection>(
            _serviceProvider,
            partnerId,
            dispatcher);

        if (Partners.ContainsKey(partnerId)) return false;

        var partner = ActivatorUtilities.CreateInstance<Partner>(
            _serviceProvider,
            _serverLinkHolder.UserId,
            partnerId,
            p2pConnection);

        if (!Partners.TryAdd(partnerId, partner)) return false;

        _peerManager.AddLink(partnerId);
        OnPartnerAdded?.Invoke(partner);

        _logger.LogPartnerAdded(partnerId);

        return true;
    }

    private bool RemovePartner(Guid partnerId)
    {
        if (!Partners.TryRemove(partnerId, out var partner)) return false;

        partner.Disconnect();
        _logger.LogDisconnectedWithPartnerId(partnerId);

        return true;
    }

    public void RemoveAllPartners()
    {
        _peerManager.RemoveAllPeer();

        foreach (var (_, partner) in Partners)
            partner.Disconnect();

        Partners.Clear();
    }
}

internal static partial class PartnerManagerLoggers
{
    [LoggerMessage(LogLevel.Warning, "[PARTNER_MANAGER] Partner connected with user ID [{partnerId}]")]
    public static partial void LogDisconnectedWithPartnerId(this ILogger logger, Guid partnerId);

    [LoggerMessage(LogLevel.Information, "[PARTNER_MANAGER] Room state changed for user [{userId}] with state [{groupState:G}]")]
    public static partial void LogRoomStateChanged(this ILogger logger, GroupUserStates groupState, Guid userId);

    [LoggerMessage(LogLevel.Information, "[PARTNER_MANAGER] Partner added with user ID [{userId}]")]
    public static partial void LogPartnerAdded(this ILogger logger, Guid userId);

    [LoggerMessage(LogLevel.Information, "[PARTNER_MANAGER] Partner using relay server [{relayServerAddress}] added with user ID [{userId}]")]
    public static partial void LogRelayPartnerAdded(this ILogger logger, IPEndPoint relayServerAddress, Guid userId);
}