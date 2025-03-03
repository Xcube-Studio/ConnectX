using ConnectX.Server.Models.DataBase;
using Microsoft.EntityFrameworkCore;

namespace ConnectX.Server.Models.Contexts;

public sealed class RoomOpsHistoryContext(DbContextOptions<RoomOpsHistoryContext> option) : DbContext(option)
{
    public DbSet<RoomCreateHistory> RoomCreateHistories { get; set; }

    public DbSet<RoomJoinHistory> RoomJoinHistories { get; set; }
}