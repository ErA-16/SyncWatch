namespace SyncWatch.Models
{
    public class ChatMessage
    {
        // v7 ids sort by creation time, so history orders by insertion even when
        // two messages land in the same clock tick. SentAt alone can't do that —
        // the system clock is far too coarse to break the tie.
        public Guid Id { get; set; } = Guid.CreateVersion7();
        public Guid RoomId { get; set; }
        public Room Room { get; set; } = null!;
        public string DisplayName { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public DateTime SentAt { get; set; } = DateTime.UtcNow;
    }
}