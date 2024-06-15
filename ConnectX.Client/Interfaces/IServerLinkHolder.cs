using Hive.Network.Abstractions.Session;
using Microsoft.Extensions.Hosting;
using STUN.StunResult;

namespace ConnectX.Client.Interfaces;

public interface IServerLinkHolder : IHostedService
{
    StunResult5389? NatType { get; }
    ISession? ServerSession { get; }
    bool IsConnected { get; }
    bool IsSignedIn { get; }
    Guid UserId { get; }

    Task ConnectAsync(CancellationToken cancellationToken);
    Task DisconnectAsync(CancellationToken cancellationToken);
}