using System.ComponentModel.DataAnnotations;

namespace SyncWatch.Dtos
{
    public class CreateRoomRequest
    {
        [Required]
        public string HostName { get; set; } = null!;
    }
}
