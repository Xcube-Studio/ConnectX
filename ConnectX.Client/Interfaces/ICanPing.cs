using ConnectX.Client.Route.Packet;
using Hive.Both.General.Dispatchers;

namespace ConnectX.Client.Interfaces;

public interface ICanPing<out TId>
{
    TId To { get; }
    IDispatcher Dispatcher { get; }
    void SendPingPacket<T>(T packet) where T : RouteLayerPacket;
}