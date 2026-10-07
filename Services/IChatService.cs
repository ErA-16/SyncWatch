using SyncWatch.Models;

namespace SyncWatch.Services
{
    public interface IChatService
    {
        Task<ChatMessage> SendAsync(Guid roomId, string? displayName, string? message);

        Task<List<ChatMessage>> GetHistoryAsync(Guid roomId);
    }
}