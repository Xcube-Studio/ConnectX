using ConnectX.Shared.Messages.P2P;

namespace ConnectX.Shared.Models;

public class PortPredictResult
{
    public PortPredictResult(
        int portLower,
        int portUpper,
        int diff,
        ChangeLaws law,
        bool sameIfNotConflict,
        int currentUsed)
    {
        PortLower = portLower;
        PortUpper = portUpper;
        Diff = diff;
        Law = law;
        SameIfNotConflict = sameIfNotConflict;
        CurrentUsed = currentUsed;
    }

    public int PortLower { get; }
    public int PortUpper { get; }


    /// <summary>
    /// difference
    /// </summary>
    public int Diff { get; }


    /// <summary>
    /// Port change pattern
    /// </summary>
    public ChangeLaws Law { get; }

    /// <summary>
    ///     有些NAT在端口没有冲突的情况下，PrivatePort和PublicPort相同
    /// </summary>
    public bool SameIfNotConflict { get; }


    /// <summary>
    ///     最后一个使用的端口
    /// </summary>
    public int CurrentUsed { get; }

    public P2PConContextInit ToP2PConInfoInit()
    {
        return new P2PConContextInit
        {
            ChangeLaw = Law,
            Diff = Diff,
            PublicPortLower = (ushort)PortLower,
            PublicPortUpper = (ushort)PortUpper,
            SameIfNotConflict = SameIfNotConflict,
            CurrentUsedPort = (ushort)CurrentUsed
        };
    }

    public static PortPredictResult FromP2PConContext(P2PConContext conInfo)
    {
        return new PortPredictResult(
            conInfo.PublicPortLower,
            conInfo.PublicPortUpper,
            conInfo.Diff,
            conInfo.ChangeLaw,
            conInfo.SameIfNotConflict,
            conInfo.CurrentUsedPort);
    }
}