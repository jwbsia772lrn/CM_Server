namespace Chamelang.Backend.Models;

public class ChatRoom
{
    public string RoomId { get; set; } = string.Empty;
    public long UserId { get; set; }
    public string? RoomName { get; set; }
    public string PersonaInstruction { get; set; } = string.Empty;
    public string TargetLanguage { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastActiveAt { get; set; } = DateTime.UtcNow;

    public User? User { get; set; }
    public List<ChatMessage> Messages { get; set; } = new();
}
