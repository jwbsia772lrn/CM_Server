using System.Threading.Channels;
using Chamelang.Backend.Data;
using Chamelang.Backend.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Chamelang.Backend.Services;

/// <summary>
/// 백그라운드 DB 비동기 저장용 작업 단위
/// </summary>
public record DbMessageJob(long MessageId, string RoomId, string SenderType, string Content, DateTime MsgTimestamp);

/// <summary>
/// 고유 메시지 ID 발급기 (클라이언트 즉시 응답과 비동기 DB 저장 간 ID 일관성 보장)
/// </summary>
public class MessageIdGenerator
{
    private long _currentId;

    public void Initialize(long maxIdFromDb)
    {
        _currentId = Math.Max(maxIdFromDb, 0);
    }

    public long NextId()
    {
        return Interlocked.Increment(ref _currentId);
    }
}

/// <summary>
/// DB 영속 저장을 클라이언트 응답과 분리하여 비동기로 처리하는 백그라운드 워커 (Fire-and-Forget)
/// - LAST_ACTIVE_AT 갱신
/// - 250건 도달 시 최신 200건 유지 단기 버퍼 트리밍
/// </summary>
public class DbPersistenceBackgroundWorker : BackgroundService
{
    private readonly ChannelReader<DbMessageJob> _channelReader;
    private readonly ChatSessionCacheService _cacheService;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DbPersistenceBackgroundWorker> _logger;

    public DbPersistenceBackgroundWorker(
        Channel<DbMessageJob> channel,
        ChatSessionCacheService cacheService,
        IServiceScopeFactory scopeFactory,
        ILogger<DbPersistenceBackgroundWorker> logger)
    {
        _channelReader = channel.Reader;
        _cacheService = cacheService;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[DbPersistenceWorker] 백그라운드 DB 저장 워커가 시작되었습니다.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await _channelReader.WaitToReadAsync(stoppingToken))
                {
                    var batch = new List<DbMessageJob>();
                    while (batch.Count < 50 && _channelReader.TryRead(out var job))
                    {
                        batch.Add(job);
                    }

                    if (batch.Count > 0)
                    {
                        await PersistBatchAsync(batch, stoppingToken);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[DbPersistenceWorker] 메시지 비동기 저장 중 예외 발생");
            }
        }
    }

    private async Task PersistBatchAsync(List<DbMessageJob> batch, CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // 1. CHAT_MESSAGES 일괄 삽입
            foreach (var item in batch)
            {
                var entity = new ChatMessage
                {
                    MessageId = item.MessageId,
                    RoomId = item.RoomId,
                    SenderType = item.SenderType,
                    Content = item.Content,
                    MsgTimestamp = item.MsgTimestamp
                };
                dbContext.ChatMessages.Add(entity);
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            _logger.LogDebug("[DbPersistenceWorker] {Count}건의 메시지 DB 영속 저장 완료.", batch.Count);

            // 2. 방별 LAST_ACTIVE_AT 갱신 및 단기 버퍼 용량(250건 -> 200건) 트리밍 검사
            var distinctRoomIds = batch.Select(b => b.RoomId).Distinct().ToList();

            foreach (var roomId in distinctRoomIds)
            {
                var latestTime = batch.Where(b => b.RoomId == roomId).Max(b => b.MsgTimestamp);

                // 2-1. LAST_ACTIVE_AT 갱신
                await dbContext.Database.ExecuteSqlRawAsync(
                    "UPDATE \"CHAT_ROOMS\" SET \"LAST_ACTIVE_AT\" = :p0 WHERE \"ROOM_ID\" = :p1",
                    [latestTime, roomId],
                    cancellationToken);

                // 2-2. 단기 버퍼 트리밍 체크: 250건 이상 도달 시 배치 정리
                var session = _cacheService.TryGetCachedSession(roomId);
                if (session != null && session.CheckAndTriggerTrim(threshold: 250))
                {
                    await TrimRoomBufferAsync(dbContext, roomId, session, cancellationToken);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DbPersistenceWorker] DB 일괄 저장 또는 트리밍 실패 (메시지 {Count}건)", batch.Count);
        }
    }

    /// <summary>
    /// 단기 버퍼 용량 제한: 해당 방의 가장 오래된 메시지들을 정리하여 최신 200건만 유지
    /// </summary>
    private async Task TrimRoomBufferAsync(
        AppDbContext dbContext,
        string roomId,
        RoomChatSessionCache session,
        CancellationToken cancellationToken)
    {
        try
        {
            // 대량 DELETE 성능 및 락 경합 방지를 위해 원시 SQL과 트랜잭션 사용
            using var tx = await dbContext.Database.BeginTransactionAsync(cancellationToken);

            string sql = @"
                DELETE FROM ""CHAT_MESSAGES""
                WHERE ""ROOM_ID"" = :p0
                  AND ""MESSAGE_ID"" NOT IN (
                      SELECT ""MESSAGE_ID""
                      FROM ""CHAT_MESSAGES""
                      WHERE ""ROOM_ID"" = :p0
                      ORDER BY ""MSG_TIMESTAMP"" DESC, ""MESSAGE_ID"" DESC
                      FETCH FIRST 200 ROWS ONLY
                  )";

            int deletedCount = await dbContext.Database.ExecuteSqlRawAsync(sql, [roomId], cancellationToken);
            await tx.CommitAsync(cancellationToken);

            session.ResetTrimmedCount(remainingCount: 200);

            _logger.LogInformation(
                "[단기 버퍼 정리 완료] 대화방({RoomId})의 메시지가 250건에 도달하여 오래된 메시지 {Count}건을 정리했습니다. (최신 200건 유지)",
                roomId, deletedCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[단기 버퍼 정리 실패] 대화방({RoomId}) 정리 중 오류 발생", roomId);
        }
    }
}
