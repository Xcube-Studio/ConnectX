using System.Net;
using MemoryPack;

namespace ConnectX.Shared.Formatters;

public class IpAddressFormatter : MemoryPackFormatter<IPAddress>
{
    public override void Serialize<TBufferWriter>(ref MemoryPackWriter<TBufferWriter> writer, scoped ref IPAddress? value)
    {
        if (value == null)
        {
            writer.WriteNullObjectHeader();
            return;
        }

        var addressBytes = value.GetAddressBytes();
        writer.WriteArray(addressBytes);
    }

    public override void Deserialize(ref MemoryPackReader reader, scoped ref IPAddress? value)
    {
        if (!reader.TryReadObjectHeader(out var count))
        {
            value = null;
            return;
        }

        var arr = reader.ReadArray<byte>()!;
        value = new IPAddress(arr);
    }
}