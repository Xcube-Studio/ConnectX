using System.Collections.Concurrent;
using ConnectX.Server.Models.Contexts;
using ConnectX.Server.Models.DataBase;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ConnectX.Server.Services;

public record RoomRecord(
    Guid CreatedBy,
    Guid RoomId,
    DateTime CreatedTime,
    string RoomName,
    string UserDisplayName,
    string? RoomDescription,
    string? RoomPassword,
    int MaxUserCount);

public class RoomCreationRecordService : BackgroundService
{
    private readonly ConcurrentQueue<RoomRecord> _roomRecords = new();

    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly ILogger<RoomCreationRecordService> _logger;

    public RoomCreationRecordService(
        IServiceScopeFactory serviceScopeFactory,
        ILogger<RoomCreationRecordService> logger)
    {
        _serviceScopeFactory = serviceScopeFactory;
        _logger = logger;
    }

    public void CreateRecord(RoomRecord roomRecord)
    {
        _roomRecords.Enqueue(roomRecord);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using var scope = _serviceScopeFactory.CreateAsyncScope();

        var dbContext = scope.ServiceProvider.GetRequiredService<RoomOpsHistoryContext>();

        while (!stoppingToken.IsCancellationRequested)
        {
            if (_roomRecords.IsEmpty)
            {
                await Task.Delay(500, stoppingToken);
                continue;
            }

            if (!_roomRecords.TryDequeue(out var roomRecord))
            {
                await Task.Delay(500, stoppingToken);
                continue;
            }

            var history = new RoomCreateHistory
            {
                CreatedBy = roomRecord.CreatedBy,
                CreatedTime = roomRecord.CreatedTime,
                RoomId = roomRecord.RoomId,
                RoomName = roomRecord.RoomName,
                UserDisplayName = roomRecord.UserDisplayName,
                RoomDescription = roomRecord.RoomDescription,
                RoomPassword = roomRecord.RoomPassword,
                MaxUserCount = roomRecord.MaxUserCount
            };

            dbContext.RoomCreateHistories.Add(history);
            await dbContext.SaveChangesAsync(stoppingToken);

            _logger.LogRoomCreationHistoryCreated(roomRecord.CreatedBy, roomRecord.RoomName, roomRecord.UserDisplayName);
        }
    }
}

internal static partial class RoomCreationRecordServiceLoggers
{
    [LoggerMessage(LogLevel.Information, "Room creation history created. CreatedBy: {CreatedBy}, RoomName: {RoomName}, UserDisplayName: {UserDisplayName}")]
    public static partial void LogRoomCreationHistoryCreated(this ILogger logger, Guid createdBy, string roomName, string userDisplayName);
}