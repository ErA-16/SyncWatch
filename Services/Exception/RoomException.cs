namespace SyncWatch.Services.Exception
{
    public class HostNameRequiredException : System.Exception
    {
        public HostNameRequiredException(string message = "Host Name is required") : base(message) { }
    }
    
    public class RoomNotFoundException : System.Exception
    {
        public RoomNotFoundException(string message = "No room with Id Found") : base(message) { }
    }

    public class MaximumRoomCapacityReachedException : System.Exception
    {
        public MaximumRoomCapacityReachedException(string message = "This room is full") : base(message) { }
    }

    public class ForbiddenException : System.Exception
    {
        public ForbiddenException(string message = "Access denied") : base(message) { }
    }

    public class ParticipantNotFoundException : System.Exception
    {
        public ParticipantNotFoundException(string message = "Participant not found") : base(message) { }
    }

}
