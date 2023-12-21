using Hive.Codec.Shared;
using MemoryPack;

namespace ConnectX.Shared.Messages.Group;

[MessageDefine]
[MemoryPackable]
public partial class GroupOpResult(bool isSucceeded, string? errorMessage = null)
{
    public bool IsSucceeded { get; init; } = isSucceeded;
    public string? ErrorMessage { get; init; } = errorMessage;
    public Guid GroupId { get; init; }
}