using Microsoft.AspNetCore.Mvc;
using SyncWatch.Dtos;
using SyncWatch.Models;

namespace SyncWatch.Services
{
    public interface IRoomService
    {
        Task<CreateRoomResponse> CreateRoomAsync(CreateRoomRequest request);
        Task<JoinRoomResponse> JoinRoomAsync(JoinRoomRequest request);
        Task<bool> CloseRoomAsync(Guid id, string hostToken);
        Task<bool> LeaveRoomAsync(string token);
        Task<bool> ReconnectRoomAsync(string token);
        Task<RoomResponse> GetRoomAsync(string code);

        // Returns the participant's status BEFORE the change, or null when the
        // token belongs to nobody. The previous status is what lets a caller tell
        // a real transition from a redundant write: Join runs on every reconnect,
        // and treating those as news would announce people who never left.
        Task<ParticipantStatus?> SetParticipantStatusAsync(string token, ParticipantStatus status);

        // Used by the Hub to name the person in a presence broadcast. Null when
        // the token belongs to nobody.
        Task<string?> GetDisplayNameAsync(string token);
    }
}
