namespace SyncWatch.Dtos
{
    public class CreateRoomResponse
    {
        public Guid RoomId { get; set; }
        public string RoomCode { get; set; } = null!;
        public Guid ParticipantId { get; set; }
        public string Token { get; set; } = null!;
    }
}
