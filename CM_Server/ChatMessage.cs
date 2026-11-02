namespace Chamelang.Backend.Models;

public class ChatMessage
{
    public long MessageId { get; set; }
    public string RoomId { get; set; } = string.Empty;
    public string SenderType { get; set; } = string.Empty; // "USER" or "AI"
    public string Content { get; set; } = string.Empty;
    public DateTime MsgTimestamp { get; set; } = DateTime.UtcNow;

    public ChatRoom? ChatRoom { get; set; }
}
