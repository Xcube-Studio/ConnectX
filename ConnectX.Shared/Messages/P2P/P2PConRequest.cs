using Hive.Codec.Shared;
using MemoryPack;

namespace ConnectX.Shared.Messages.P2P;

[MessageDefine]
[MemoryPackable]
public sealed partial class P2PConRequest : P2PConContext
{
    [MemoryPackConstructor]
    private P2PConRequest()
    {
    }
    
    public P2PConRequest(
        int bargain,
        Guid targetId,
        Guid selfId,
        ushort selfPrivatePort,
        P2PConContext context) : base(context)
    {
        Bargain = bargain;
        TargetId = targetId;
        SelfPrivatePort = selfPrivatePort;
        SelfId = selfId;
    }

    public P2PConRequest(
        int bargain,
        Guid targetId,
        Guid selfId,
        ushort selfPrivatePort,
        P2PConContextInit context) : base(context)
    {
        Bargain = bargain;
        TargetId = targetId;
        SelfPrivatePort = selfPrivatePort;
        SelfId = selfId;
    }

    public Guid TargetId { get; init; }
    public ushort SelfPrivatePort { get; init; }
    public Guid SelfId { get; init; }
    public int Bargain { get; init; }
}