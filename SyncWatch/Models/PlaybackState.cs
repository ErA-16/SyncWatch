namespace SyncWatch.Models
{
    public class PlaybackState
    {
        public Guid RoomId { get; set; }
        public Room Room { get; set; } = null!;
        public Guid CurrentMovieId { get; set; }
        public Movie Movie { get; set; } = null!;
        public PlaybackStatus Status { get; set; } = PlaybackStatus.Playing;
        public double Position { get; set; }
        public int Version { get; set; }
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }
}
