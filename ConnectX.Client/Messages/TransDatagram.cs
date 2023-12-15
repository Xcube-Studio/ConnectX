using System.Buffers;
using ConnectX.Client.Models;
using Hive.Codec.Shared;
using MemoryPack;

namespace ConnectX.Client.Messages;

[MessageDefine]
[MemoryPackable]
public partial struct TransDatagram
{
    public DatagramFlag Flag;
    public int SynOrAck;
    public ReadOnlyMemory<byte>? Payload;

    public TransDatagram()
    {
        Flag = DatagramFlag.SYN;
        SynOrAck = 0;
        Payload = null;
    }

    public const DatagramFlag FirstHandShakeFlag = DatagramFlag.CON | DatagramFlag.SYN;
    public const DatagramFlag SecondHandShakeFlag = DatagramFlag.CON | DatagramFlag.SYN | DatagramFlag.ACK;
    public const DatagramFlag ThirdHandShakeFlag = DatagramFlag.CON | DatagramFlag.ACK;

    [MemoryPackConstructor]
    public TransDatagram(DatagramFlag flag, int synOrAck, ReadOnlyMemory<byte>? payload)
    {
        Flag = flag;
        SynOrAck = synOrAck;
        Payload = payload;
    }

    /// <summary>
    ///     创建Connect请求包，建立连接的第一次握手
    /// </summary>
    public static TransDatagram CreateShakeHandFirst(int synOrAck)
    {
        return new TransDatagram(FirstHandShakeFlag, synOrAck, null);
    }

    /// <summary>
    ///     创建ConnectSYNACK请求包，建立连接时的第二次握手
    /// </summary>
    public static TransDatagram CreateShakeHandSecond(int synOrAck)
    {
        return new TransDatagram(SecondHandShakeFlag, synOrAck, null);
    }

    /// <summary>
    ///     创建ConnectACK请求包，建立连接时的第三次握手
    /// </summary>
    public static TransDatagram CreateShakeHandThird(int synOrAck)
    {
        return new TransDatagram(ThirdHandShakeFlag, synOrAck, null);
    }

    public static TransDatagram CreateNormal(int syn, ReadOnlyMemory<byte> payload)
    {
        return new TransDatagram(DatagramFlag.SYN, syn, payload);
    }

    public static TransDatagram CreateAck(int ack)
    {
        return new TransDatagram(DatagramFlag.ACK, ack, null);
    }
}