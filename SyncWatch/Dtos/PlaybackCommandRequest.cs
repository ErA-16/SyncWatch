namespace SyncWatch.Dtos
{
    public class PlaybackCommandRequest
    {
        public Guid MovieId { get; set; }
        public double Position { get; set; }
        public int Version { get; set; }
    }
}
