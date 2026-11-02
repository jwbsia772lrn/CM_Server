using System.Threading.Channels;
using Chamelang.Backend.Data;
using Chamelang.Backend.Models;
using Chamelang.Backend.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Chamelang.Backend.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ChatController : ControllerBase
{
    private readonly ChannelWriter<ChatJob> _channelWriter;
    private readonly AppDbContext _dbContext;
    private readonly ChatSessionCacheService _cacheService;
    private readonly ILogger<ChatController> _logger;

    public ChatController(
        Channel<ChatJob> channel,
        AppDbContext dbContext,
        ChatSessionCacheService cacheService,
        ILogger<ChatController> logger)
    {
        _channelWriter = channel.Writer;
        _dbContext = dbContext;
        _cacheService = cacheService;
        _logger = logger;
    }

    /// <summary>
    /// 대화방 생성 (USER_ID 유효성 및 외래키 검증)
    /// </summary>
    [HttpPost("room")]
    public async Task<IActionResult> CreateRoom([FromBody] CreateRoomRequest request)
    {
        string? rawUserId = request.UserId?.ToString();
        if (string.IsNullOrWhiteSpace(rawUserId))
        {
            return BadRequest(new { error = "USER_ID_REQUIRED", message = "USER_ID는 필수 입력값입니다." });
        }

        if (!long.TryParse(rawUserId, out long numericUserId))
        {
            _logger.LogWarning("[FK 위반 차단] 유효하지 않은 형식의 USER_ID: {UserId}", rawUserId);
            return BadRequest(new
            {
                error = "INVALID_USER_ID",
                message = $"USER_ID('{rawUserId}')는 유효한 숫자 형식이어야 합니다. 외래키 제약조건(FK_CHAT_ROOMS_USER)에 의해 방 생성이 거부되었습니다."
            });
        }

        // 외래키 제약조건(FK_CHAT_ROOMS_USER) 사전 검증: 유저 존재 여부 확인
        var userExists = await _dbContext.Users.AnyAsync(u => u.UserId == numericUserId);
        if (!userExists)
        {
            _logger.LogWarning("[FK 위반 차단] DB에 존재하지 않는 USER_ID: {UserId}", numericUserId);
            return BadRequest(new
            {
                error = "USER_NOT_FOUND",
                message = $"존재하지 않는 유저 ID('{numericUserId}')입니다. 외래키 제약조건(FK_CHAT_ROOMS_USER)에 의해 방 생성이 거부되었습니다."
            });
        }

        string roomId = "room_" + Guid.NewGuid().ToString("N")[..8];

        var room = new ChatRoom
        {
            RoomId = roomId,
            UserId = numericUserId,
            RoomName = request.RoomName ?? "기본 대화방",
            PersonaInstruction = request.PersonaInstruction ?? "Helpful Assistant",
            TargetLanguage = request.EffectiveLanguage,
            CreatedAt = DateTime.UtcNow
        };

        try
        {
            _dbContext.ChatRooms.Add(room);
            await _dbContext.SaveChangesAsync();
            _logger.LogInformation("[대화방 생성 성공] RoomId: {RoomId}, UserId: {UserId}", roomId, numericUserId);

            // [인메모리 캐시 적재] 첫 대화 요청 시 DB 로드 지연(Cold Start) 제거
            _cacheService.RegisterNewRoom(roomId, room.PersonaInstruction, room.TargetLanguage);

            return Ok(new CreateRoomResponse(roomId));
        }
        catch (DbUpdateException ex)
        {
            _logger.LogError(ex, "[DB 에러] 대화방 생성 실패");
            return StatusCode(500, new { error = "DB_ERROR", message = "데이터베이스 제약조건 위반 또는 저장 오류가 발생했습니다.", detail = ex.InnerException?.Message ?? ex.Message });
        }
    }

    /// <summary>
    /// 대화방 정보 조회 (재접속 및 영속성 검증용)
    /// </summary>
    [HttpGet("room/{roomId}")]
    public async Task<IActionResult> GetRoom(string roomId)
    {
        if (string.IsNullOrWhiteSpace(roomId))
        {
            return BadRequest(new { error = "INVALID_ROOM_ID", message = "방 ID가 유효하지 않습니다." });
        }

        var room = await _dbContext.ChatRooms.FirstOrDefaultAsync(r => r.RoomId == roomId);
        if (room == null)
        {
            return NotFound(new { error = "ROOM_NOT_FOUND", message = $"대화방을 찾을 수 없습니다. (ROOM_ID: {roomId})" });
        }

        return Ok(new
        {
            roomId = room.RoomId,
            userId = room.UserId,
            roomName = room.RoomName,
            personaInstruction = room.PersonaInstruction,
            lang = room.TargetLanguage,
            targetLanguage = room.TargetLanguage,
            createdAt = room.CreatedAt,
            lastActiveAt = room.LastActiveAt
        });
    }

    /// <summary>
    /// 대화방 이전 대화 기록 목록 조회 (시간순 정렬)
    /// </summary>
    [HttpGet("room/{roomId}/messages")]
    public async Task<IActionResult> GetMessages(string roomId)
    {
        if (string.IsNullOrWhiteSpace(roomId))
        {
            return BadRequest(new { error = "INVALID_ROOM_ID", message = "방 ID가 유효하지 않습니다." });
        }

        var roomExists = await _dbContext.ChatRooms.AnyAsync(r => r.RoomId == roomId);
        if (!roomExists)
        {
            return NotFound(new { error = "ROOM_NOT_FOUND", message = $"대화방을 찾을 수 없습니다. (ROOM_ID: {roomId})" });
        }

        var messages = await _dbContext.ChatMessages
            .Where(m => m.RoomId == roomId)
            .OrderBy(m => m.MsgTimestamp)
            .ThenBy(m => m.MessageId)
            .Select(m => new ChatMessageDto(m.MessageId, m.RoomId, m.SenderType, m.Content, m.MsgTimestamp))
            .ToListAsync();

        return Ok(messages);
    }

    /// <summary>
    /// 메시지 전송 (동기식 DB 쿼리를 제거하여 응답 지연 최소화)
    /// </summary>
    [HttpPost("message")]
    public async Task<IActionResult> SendMessage([FromBody] SendMessageRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ChatRoomId))
        {
            return BadRequest(new { error = "ROOM_ID_REQUIRED", message = "ChatRoomId는 필수입니다." });
        }

        if (string.IsNullOrWhiteSpace(request.Message))
        {
            return BadRequest(new { error = "MESSAGE_REQUIRED", message = "메시지 내용은 비어있을 수 없습니다." });
        }

        var job = new ChatJob(request);
        await _channelWriter.WriteAsync(job);

        try
        {
            ChatResponse response = await job.CompletionSource.Task;
            return Ok(response);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = "ROOM_NOT_FOUND", message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[메시지 처리 오류]");
            return StatusCode(500, new { error = "PROCESSING_ERROR", message = ex.Message });
        }
    }

    /// <summary>
    /// 유저 조회/확인 (ID 숫자 또는 사용자명으로 조회 지원)
    /// </summary>
    [HttpGet("/api/user/{idOrName}")]
    public async Task<IActionResult> GetUser(string idOrName)
    {
        User? user = null;
        if (long.TryParse(idOrName, out long numericId))
        {
            user = await _dbContext.Users.FirstOrDefaultAsync(u => u.UserId == numericId);
        }
        
        if (user == null)
        {
            user = await _dbContext.Users.FirstOrDefaultAsync(u => u.Username == idOrName);
        }

        if (user == null)
        {
            return NotFound(new { error = "USER_NOT_FOUND", message = $"유저를 찾을 수 없습니다. (검색어: {idOrName})" });
        }

        return Ok(new UserDto(user.UserId, user.Username));
    }

    /// <summary>
    /// 유저 등록 (테스트 편의용)
    /// </summary>
    [HttpPost("/api/user")]
    public async Task<IActionResult> CreateUser([FromBody] CreateUserRequest request)
    {
        string? username = request.Username ?? request.UserName;
        if (string.IsNullOrWhiteSpace(username))
        {
            return BadRequest(new { error = "INVALID_USER_DATA", message = "Username을 입력해야 합니다." });
        }

        long userId;
        string? rawId = request.UserId?.ToString();
        if (!string.IsNullOrWhiteSpace(rawId) && long.TryParse(rawId, out long parsedId))
        {
            userId = parsedId;
        }
        else
        {
            // 최대 ID + 1로 자동 부여
            long maxId = await _dbContext.Users.Select(u => (long?)u.UserId).MaxAsync() ?? 0;
            userId = maxId + 1;
        }

        if (await _dbContext.Users.AnyAsync(u => u.UserId == userId))
        {
            return Conflict(new { error = "USER_ALREADY_EXISTS", message = $"이미 존재하는 유저 ID({userId})입니다." });
        }

        var user = new User
        {
            UserId = userId,
            Username = username,
            CreatedAt = DateTime.UtcNow
        };

        _dbContext.Users.Add(user);
        await _dbContext.SaveChangesAsync();

        return Ok(new UserDto(user.UserId, user.Username));
    }
}