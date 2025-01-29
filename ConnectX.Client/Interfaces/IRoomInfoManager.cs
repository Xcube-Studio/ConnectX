using ConnectX.Shared.Messages.Group;

namespace ConnectX.Client.Interfaces;

public interface IRoomInfoManager
{
    GroupInfo? CurrentGroupInfo { get; }

    void ClearRoomInfo();

    Task<GroupInfo?> AcquireGroupInfoAsync(Guid groupId);
}