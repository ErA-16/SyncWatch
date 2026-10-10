namespace SyncWatch.Dtos
{
    public class UploadTicketRequest
    {
        public string FileName { get; set; } = null!;
        public long FileSize { get; set; }
        public string? ContentType { get; set; }
    }
}
