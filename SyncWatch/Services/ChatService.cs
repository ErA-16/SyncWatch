using Microsoft.EntityFrameworkCore;
using SyncWatch.Data;
using SyncWatch.Models;
using SyncWatch.Services.Exception;

namespace SyncWatch.Services
{
    public class ChatService : IChatService
    {
        // Matches the maxlength on the chat input. Enforced here as well because
        // the Hub method is callable without the browser.
        public const int MaxMessageLength = 300;

        // A room lives three days, so history is trimmed on the way in rather than
        // growing unbounded for every room the server has ever seen.
        public const int HistoryLimit = 200;

        private readonly DatabaseContext _db;
        private readonly ILogger<ChatService> _logger;

        public ChatService(DatabaseContext db, ILogger<ChatService> logger)
        {
            _db = db;
            _logger = logger;
        }

        public async Task<ChatMessage> SendAsync(Guid roomId, string? displayName, string? message)
        {
            var text = message?.Trim() ?? string.Empty;

            if (text.Length == 0)
            {
                _logger.LogDebug("Chat message rejected for room {RoomId}: empty.", roomId);
                throw new EmptyMessageException();
            }

            if (text.Length > MaxMessageLength)
            {
                _logger.LogWarning(
                    "Chat message rejected for room {RoomId}: {Length} characters.", roomId, text.Length);
                throw new MessageTooLongException();
            }

            if (!await _db.Rooms.AnyAsync(r => r.Id == roomId))
            {
                _logger.LogWarning("Chat message rejected: room {RoomId} does not exist.", roomId);
                throw new RoomNotAcceptingMessagesException();
            }

            var chatMessage = new ChatMessage
            {
                RoomId = roomId,
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? "Guest" : displayName.Trim(),
                Message = text,
                SentAt = DateTime.UtcNow
            };

            _db.ChatMessages.Add(chatMessage);
            await _db.SaveChangesAsync();

            await TrimHistoryAsync(roomId);

            return chatMessage;
        }

        public async Task<List<ChatMessage>> GetHistoryAsync(Guid roomId)
        {
            return await _db.ChatMessages
                .Where(m => m.RoomId == roomId)
                .OrderBy(m => m.Id)
                .Take(HistoryLimit)
                .ToListAsync();
        }

        private async Task TrimHistoryAsync(Guid roomId)
        {
            var stale = await _db.ChatMessages
                .Where(m => m.RoomId == roomId)
                .OrderByDescending(m => m.Id)
                .Skip(HistoryLimit)
                .ToListAsync();

            if (stale.Count == 0)
            {
                return;
            }

            _db.ChatMessages.RemoveRange(stale);
            await _db.SaveChangesAsync();
        }
    }
}