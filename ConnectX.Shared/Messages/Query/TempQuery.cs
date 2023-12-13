using ConnectX.Shared.Models;
using Hive.Codec.Shared;
using MemoryPack;

namespace ConnectX.Shared.Messages.Query;

[MessageDefine]
[MemoryPackable]
public partial class TempQuery(QueryOps opCode)
{
    public QueryOps OpCode { get; init; } = opCode;
}