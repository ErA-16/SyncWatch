using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using SyncWatch.Data;
using SyncWatch.Models;
using SyncWatch.Services;
using SyncWatch.Services.Exception;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace SyncWatch.Tests
{
    public class ChatServiceTests
    {
        private ChatService CreateService(out DatabaseContext db, out Guid roomId)
        {
            db = TestHelper.CreateDbContext();
            roomId = Guid.NewGuid();
            db.Rooms.Add(new Room { Id = roomId, Code = "ABCDE" });
            db.SaveChanges();

            var logger = Mock.Of<ILogger<ChatService>>();
            return new ChatService(db, logger);
        }

        [Fact]
        public async Task SendAsync_ValidMessage_StoresTrimmedMessage()
        {
            var service = CreateService(out var db, out var roomId);

            var stored = await service.SendAsync(roomId, "Taiwo", "  this is peak  ");

            Assert.Equal("Taiwo", stored.DisplayName);
            Assert.Equal("this is peak", stored.Message);

            var row = await db.ChatMessages.FindAsync(stored.Id);
            Assert.NotNull(row);
            Assert.Equal(roomId, row!.RoomId);
        }

        [Fact]
        public async Task SendAsync_BlankDisplayName_FallsBackToGuest()
        {
            var service = CreateService(out _, out var roomId);

            var stored = await service.SendAsync(roomId, "   ", "hello");

            Assert.Equal("Guest", stored.DisplayName);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public async Task SendAsync_EmptyMessage_ThrowsEmptyMessageException(string message)
        {
            var service = CreateService(out _, out var roomId);

            await Assert.ThrowsAsync<EmptyMessageException>(
                () => service.SendAsync(roomId, "Taiwo", message));
        }

        [Fact]
        public async Task SendAsync_MessageLongerThanLimit_ThrowsMessageTooLongException()
        {
            var service = CreateService(out _, out var roomId);
            var tooLong = new string('x', ChatService.MaxMessageLength + 1);

            await Assert.ThrowsAsync<MessageTooLongException>(
                () => service.SendAsync(roomId, "Taiwo", tooLong));
        }

        [Fact]
        public async Task SendAsync_RoomDoesNotExist_ThrowsRoomNotAcceptingMessagesException()
        {
            var service = CreateService(out _, out var roomId);

            await Assert.ThrowsAsync<RoomNotAcceptingMessagesException>(
                () => service.SendAsync(Guid.NewGuid(), "Taiwo", "hello"));
        }

        [Fact]
        public async Task GetHistoryAsync_MessagesInRoom_ReturnsOldestFirst()
        {
            var service = CreateService(out var db, out var roomId);
            var otherRoomId = Guid.NewGuid();
            db.Rooms.Add(new Room { Id = otherRoomId, Code = "FGHIJ" });
            await db.SaveChangesAsync();

            await service.SendAsync(roomId, "Taiwo", "first");
            await service.SendAsync(otherRoomId, "Someone", "other room");
            await service.SendAsync(roomId, "Taiwo", "second");

            var history = await service.GetHistoryAsync(roomId);

            Assert.Equal(new[] { "first", "second" }, history.Select(m => m.Message));
        }

        [Fact]
        public async Task GetHistoryAsync_NoMessages_ReturnsEmpty()
        {
            var service = CreateService(out _, out var roomId);

            var history = await service.GetHistoryAsync(roomId);

            Assert.Empty(history);
        }

        [Fact]
        public async Task SendAsync_MoreMessagesThanHistoryLimit_TrimsOldest()
        {
            var service = CreateService(out var db, out var roomId);

            for (var i = 0; i < ChatService.HistoryLimit + 5; i++)
            {
                await service.SendAsync(roomId, "Taiwo", $"message {i}");
            }

            var history = await service.GetHistoryAsync(roomId);

            Assert.Equal(ChatService.HistoryLimit, history.Count);
            Assert.Equal("message 5", history[0].Message);
            Assert.Equal($"message {ChatService.HistoryLimit + 4}", history[^1].Message);
            Assert.Equal(ChatService.HistoryLimit, await db.ChatMessages.CountAsync());
        }
    }
}