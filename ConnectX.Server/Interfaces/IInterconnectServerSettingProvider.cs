using System.Collections.Frozen;
using System.Net;

namespace ConnectX.Server.Interfaces;

public interface IInterconnectServerSettingProvider
{
    FrozenSet<IPEndPoint> EndPoints { get; }
}