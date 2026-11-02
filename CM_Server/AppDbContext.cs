using Chamelang.Backend.Models;
using Microsoft.EntityFrameworkCore;

namespace Chamelang.Backend.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<User> Users => Set<User>();
    public DbSet<ChatRoom> ChatRooms => Set<ChatRoom>();
    public DbSet<ChatMessage> ChatMessages => Set<ChatMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // USERS 테이블 (USER_ID, USERNAME, CREATED_AT)
        modelBuilder.Entity<User>(entity =>
        {
            entity.ToTable("USERS");
            entity.HasKey(e => e.UserId);
            entity.Property(e => e.UserId).HasColumnName("USER_ID");
            entity.Property(e => e.Username).HasColumnName("USERNAME").HasMaxLength(100);
            entity.Property(e => e.CreatedAt).HasColumnName("CREATED_AT");
        });

        // CHAT_ROOMS 테이블 (ROOM_ID, USER_ID, ROOM_NAME, PERSONA_INSTRUCTION, TARGET_LANGUAGE, CREATED_AT)
        modelBuilder.Entity<ChatRoom>(entity =>
        {
            entity.ToTable("CHAT_ROOMS");
            entity.HasKey(e => e.RoomId);
            entity.Property(e => e.RoomId).HasColumnName("ROOM_ID").HasMaxLength(50);
            entity.Property(e => e.UserId).HasColumnName("USER_ID").IsRequired();
            entity.Property(e => e.RoomName).HasColumnName("ROOM_NAME").HasMaxLength(100);
            entity.Property(e => e.PersonaInstruction).HasColumnName("PERSONA_INSTRUCTION");
            entity.Property(e => e.TargetLanguage).HasColumnName("TARGET_LANGUAGE").HasMaxLength(50);
            entity.Property(e => e.CreatedAt).HasColumnName("CREATED_AT");
            entity.Property(e => e.LastActiveAt).HasColumnName("LAST_ACTIVE_AT");

            entity.HasOne(e => e.User)
                  .WithMany()
                  .HasForeignKey(e => e.UserId)
                  .HasConstraintName("FK_CHAT_ROOMS_USER")
                  .OnDelete(DeleteBehavior.Restrict);
        });

        // CHAT_MESSAGES 테이블 (MESSAGE_ID, ROOM_ID, SENDER_TYPE, CONTENT, MSG_TIMESTAMP)
        modelBuilder.Entity<ChatMessage>(entity =>
        {
            entity.ToTable("CHAT_MESSAGES");
            entity.HasKey(e => e.MessageId);
            entity.Property(e => e.MessageId).HasColumnName("MESSAGE_ID").ValueGeneratedNever();
            entity.Property(e => e.RoomId).HasColumnName("ROOM_ID").HasMaxLength(50).IsRequired();
            entity.Property(e => e.SenderType).HasColumnName("SENDER_TYPE").HasMaxLength(10).IsRequired();
            entity.Property(e => e.Content).HasColumnName("CONTENT").IsRequired();
            entity.Property(e => e.MsgTimestamp).HasColumnName("MSG_TIMESTAMP");

            entity.HasOne(e => e.ChatRoom)
                  .WithMany(r => r.Messages)
                  .HasForeignKey(e => e.RoomId)
                  .HasConstraintName("FK_MESSAGES_ROOM")
                  .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
