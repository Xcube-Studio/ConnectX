﻿using Hive.Network.Abstractions.Session;
using Microsoft.Extensions.Hosting;

namespace ConnectX.Client.Interfaces;

public interface IServerLinkHolder : IHostedService
{
    ISession? ServerSession { get; }
    bool IsConnected { get; }
    bool IsSignedIn { get; }
    Guid UserId { get; }

    event Action OnServerLinkDisconnected;

    Task ConnectAsync(CancellationToken cancellationToken);
    Task DisconnectAsync(CancellationToken cancellationToken);
}