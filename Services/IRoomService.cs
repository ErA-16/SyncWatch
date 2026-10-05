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

        Task<bool> SetParticipantStatusAsync(string token, ParticipantStatus status);
    }
}
