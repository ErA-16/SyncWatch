namespace SyncWatch.Models
{
    public class Participant
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid RoomId { get; set; }
        public Room Room { get; set; } = null!;
        public string Token { get; set; } = null!;
        public string? DisplayName { get; set; }
        public bool IsHost { get; set; }
        public ParticipantStatus Status { get; set; } = ParticipantStatus.Offline;
    }
}
