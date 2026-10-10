namespace SyncWatch.Dtos
{
    public class JoinRoomResponse
    {
        public Guid RoomId { get; set; }
        public Guid ParticipantId { get; set; }
        public string Token { get; set; } = null!;
    }
}
