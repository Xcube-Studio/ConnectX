using Hive.Network.Abstractions.Session;

namespace ConnectX.Relay.Helpers;

public static class SessionHelper
{
    public static bool IsSameSession(this ISession session, ISession other)
    {
        if (session.RemoteEndPoint == null && other.RemoteEndPoint != null)
            return false;
        if (session.RemoteEndPoint != null && other.RemoteEndPoint == null)
            return false;
        if (session.RemoteEndPoint == null && other.RemoteEndPoint == null)
            return session.Id == other.Id;
        if (session.RemoteEndPoint == null || other.RemoteEndPoint == null)
            return false;

        var isAddressSame = session.RemoteEndPoint.Address.Equals(other.RemoteEndPoint.Address);
        var isPortSame = session.RemoteEndPoint.Port == other.RemoteEndPoint.Port;

        return isAddressSame && isPortSame;
    }
}