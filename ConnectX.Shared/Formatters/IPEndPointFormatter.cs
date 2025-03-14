using System.Net;
using MemoryPack;

namespace ConnectX.Shared.Formatters;

public class IpEndPointFormatter : MemoryPackFormatter<IPEndPoint>
{
    public override void Serialize<TBufferWriter>(ref MemoryPackWriter<TBufferWriter> writer,
        scoped ref IPEndPoint? value)
    {
        if (value == null)
        {
            writer.WriteNullCollectionHeader();
            return;
        }

        var endPointStr = value.ToString();
        writer.WriteString(endPointStr);
    }

    public override void Deserialize(ref MemoryPackReader reader, scoped ref IPEndPoint? value)
    {
        var str = reader.ReadString();

        if (string.IsNullOrEmpty(str) || !IPEndPoint.TryParse(str, out var endPoint))
        {
            value = null;
            return;
        }

        value = endPoint;
    }
}