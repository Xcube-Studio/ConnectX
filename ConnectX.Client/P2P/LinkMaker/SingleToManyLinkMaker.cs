using System.Net;
using Microsoft.Extensions.Logging;

namespace ConnectX.Client.P2P.LinkMaker;

/// <summary>
///     无法获取对方的准确端口时，采用此方法(碰运气)
/// </summary>
public abstract class SingleToManyLinkMaker(
    IServiceProvider serviceProvider,
    ILogger<SingleToManyLinkMaker> logger,
    long startTimeTick,
    Guid partnerId,
    int selfSocketCount,
    int[] targetPredictPort,
    IPAddress targetIp,
    int selfPort,
    CancellationToken cancellationToken)
    : P2PLinkMaker(startTimeTick, partnerId,
        serviceProvider, logger, cancellationToken)
{
    protected int SelfPort { get; } = selfPort;
    protected int SelfSocketCount { get; } = selfSocketCount;
    protected int[] TargetPredictPort { get; } = targetPredictPort;
    protected IPAddress TargetIp { get; } = targetIp;
}