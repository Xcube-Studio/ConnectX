namespace ConnectX.Shared.Interfaces;

public interface ISender
{
    void Send(ReadOnlyMemory<byte> payload);
    void SendData<T>(T data);
}