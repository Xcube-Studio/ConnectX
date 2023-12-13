using Hive.Codec.Shared;
using MemoryPack;

namespace ConnectX.Shared.Messages.P2P;

[MessageDefine]
[MemoryPackable]
public sealed partial class P2PConReady : P2PConContext
{
    [MemoryPackConstructor]
    private P2PConReady()
    {
    }
    
    public P2PConReady(
        Guid recipientId,
        long time,
        P2PConContext context) : base(context)
    {
        RecipientId = recipientId;
        Time = time;
    }
    
    public P2PConReady(
        Guid recipientId,
        long time,
        P2PConContextInit context) : base(context)
    {
        RecipientId = recipientId;
        Time = time;
    }
    
    public Guid RecipientId { get; init; }
    public long Time { get; init; }
    public int Bargain { get; init; }
}