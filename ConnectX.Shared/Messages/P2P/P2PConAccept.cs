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
    
    public required Guid SelfId { get; init; }
    public required int Bargain { get; init; }
}