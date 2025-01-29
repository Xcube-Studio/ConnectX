using System.Text.Json;
using System.Text.Json.Serialization;

namespace ConnectX.Server.Models.ZeroTier;

public class V4AssignMode
{
    [JsonPropertyName("zt")]
    public bool Zt { get; init; }
}

public class V6AssignMode
{
    [JsonPropertyName("6plane")]
    public bool SixPlane { get; init; }

    [JsonPropertyName("rfc4193")]
    public bool Rfc4193 { get; init; }

    [JsonPropertyName("zt")]
    public bool Zt { get; init; }
}

public class Rule
{
    [JsonPropertyName("not")]
    public bool Not { get; init; }

    [JsonPropertyName("or")]
    public bool Or { get; init; }

    [JsonPropertyName("type")]
    public required string Type { get; init; }
}

public class IpAssignment
{
    [JsonPropertyName("ipRangeStart")]
    public required string IpRangeStart { get; init; }

    [JsonPropertyName("ipRangeEnd")]
    public required string IpRangeEnd { get; init; }
}

public class Dns
{
    [JsonPropertyName("domain")]
    public required string Domain { get; init; }

    [JsonPropertyName("servers")]
    public required string[] Servers { get; init; }
}

public class Route
{
    [JsonPropertyName("target")]
    public required string Target { get; init; }

    [JsonPropertyName("via")]
    public string? Via { get; init; }
}

public class NetworkDetailsReqModel
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("enableBroadcast")]
    public required bool EnableBroadcast { get; init; }

    [JsonPropertyName("mtu")]
    public ushort? Mtu { get; init; }

    [JsonPropertyName("dns")]
    public Dns[]? Dns { get; init; }

    [JsonPropertyName("private")]
    public required bool Private { get; init; }

    [JsonPropertyName("ipAssignmentPools")]
    public IpAssignment[]? IpAssignmentPools { get; init; }

    [JsonPropertyName("v4AssignMode")]
    public V4AssignMode? V4AssignMode { get; init; }

    [JsonPropertyName("v6AssignMode")]
    public V6AssignMode? V6AssignMode { get; init; }

    [JsonPropertyName("multicastLimit")]
    public ushort? MulticastLimit { get; init; }

    [JsonPropertyName("routes")]
    public Route[]? Routes { get; init; }
}

public class NetworkDetailsModel
{
    [JsonPropertyName("authTokens")]
    public string?[]? AuthTokens { get; init; }

    [JsonPropertyName("authorizationEndpoint")]
    public string? AuthorizationEndpoint { get; init; }

    [JsonPropertyName("capabilities")]
    public int[]? Capabilities { get; init; }

    [JsonPropertyName("clientId")]
    public string? ClientId { get; init; }

    [JsonPropertyName("creationTime")]
    public ulong CreationTime { get; init; }

    [JsonPropertyName("dns")]
    public required Dns[] Dns { get; init; }

    [JsonPropertyName("enableBroadcast")]
    public bool EnableBroadcast { get; init; }

    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("ipAssignmentPools")]
    public IpAssignment[]? IpAssignmentPools { get; init; }

    [JsonPropertyName("mtu")]
    public ushort Mtu { get; init; }

    [JsonPropertyName("multicastLimit")]
    public ushort MulticastLimit { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("nwid")]
    public required string NetworkId { get; init; }

    [JsonPropertyName("objtype")]
    public string? ObjType { get; init; }

    [JsonPropertyName("private")]
    public bool Private { get; init; }

    [JsonPropertyName("remoteTraceLevel")]
    public ushort RemoteTraceLevel { get; init; }

    [JsonPropertyName("remoteTraceTarget")]
    public JsonElement? RemoteTraceTarget { get; init; }

    [JsonPropertyName("revision")]
    public ushort Revision { get; init; }

    [JsonPropertyName("routes")]
    public Route[]? Routes { get; init; }

    [JsonPropertyName("rules")]
    public Rule[]? Rules { get; init; }

    [JsonPropertyName("rulesSource")]
    public string? RulesSource { get; init; }

    [JsonPropertyName("ssoEnabled")]
    public bool SsoEnabled { get; set; }

    [JsonPropertyName("tags")]
    public int[]? Tags { get; init; }

    [JsonPropertyName("v4AssignMode")]
    public required V4AssignMode V4AssignMode { get; init; }

    [JsonPropertyName("v6AssignMode")]
    public required V6AssignMode V6AssignMode { get; init; }
}