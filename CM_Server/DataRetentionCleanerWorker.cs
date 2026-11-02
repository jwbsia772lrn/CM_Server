using Chamelang.Backend.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Chamelang.Backend.Services;

/// <summary>
/// 데이터 보존 정책(Data Retention Policy) 백그라운드 청소 스케줄러
/// - 30일 이상 비활성 대화방 정리: 방은 보존하고 최신 20건을 제외한 오래된 메시지 일괄 삭제
/// - 방 단위 독립 트랜잭션 및 비동기 간격 분산으로 DB 부하 및 락 경합 방지
/// </summary>
public class DataRetentionCleanerWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<DataRetentionCleanerWorker> _logger;

    public DataRetentionCleanerWorker(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<DataRetentionCleanerWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[DataRetentionCleaner] 데이터 보존 주기 스케줄러가 시작되었습니다.");

        // 서버 기동 직후 DB 경합을 피하기 위해 초기 대기 (1분)
        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CleanInactiveRoomsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[DataRetentionCleaner] 주기적 청소 작업 중 예외 발생");
            }

            // 설정된 주기 (기본 24시간) 마다 반복 실행
            int intervalHours = _configuration.GetValue("DataRetention:CleanIntervalHours", 24);
            _logger.LogInformation("[DataRetentionCleaner] 다음 청소 작업까지 {Hours}시간 대기합니다.", intervalHours);
            await Task.Delay(TimeSpan.FromHours(intervalHours), stoppingToken);
        }
    }

    /// <summary>
    /// 30일 이상 대화가 없는 장기 비활성 방의 오래된 메시지를 정리 (최신 20건 보존)
    /// </summary>
    private async Task CleanInactiveRoomsAsync(CancellationToken cancellationToken)
    {
        int inactiveDays = _configuration.GetValue("DataRetention:InactiveDaysThreshold", 30);
        var cutoffDate = DateTime.UtcNow.AddDays(-inactiveDays);

        _logger.LogInformation(
            "[DataRetentionCleaner] 장기 비활성 대화방 정리 시작 (기준일시: {Cutoff:yyyy-MM-dd HH:mm:ss}, {Days}일 전)",
            cutoffDate, inactiveDays);

        List<string> targetRoomIds;
        using (var scope = _scopeFactory.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // LAST_ACTIVE_AT 또는 CREATED_AT 기준 30일 경과 방 탐색
            targetRoomIds = await dbContext.ChatRooms
                .Where(r => (r.LastActiveAt ?? r.CreatedAt) <= cutoffDate)
                .Select(r => r.RoomId)
                .ToListAsync(cancellationToken);
        }

        if (targetRoomIds.Count == 0)
        {
            _logger.LogInformation("[DataRetentionCleaner] 정리 대상인 30일 이상 비활성 방이 없습니다.");
            return;
        }

        _logger.LogInformation("[DataRetentionCleaner] 총 {Count}개의 비활성 방을 순회하여 정리합니다.", targetRoomIds.Count);

        int totalDeletedMessages = 0;
        int cleanedRoomsCount = 0;

        // [안전성 및 락 경합 방지] 대량 전체 잠금을 방지하기 위해 방 단위로 분할 처리
        foreach (var roomId in targetRoomIds)
        {
            if (cancellationToken.IsCancellationRequested) break;

            try
            {
                using var scope = _scopeFactory.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                // 각 방 단위 독립 트랜잭션으로 격리
                using var tx = await dbContext.Database.BeginTransactionAsync(cancellationToken);

                // 최신 20건(AI 컨텍스트용)을 제외한 오래된 메시지 일괄 DELETE
                string deleteSql = @"
                    DELETE FROM ""CHAT_MESSAGES""
                    WHERE ""ROOM_ID"" = :p0
                      AND ""MESSAGE_ID"" NOT IN (
                          SELECT ""MESSAGE_ID""
                          FROM ""CHAT_MESSAGES""
                          WHERE ""ROOM_ID"" = :p0
                          ORDER BY ""MSG_TIMESTAMP"" DESC, ""MESSAGE_ID"" DESC
                          FETCH FIRST 20 ROWS ONLY
                      )";

                int deleted = await dbContext.Database.ExecuteSqlRawAsync(deleteSql, [roomId], cancellationToken);
                await tx.CommitAsync(cancellationToken);

                if (deleted > 0)
                {
                    totalDeletedMessages += deleted;
                    cleanedRoomsCount++;
                    _logger.LogDebug(
                        "[DataRetentionCleaner] 비활성 대화방({RoomId}) 정리 완료: 오래된 메시지 {Deleted}건 삭제 (최신 20건 유지)",
                        roomId, deleted);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[DataRetentionCleaner] 대화방({RoomId}) 정리 중 오류 발생 (해당 방 건너뜀)", roomId);
            }

            // DB I/O 부하 분산을 위한 방 간 짧은 지연 (100ms)
            await Task.Delay(100, cancellationToken);
        }

        _logger.LogInformation(
            "[DataRetentionCleaner] 장기 비활성 방 청소 완료: 총 {Rooms}개 방에서 오래된 메시지 {Count}건 삭제",
            cleanedRoomsCount, totalDeletedMessages);
    }
}
