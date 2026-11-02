using System.Collections.Concurrent;
using Chamelang.Backend.Data;
using Chamelang.Backend.Models;
using Microsoft.EntityFrameworkCore;

namespace Chamelang.Backend.Services;

public class CachedMessage
{
    public string SenderType { get; set; } = string.Empty; // "USER" or "AI"
    public string Content { get; set; } = string.Empty;
    public DateTime MsgTimestamp { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// 특정 대화방의 활성 인메모리 세션 캐시 (슬라이딩 윈도우 최대 20개 메시지 유지 및 단기 버퍼 카운트 추적)
/// </summary>
public class RoomChatSessionCache
{
    public string PersonaInstruction { get; set; } = string.Empty;
    public string TargetLanguage { get; set; } = string.Empty;

    // 대화방 누적 메시지 수 (단기 버퍼 트리밍 판단용)
    public int TotalMessageCount { get; private set; }

    private readonly List<CachedMessage> _recentMessages = new();
    private readonly object _lock = new();

    public void Initialize(string persona, string lang, IEnumerable<CachedMessage> history, int initialTotalCount)
    {
        lock (_lock)
        {
            PersonaInstruction = persona;
            TargetLanguage = lang;
            TotalMessageCount = initialTotalCount;
            _recentMessages.Clear();
            _recentMessages.AddRange(history.TakeLast(20));
        }
    }

    /// <summary>
    /// 현재 캐시된 최근 대화 내역 스냅샷 반환 (최대 20개)
    /// </summary>
    public List<CachedMessage> GetHistorySnapshot()
    {
        lock (_lock)
        {
            return new List<CachedMessage>(_recentMessages);
        }
    }

    /// <summary>
    /// 새 메시지를 캐시에 추가하고 슬라이딩 윈도우 유지 및 누적 메시지 카운트 증가
    /// </summary>
    public void AddMessage(string senderType, string content, DateTime timestamp)
    {
        lock (_lock)
        {
            _recentMessages.Add(new CachedMessage
            {
                SenderType = senderType,
                Content = content,
                MsgTimestamp = timestamp
            });

            while (_recentMessages.Count > 20)
            {
                _recentMessages.RemoveAt(0);
            }

            TotalMessageCount++;
        }
    }

    /// <summary>
    /// 단기 버퍼 임계치(250건) 도달 여부 확인
    /// </summary>
    public bool CheckAndTriggerTrim(int threshold = 250)
    {
        lock (_lock)
        {
            return TotalMessageCount >= threshold;
        }
    }

    /// <summary>
    /// 트리밍 완료 후 카운터 재조정 (200건으로 리셋)
    /// </summary>
    public void ResetTrimmedCount(int remainingCount = 200)
    {
        lock (_lock)
        {
            TotalMessageCount = remainingCount;
        }
    }
}

/// <summary>
/// 대화방별 인메모리 캐시 관리 서비스 (Active Session Cache)
/// </summary>
public class ChatSessionCacheService
{
    private readonly ConcurrentDictionary<string, RoomChatSessionCache> _cache = new();
    private readonly ILogger<ChatSessionCacheService> _logger;

    public ChatSessionCacheService(ILogger<ChatSessionCacheService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// 대화방 세션을 메모리 캐시에서 가져오거나, 미존재 시 DB에서 최근 20개 메시지를 로드
    /// </summary>
    public async Task<RoomChatSessionCache> GetOrLoadSessionAsync(string roomId, AppDbContext dbContext)
    {
        if (_cache.TryGetValue(roomId, out var existing))
        {
            return existing;
        }

        // 캐시 미스 시 DB에서 Cold Load (방 정보, 최근 20개 메시지 및 전체 메시지 수)
        _logger.LogInformation("[Cache Miss] 대화방({RoomId}) 정보를 DB에서 메모리로 로드합니다.", roomId);

        var room = await dbContext.ChatRooms.FirstOrDefaultAsync(r => r.RoomId == roomId);
        if (room == null)
        {
            throw new KeyNotFoundException($"존재하지 않는 대화방입니다. (ROOM_ID: {roomId})");
        }

        // 전체 메시지 건수 조회 (버퍼 제한 판단용)
        int totalCount = await dbContext.ChatMessages.CountAsync(m => m.RoomId == roomId);

        // 최근 20개 메시지 시간순 로드
        var recentFromDb = await dbContext.ChatMessages
            .Where(m => m.RoomId == roomId)
            .OrderByDescending(m => m.MsgTimestamp)
            .ThenByDescending(m => m.MessageId)
            .Take(20)
            .OrderBy(m => m.MsgTimestamp)
            .ThenBy(m => m.MessageId)
            .Select(m => new CachedMessage
            {
                SenderType = m.SenderType,
                Content = m.Content,
                MsgTimestamp = m.MsgTimestamp
            })
            .ToListAsync();

        var session = new RoomChatSessionCache();
        session.Initialize(room.PersonaInstruction, room.TargetLanguage, recentFromDb, totalCount);

        return _cache.GetOrAdd(roomId, session);
    }

    /// <summary>
    /// 방 생성 시 캐시에 사전 등록하여 첫 요청 시 DB 로드 지연 방지
    /// </summary>
    public void RegisterNewRoom(string roomId, string persona, string lang)
    {
        var session = new RoomChatSessionCache();
        session.Initialize(persona, lang, Enumerable.Empty<CachedMessage>(), initialTotalCount: 0);
        _cache[roomId] = session;
        _logger.LogInformation("[Cache Warmup] 대화방({RoomId}) 캐시 사전 등록 완료.", roomId);
    }

    public RoomChatSessionCache? TryGetCachedSession(string roomId)
    {
        _cache.TryGetValue(roomId, out var session);
        return session;
    }
}
