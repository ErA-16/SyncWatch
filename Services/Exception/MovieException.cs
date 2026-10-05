namespace SyncWatch.Services.Exception
{
    public class EmptyFileException : System.Exception
    {
        public EmptyFileException(string message = "No file uploaded.") : base(message) { }
    }

    public class InvalidFileTypeException : System.Exception
    {
        public InvalidFileTypeException(string message = "Invalid file type") : base(message) { }
    }

    public class StorageLimitException : System.Exception
    {
        public StorageLimitException(string message = "Uploading this file exceeds the 500 MB room storage limit") : base(message) { }
    }

    public class MovieNotFoundException : System.Exception
    {
        public MovieNotFoundException(string message = "Movie file not found on disk") : base(message) { }
    }
}
