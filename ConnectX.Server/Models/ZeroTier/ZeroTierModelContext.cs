using System.Text.Json.Serialization;

namespace ConnectX.Server.Models.ZeroTier;

[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(NodeStatusModel))]
[JsonSerializable(typeof(NetworkDetailsModel))]
[JsonSerializable(typeof(NetworkDetailsReqModel))]
[JsonSerializable(typeof(NetworkPeerModel[]))]
public partial class ZeroTierModelContext : JsonSerializerContext;