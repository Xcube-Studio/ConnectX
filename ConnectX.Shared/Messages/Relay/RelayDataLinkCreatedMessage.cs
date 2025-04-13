using Hive.Codec.Shared;
using MemoryPack;

namespace ConnectX.Shared.Messages.Relay;

[MessageDefine]
[MemoryPackable]
public partial class RelayDataLinkCreatedMessage;

[MessageDefine]
[MemoryPackable]
public partial class RelayWorkerLinkCreatedMessage;