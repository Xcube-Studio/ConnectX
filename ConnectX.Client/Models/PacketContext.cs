using ConnectX.Client.Route;

namespace ConnectX.Client.Models;

public class PacketContext
{
    public PacketContext(
        Guid senderId,
        RouterPacketDispatcher dispatcher)
    {
        SenderId = senderId;
        Dispatcher = dispatcher;
    }

    public RouterPacketDispatcher Dispatcher { get; }
    public Guid SenderId { get; }

    public void Reply(object data)
    {
        Dispatcher.Send(SenderId, data);
    }
}