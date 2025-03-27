using System.Text.Json.Serialization;

namespace ConnectX.Server.Models.ZeroTier;

public class NetworkPathModel
{
    [JsonPropertyName("active")]
    public bool Active { get; init; }

    [JsonPropertyName("address")]
    public string? Address { get; init; }

    [JsonPropertyName("expired")]
    public bool Expired { get; init; }

    [JsonPropertyName("lastReceive")]
    public ulong LastReceive { get; init; }

    [JsonPropertyName("lastSend")]
    public ulong LastSend { get; init; }

    [JsonPropertyName("localPort")]
    public ushort LocalPort { get; init; }

    [JsonPropertyName("localSocket")]
    public ulong LocalSocket { get; init; }

    [JsonPropertyName("preferred")]
    public bool Preferred { get; init; }

    [JsonPropertyName("trustedPathId")]
    public ulong TrustedPathId { get; init; }
}

public class NetworkPeerModel
{
    [JsonPropertyName("address")]
    public required string Address { get; init; }

    [JsonPropertyName("isBonded")]
    public bool IsBonded { get; init; }

    [JsonPropertyName("latency")]
    public int Latency { get; init; }

    [JsonPropertyName("paths")]
    public NetworkPathModel[]? Paths { get; init; }

    [JsonPropertyName("role")]
    public string? Role { get; init; }

    [JsonPropertyName("tunneled")]
    public bool Tunneled { get; init; }

    [JsonPropertyName("version")]
    public string? Version { get; init; }

    [JsonPropertyName("versionMajor")]
    public int VersionMajor { get; init; }

    [JsonPropertyName("versionMinor")]
    public int VersionMinor { get; init; }

    [JsonPropertyName("versionRev")]
    public int VersionRev { get; init; }
}