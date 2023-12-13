using Hive.Codec.Shared;
using MemoryPack;

namespace ConnectX.Shared.Messages.P2P;

[MessageDefine]
[MemoryPackable]
public sealed partial class P2PConAccept : P2PConContext
{
    [MemoryPackConstructor]
    private P2PConAccept()
    {
    }
    
    public P2PConAccept(
        int bargain,
        Guid selfId,
        P2PConContext context) : base(context)
    {
        SelfId = selfId;
        Bargain = bargain;
    }
    
    public P2PConAccept(
        int bargain,
        Guid selfId,
        P2PConContextInit context) : base(context)
    {
        SelfId = selfId;
        Bargain = bargain;
    }
    
    public Guid SelfId { get; init; }
    public int Bargain { get; init; }
}