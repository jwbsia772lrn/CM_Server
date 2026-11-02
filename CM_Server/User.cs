namespace Chamelang.Backend.Models;

public class User
{
    public long UserId { get; set; }
    public string Username { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
