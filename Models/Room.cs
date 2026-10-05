namespace SyncWatch.Models
{
    public class Room
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Code { get; set; } = string.Empty;
        public RoomStatus Status { get; set; } = RoomStatus.Waiting;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime ExpiresAt { get; set; } = DateTime.UtcNow.AddDays(3);

        public List<Movie> Movies { get; set; } = new();
        public List<Participant> Participants { get; set; } = new();
    }
}
