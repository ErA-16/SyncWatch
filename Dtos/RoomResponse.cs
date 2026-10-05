using SyncWatch.Models;

namespace SyncWatch.Dtos
{
    public class RoomResponse
    {
        public Guid RoomId { get; set; }
        public string Code { get; set; } = null!;
        public RoomStatus Status { get; set; }
        public List<ParticipantSummary> Participants { get; set; } = new();
        public List<MovieSummary> Movies { get; set; } = new();
    }

    public class ParticipantSummary
    {
        public string? DisplayName { get; set; }
        public bool IsHost { get; set; }
        public ParticipantStatus Status { get; set; }
    }

    public class MovieSummary
    {
        public Guid Id { get; set; }
        public string Title { get; set; } = null!;
        public int EpisodeNumber { get; set; }
        public double Duration { get; set; }
    }
}
