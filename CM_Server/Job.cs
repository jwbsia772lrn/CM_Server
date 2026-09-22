namespace Chamelang.Backend.Models;

public record CreateRoomRequest(string PersonaInstruction, string Lang);
public record CreateRoomResponse(string ChatRoomId);

public record SendMessageRequest(string ChatRoomId, string Message);
public record ChatResponse(string AiReply);

public class ChatJob
{
    public SendMessageRequest Request { get; }
    public TaskCompletionSource<ChatResponse> CompletionSource { get; } = new();

    public ChatJob(SendMessageRequest request) => Request = request;
}