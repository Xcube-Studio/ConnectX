using Hive.Codec.Shared;
using MemoryPack;
using System.Net;
using System.Text.Json.Serialization;
using ConnectX.Shared.JsonConverters;

namespace ConnectX.Shared.Messages.Server;

[MessageDefine]
[MemoryPackable]
public partial class InterconnectServerRegistration
{
    [MemoryPackAllowSerialize]
    [JsonConverter(typeof(IPEndPointJsonConverter))]
    public required IPEndPoint ServerAddress { get; init; }

    public required string ServerName { get; init; }

    public required string ServerMotd { get; init; }
}

[JsonSerializable(typeof(InterconnectServerRegistration))]
public partial class InterconnectServerRegistrationContexts : JsonSerializerContext;