using System.Net;
using Microsoft.Extensions.Logging;

namespace ConnectX.Client.P2P.LinkMaker.ManyToMany;

/// <summary>
///     双方都无法预测自己在NAT外的端口，使用这个LinkMaker
///     主要方法：猜
/// </summary>
public abstract class ManyToManyPortLinkMaker(
    IServiceProvider serviceProvider,
    ILogger<ManyToManyPortLinkMaker> logger,
    long startTimeTick,
    Guid partnerId,
    IPAddress targetIp,
    int[] selfPredictPorts,
    int[] targetPredictPorts,
    CancellationToken cancellationToken)
    : P2PLinkMaker(startTimeTick, partnerId, serviceProvider,
        logger, cancellationToken)
{
    protected IPAddress TargetIp { get; init; } = targetIp;
    protected int[] SelfPredictPorts { get; init; } = selfPredictPorts;
    protected int[] TargetPredictPorts { get; init; } = targetPredictPorts;
    protected int SelfSocketCount { get; init; }
}