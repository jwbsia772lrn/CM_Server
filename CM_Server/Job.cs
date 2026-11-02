using System.Text.Json.Serialization;

namespace Chamelang.Backend.Models;

public record CreateRoomRequest(
    [property: JsonPropertyName("userId")] object? UserId,
    [property: JsonPropertyName("personaInstruction")] string? PersonaInstruction,
    [property: JsonPropertyName("lang")] string? Lang,
    [property: JsonPropertyName("targetLanguage")] string? TargetLanguage,
    [property: JsonPropertyName("roomName")] string? RoomName
)
{
    public string EffectiveLanguage => !string.IsNullOrWhiteSpace(TargetLanguage) ? TargetLanguage : (!string.IsNullOrWhiteSpace(Lang) ? Lang : "Korean");
}

public record CreateRoomResponse(string ChatRoomId);

public record SendMessageRequest(
    [property: JsonPropertyName("chatRoomId")] string ChatRoomId,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("userId")] object? UserId = null
);

public record ChatResponse(long UserMessageId, long AiMessageId, string AiReply, DateTime MsgTimestamp);

public record ChatMessageDto(long MessageId, string RoomId, string SenderType, string Content, DateTime MsgTimestamp);

public record UserDto(long UserId, string Username);
public record CreateUserRequest(
    [property: JsonPropertyName("userId")] object? UserId,
    [property: JsonPropertyName("username")] string? Username,
    [property: JsonPropertyName("userName")] string? UserName
);

public class ChatJob
{
    public SendMessageRequest Request { get; }
    public TaskCompletionSource<ChatResponse> CompletionSource { get; } = new();

    public ChatJob(SendMessageRequest request) => Request = request;
}