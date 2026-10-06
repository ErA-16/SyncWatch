namespace SyncWatch.Dtos
{
    public class UploadTicketResponse
    {
        public string UploadUrl { get; set; } = null!;
        public string ObjectKey { get; set; } = null!;

        // Signed into the URL, so the client has to echo it back byte for byte.
        public string ContentType { get; set; } = null!;
        public int ExpiresInSeconds { get; set; }
    }
}
