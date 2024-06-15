using Hive.Network.Abstractions.Session;

namespace ConnectX.Server;

public class WatchDog(ISession session)
{
    /// <summary>
    ///     Max heartbeat interval in seconds.
    /// </summary>
    public const int MaxHeartbeatInterval = 15;

    /// <summary>
    ///     Last heartbeat time.
    /// </summary>
    public DateTime LastHeartbeat { get; private set; } = DateTime.UtcNow;

    public ISession Session { get; init; } = session;

    public void Received()
    {
        LastHeartbeat = DateTime.UtcNow;
    }

    public bool IsTimeoutExceeded()
    {
        return (DateTime.UtcNow - LastHeartbeat).TotalSeconds > MaxHeartbeatInterval;
    }
}