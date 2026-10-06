namespace SyncWatch.Dtos
{
    public class CompleteUploadRequest
    {
        public string ObjectKey { get; set; } = null!;
        public string FileName { get; set; } = null!;

        // The server trusts R2's reported size/content type, not these, but it does
        // sanity-check that the client is talking about the file it just uploaded.
        public long FileSize { get; set; }
        public string? ContentType { get; set; }

        // Read in the browser from the video element's metadata; the server can no
        // longer sniff the container because the bytes never pass through it.
        public double? DurationSeconds { get; set; }
    }
}
