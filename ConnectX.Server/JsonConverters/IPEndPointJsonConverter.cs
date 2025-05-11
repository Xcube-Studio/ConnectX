using System.Net;
using System.Text.Json.Serialization;
using System.Text.Json;

namespace ConnectX.Server.JsonConverters;

public class IPEndPointJsonConverter : JsonConverter<IPEndPoint>
{
    public override IPEndPoint? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var endpointString = reader.GetString();
        if (string.IsNullOrWhiteSpace(endpointString))
            return null;

        // Try parse IP:Port format
        var parts = endpointString.Split(':');
        if (parts.Length < 2)
            throw new JsonException($"Invalid IPEndPoint format: {endpointString}");

        if (!int.TryParse(parts[^1], out var port))
            throw new JsonException($"Invalid port number: {parts[^1]}");

        var ipPart = string.Join(":", parts[..^1]);
        if (!IPAddress.TryParse(ipPart, out var ip))
            throw new JsonException($"Invalid IP address: {ipPart}");

        return new IPEndPoint(ip, port);
    }

    public override void Write(Utf8JsonWriter writer, IPEndPoint value, JsonSerializerOptions options)
    {
        var endpointString = $"{value.Address}:{value.Port}";
        writer.WriteStringValue(endpointString);
    }
}