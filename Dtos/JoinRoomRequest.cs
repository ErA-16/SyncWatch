namespace SyncWatch.Dtos
{
    public class JoinRoomRequest
    {
        public string RoomCode { get; set; } = null!;
        public string? DisplayName { get; set; }
    }
}
