namespace SyncWatch.Services.Exception
{
    public class EmptyMessageException : System.Exception
    {
        public EmptyMessageException(string message = "Message cannot be empty.") : base(message) { }
    }

    public class MessageTooLongException : System.Exception
    {
        public MessageTooLongException(string message = "That message is too long.") : base(message) { }
    }

    public class RoomNotAcceptingMessagesException : System.Exception
    {
        public RoomNotAcceptingMessagesException(string message = "This room no longer exists.") : base(message) { }
    }
}