using Hive.Both.General.Dispatchers;
using Hive.Network.Abstractions.Session;

namespace ConnectX.Shared.Models;

public class InitializedDispatchableSession : IDisposable
{
    public InitializedDispatchableSession(
        ISession session,
        IDispatcher dispatcher)
    {
        Session = session;
        Dispatcher = dispatcher;

        Session.BindTo(Dispatcher);
    }

    public ISession Session { get; }
    public IDispatcher Dispatcher { get; }

    public void Dispose()
    {
        Session.OnMessageReceived -= Dispatcher.Dispatch;
    }
}