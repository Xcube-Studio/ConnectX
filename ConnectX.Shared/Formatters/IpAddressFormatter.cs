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
        var arr = reader.ReadArray<byte>();

        if (arr == null || arr.Length == 0)
        {
            value = null;
            return;
        }

        value = new IPAddress(arr);
    }
}