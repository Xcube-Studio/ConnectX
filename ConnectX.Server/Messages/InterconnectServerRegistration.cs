using ConnectX.Server.JsonConverters;
using Hive.Codec.Shared;
using MemoryPack;
using System.Net;
using System.Text.Json.Serialization;

namespace ConnectX.Server.Messages;

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