namespace SyncWatch.Models
{
    public class Movie
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid RoomId { get; set; }
        public Room Room { get; set; } = null!;
        public string FileName { get; set; } = null!;
        public int EpisodeNumber { get; set; }
        public string Title { get; set; } = null!;
        public long FileSize { get; set; }
        public string ContentType { get; set; } = "video/mp4";
        public string StorageLocation { get; set; } = null!;
        public double Duration { get; set; }
        public MovieUploadStatus Status { get; set; } = MovieUploadStatus.Uploading;
    }
}
