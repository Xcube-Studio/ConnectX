using Hive.Codec.Shared;
using MemoryPack;
using STUN.Enums;

namespace ConnectX.Shared.Messages.Identity;

[MessageDefine]
[MemoryPackable]
public partial class SigninMessage
{
    public required BindingTestResult BindingTestResult { get; init; }

    public required MappingBehavior MappingBehavior { get; init; }

    public required FilteringBehavior FilteringBehavior { get; init; }
    
    //识别身份, 如果为空则为与服务器通信的子链接；不为空则为建立P2P连接用的辅助连接
    public Guid Id { get; set; } = Guid.Empty;
}