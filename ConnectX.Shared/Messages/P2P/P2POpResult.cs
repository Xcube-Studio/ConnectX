using Hive.Codec.Shared;
using MemoryPack;

namespace ConnectX.Shared.Messages.P2P;

[MessageDefine]
[MemoryPackable]
public partial class P2POpResult(bool isSucceeded, string? errorMessage = null)
{
    public bool IsSucceeded { get; init; }
    public string? ErrorMessage { get; init; }
    public P2PConContext? Context { get; init; }
}