using System.Net;
using Microsoft.Extensions.Logging;

namespace ConnectX.Client.P2P.LinkMaker.ManyToSingle;

/// <summary>
///     对方无法预测自己在NAT外的端口，使用这个LinkMaker
///     用尽可能多的Socket去连接目标，加大连接成功的概率
/// </summary>
public abstract class ManyToSinglePortLinkMaker(
    IServiceProvider serviceProvider,
    ILogger<ManyToSinglePortLinkMaker> logger,
    long startTimeTick,
    Guid partnerId,
    IPEndPoint remoteIpe,
    int[] selfPredictPorts,
    CancellationToken cancellationToken)
    : P2PLinkMaker(startTimeTick, partnerId, serviceProvider, logger, cancellationToken)
{
    public int[] SelfPredictPorts { get; } = selfPredictPorts;

    public override IPEndPoint? RemoteIpEndPoint { get; } = remoteIpe;
}