using System.Threading.Channels;
using Chamelang.Backend.Data;
using Chamelang.Backend.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Chamelang.Backend.Services;

public class ChatBackgroundWorker : BackgroundService
{
    private readonly ChannelReader<ChatJob> _channelReader;
    private readonly GeminiService _geminiService;
    private readonly ChatSessionCacheService _cacheService;
    private readonly ChannelWriter<DbMessageJob> _dbChannelWriter;
    private readonly MessageIdGenerator _idGenerator;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ChatBackgroundWorker> _logger;

    public ChatBackgroundWorker(
        Channel<ChatJob> channel,
        GeminiService geminiService,
        ChatSessionCacheService cacheService,
        Channel<DbMessageJob> dbChannel,
        MessageIdGenerator idGenerator,
        IServiceScopeFactory scopeFactory,
        ILogger<ChatBackgroundWorker> logger)
    {
        _channelReader = channel.Reader;
        _geminiService = geminiService;
        _cacheService = cacheService;
        _dbChannelWriter = dbChannel.Writer;
        _idGenerator = idGenerator;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[ChatBackgroundWorker] 메시지 처리 워커가 시작되었습니다.");

        await foreach (var job in _channelReader.ReadAllAsync(stoppingToken))
        {
            try
            {
                string roomId = job.Request.ChatRoomId;
                string userMessage = job.Request.Message;
                DateTime userTimestamp = DateTime.UtcNow;

                // 1. 인메모리 세션 캐시에서 방 정보 및 최근 대화 20개 획득 (Cache Hit 시 DB 조회 없음)
                RoomChatSessionCache session;
                using (var scope = _scopeFactory.CreateScope())
                {
                    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    session = await _cacheService.GetOrLoadSessionAsync(roomId, dbContext);
                }

                // 2. 현재 요청 이전의 최근 20개 대화 스냅샷 획득 (슬라이딩 윈도우 컨텍스트)
                var historySnapshot = session.GetHistorySnapshot();

                // 3. 사용자 메시지 메모리 캐시 추가 및 ID 발급
                long userMsgId = _idGenerator.NextId();
                session.AddMessage("USER", userMessage, userTimestamp);

                // 4. Gemini API 호출 (최근 대화 20개 컨텍스트 포함)
                string aiReply = await _geminiService.GenerateReplyWithHistoryAsync(
                    session.PersonaInstruction,
                    session.TargetLanguage,
                    historySnapshot,
                    userMessage
                );

                DateTime aiTimestamp = DateTime.UtcNow;
                long aiMsgId = _idGenerator.NextId();

                // 5. AI 응답 메모리 캐시 추가 (슬라이딩 윈도우 자동 유지)
                session.AddMessage("AI", aiReply, aiTimestamp);

                // 6. [핵심 최적화] 클라이언트에게 즉시 응답 반환! (DB I/O 대기 0ms)
                job.CompletionSource.SetResult(new ChatResponse(
                    userMsgId,
                    aiMsgId,
                    aiReply,
                    aiTimestamp
                ));

                // 7. [비동기 분리] DB 영속 저장은 백그라운드 큐로 Fire-and-Forget 전달
                _dbChannelWriter.TryWrite(new DbMessageJob(userMsgId, roomId, "USER", userMessage, userTimestamp));
                _dbChannelWriter.TryWrite(new DbMessageJob(aiMsgId, roomId, "AI", aiReply, aiTimestamp));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ChatBackgroundWorker] 메시지 처리 중 오류 발생");
                job.CompletionSource.SetException(ex);
            }
        }
    }
}