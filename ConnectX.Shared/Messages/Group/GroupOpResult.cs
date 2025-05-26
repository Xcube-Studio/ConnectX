using Hive.Codec.Shared;
using MemoryPack;
using System.Collections.ObjectModel;

namespace ConnectX.Shared.Messages.Group;

[MessageDefine]
[MemoryPackable]
public partial class GroupOpResult(
    GroupCreationStatus status,
    string? errorMessage = null,
    IReadOnlyDictionary<string, string>? metadata = null)
{
    public const string RedirectInfoKey = "RedirectInfo";
    public const string UseRelayServerKey = "UseRelayServer";

    public string? ErrorMessage { get; } = errorMessage;
    public Guid RoomId { get; init; }
    public GroupCreationStatus Status { get; } = status;
    public IReadOnlyDictionary<string, string> Metadata { get; } = metadata ?? ReadOnlyDictionary<string, string>.Empty;
}