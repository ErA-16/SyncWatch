namespace SyncWatch.Services.Exception
{
    public class PlaybackStateNotFoundException : System.Exception
    {
        public PlaybackStateNotFoundException(string message = "No playback state exists for this room yet.") : base(message) { }
    }
}
