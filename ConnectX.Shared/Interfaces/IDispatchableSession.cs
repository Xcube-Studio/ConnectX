using Hive.Both.General.Dispatchers;
using Hive.Network.Abstractions.Session;

namespace ConnectX.Shared.Interfaces;

public interface IDispatchableSession : IDisposable
{
    ISession Session { get; }
    IDispatcher Dispatcher { get; }
}