using ConnectX.Shared.Helpers;
using Hive.Both.General.Dispatchers;
using Hive.Network.Abstractions.Session;

namespace ConnectX.Shared.Models;

public class DispatchableSession : IDisposable
{
    public ISession Session { get; init; }
    public IDispatcher Dispatcher { get; init; }
    
    public DispatchableSession(
        ISession session,
        IDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        Session = session;
        Dispatcher = dispatcher;
        
        Session.BindTo(Dispatcher);
        Session.StartAsync(cancellationToken).Forget();
    }

    public void Dispose()
    {
        Session.OnMessageReceived -= Dispatcher.Dispatch;
    }
}