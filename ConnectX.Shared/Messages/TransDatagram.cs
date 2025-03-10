using ConnectX.Shared.Models;
using Hive.Codec.Shared;
using MemoryPack;

namespace ConnectX.Shared.Messages;

[MessageDefine]
[MemoryPackable]
public partial struct TransDatagram
{
    public readonly DatagramFlag Flag;
    public readonly int SynOrAck;
    public ReadOnlyMemory<byte>? Payload;
    public Guid? RelayTo;

    public TransDatagram()
    {
        Flag = DatagramFlag.SYN;
        SynOrAck = 0;
        Payload = null;
        RelayTo = null;
    }

    public const DatagramFlag FirstHandShakeFlag = DatagramFlag.CON | DatagramFlag.SYN;
    public const DatagramFlag SecondHandShakeFlag = DatagramFlag.CON | DatagramFlag.SYN | DatagramFlag.ACK;
    public const DatagramFlag ThirdHandShakeFlag = DatagramFlag.CON | DatagramFlag.ACK;

    [MemoryPackConstructor]
    public TransDatagram(DatagramFlag flag, int synOrAck, ReadOnlyMemory<byte>? payload, Guid? relayTo)
    {
        Flag = flag;
        SynOrAck = synOrAck;
        Payload = payload;
        RelayTo = relayTo;
    }

    /// <summary>
    ///     创建 Connect 请求包，建立连接的第一次握手
    /// </summary>
    public static TransDatagram CreateHandShakeFirst(int synOrAck, Guid? to = null)
    {
        return new TransDatagram(FirstHandShakeFlag, synOrAck, null, to);
    }

    /// <summary>
    ///     创建 Connect SYN ACK 请求包，建立连接时的第二次握手
    /// </summary>
    public static TransDatagram CreateHandShakeSecond(int synOrAck, Guid? to = null)
    {
        return new TransDatagram(SecondHandShakeFlag, synOrAck, null, to);
    }

    /// <summary>
    ///     创建 Connect ACK 请求包，建立连接时的第三次握手
    /// </summary>
    public static TransDatagram CreateHandShakeThird(int synOrAck, Guid? to = null)
    {
        return new TransDatagram(ThirdHandShakeFlag, synOrAck, null, to);
    }

    public static TransDatagram CreateNormal(int syn, ReadOnlyMemory<byte> payload, Guid? to = null)
    {
        return new TransDatagram(DatagramFlag.SYN, syn, payload, to);
    }

    public static TransDatagram CreateAck(int ack, Guid? to = null)
    {
        return new TransDatagram(DatagramFlag.ACK, ack, null, to);
    }
}