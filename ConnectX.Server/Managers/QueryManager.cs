using ConnectX.Shared.Messages.Query;
using ConnectX.Shared.Messages.Query.Response;
using ConnectX.Shared.Models;
using Hive.Both.General.Dispatchers;

namespace ConnectX.Server.Managers;

public class QueryManager
{
    public async Task ProcessQuery(MessageContext<TempQuery> ctx)
    {
        switch (ctx.Message.OpCode)
        {
            case QueryOps.PublicPort:
                await ctx.Dispatcher.SendAsync(
                    ctx.FromSession,
                    new PublicPortQueryResult
                    {
                        Port = (ushort)(ctx.FromSession.RemoteEndPoint?.Port ?? 0)
                    });
                break;
            default:
                return;
        }
    }
}