using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Moq;
using SyncWatch.Hubs;
using SyncWatch.Models;
using SyncWatch.Services;
using SyncWatch.Services.Exception;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SyncWatch.Tests
{
    /// <summary>
    /// A SignalR Hub has no test project reference of its own, so these tests
    /// substitute the three things a live connection would supply — Clients,
    /// Groups and Context — and drive the hub methods directly. What they exist
    /// to protect is the wiring inside OnDisconnectedAsync, which unit tests on
    /// the services below it cannot see: delete the pause call from the handler
    /// and the service tests still pass.
    /// </summary>
    public class PlaybackHubTests
    {
        private const string ConnectionId = "conn-1";

        private readonly Mock<IHubCallerClients> _clients = new();
        private readonly Mock<IClientProxy> _groupProxy = new();
        private readonly Mock<ISingleClientProxy> _callerProxy = new();
        private readonly Mock<IGroupManager> _groups = new();
        private readonly Mock<HubCallerContext> _context = new();
        private readonly Mock<IRoomService> _roomService = new();
        private readonly Mock<IPlaybackService> _playbackService = new();
        private readonly Mock<IChatService> _chatService = new();
        private readonly PresenceTracker _presence = new();

        private PlaybackHub CreateHub()
        {
            _clients.Setup(c => c.Group(It.IsAny<string>())).Returns(_groupProxy.Object);
            _clients.Setup(c => c.Caller).Returns(_callerProxy.Object);
            _groups.Setup(g => g.AddToGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            _context.Setup(c => c.ConnectionId).Returns(ConnectionId);

            return new PlaybackHub(
                _playbackService.Object,
                _roomService.Object,
                _chatService.Object,
                _presence,
                Mock.Of<ILogger<PlaybackHub>>())
            {
                Clients = _clients.Object,
                Groups = _groups.Object,
                Context = _context.Object
            };
        }

        // Moq records the hub's SendAsync calls as SendCoreAsync invocations.
        private static List<(string Method, object[] Args)> Sent<T>(Mock<T> proxy) where T : class
        {
            return proxy.Invocations
                .Where(i => i.Method.Name == nameof(IClientProxy.SendCoreAsync))
                .Select(i => (Method: (string)i.Arguments[0]!, Args: (object[]?)i.Arguments[1] ?? Array.Empty<object>()))
                .ToList();
        }

        private static object? Property(object payload, string name)
        {
            return payload.GetType().GetProperty(name)?.GetValue(payload);
        }

        private static PlaybackState PlayingState(Guid roomId)
        {
            return new PlaybackState
            {
                RoomId = roomId,
                CurrentMovieId = Guid.NewGuid(),
                Status = PlaybackStatus.Paused,
                Position = 424,
                Version = 9
            };
        }

        [Fact]
        public async Task OnDisconnectedAsync_ParticipantDrops_BroadcastsPresenceThenPausesRoom()
        {
            var hub = CreateHub();
            var roomId = Guid.NewGuid();
            _presence.Add(ConnectionId, roomId, "tok-1");
            _roomService.Setup(r => r.SetParticipantStatusAsync("tok-1", ParticipantStatus.Offline))
                .ReturnsAsync(ParticipantStatus.Online);
            _roomService.Setup(r => r.GetDisplayNameAsync("tok-1")).ReturnsAsync("Taiwo");
            _playbackService.Setup(p => p.PauseAtCurrentPositionAsync(roomId))
                .ReturnsAsync(PlayingState(roomId));

            await hub.OnDisconnectedAsync(null);

            var sent = Sent(_groupProxy);
            Assert.Equal(new[] { "ParticipantsUpdated", "PlaybackUpdated" }, sent.Select(s => s.Method));

            var presence = Assert.Single(sent[0].Args);
            Assert.Equal("Taiwo", Property(presence, "displayName"));
            Assert.Equal(ParticipantStatus.Offline, Property(presence, "status"));

            var paused = Assert.Single(sent[1].Args);
            Assert.Equal(PlaybackStatus.Paused, Property(paused, "Status"));
            Assert.Equal(424d, Property(paused, "Position"));

            _playbackService.Verify(p => p.PauseAtCurrentPositionAsync(roomId), Times.Once);
        }

        [Fact]
        public async Task OnDisconnectedAsync_AnotherTabStillOpen_LeavesRoomPlaying()
        {
            var hub = CreateHub();
            var roomId = Guid.NewGuid();
            _presence.Add(ConnectionId, roomId, "tok-1");
            _presence.Add("conn-2", roomId, "tok-1");
            _roomService.Setup(r => r.SetParticipantStatusAsync(It.IsAny<string>(), It.IsAny<ParticipantStatus>()))
                .ReturnsAsync(ParticipantStatus.Online);
            _playbackService.Setup(p => p.PauseAtCurrentPositionAsync(It.IsAny<Guid>()))
                .ReturnsAsync(PlayingState(roomId));

            await hub.OnDisconnectedAsync(null);

            // Same person, still watching in the other tab — nothing to announce.
            Assert.Empty(Sent(_groupProxy));
            _playbackService.Verify(p => p.PauseAtCurrentPositionAsync(It.IsAny<Guid>()), Times.Never);
        }

        [Fact]
        public async Task OnDisconnectedAsync_UnknownToken_NeitherAnnouncesNorPauses()
        {
            var hub = CreateHub();
            var roomId = Guid.NewGuid();
            _presence.Add(ConnectionId, roomId, "tok-gone");
            _roomService.Setup(r => r.SetParticipantStatusAsync(It.IsAny<string>(), It.IsAny<ParticipantStatus>()))
                .ReturnsAsync((ParticipantStatus?)null);
            _playbackService.Setup(p => p.PauseAtCurrentPositionAsync(It.IsAny<Guid>()))
                .ReturnsAsync(PlayingState(roomId));

            await hub.OnDisconnectedAsync(null);

            Assert.Empty(Sent(_groupProxy));
            _playbackService.Verify(p => p.PauseAtCurrentPositionAsync(It.IsAny<Guid>()), Times.Never);
        }

        [Fact]
        public async Task OnDisconnectedAsync_RoomWasNotPlaying_AnnouncesWithoutRebroadcastingPlayback()
        {
            var hub = CreateHub();
            var roomId = Guid.NewGuid();
            _presence.Add(ConnectionId, roomId, "tok-1");
            _roomService.Setup(r => r.SetParticipantStatusAsync("tok-1", ParticipantStatus.Offline))
                .ReturnsAsync(ParticipantStatus.Online);
            _roomService.Setup(r => r.GetDisplayNameAsync("tok-1")).ReturnsAsync("Taiwo");
            // Already paused, so PauseAtCurrentPositionAsync has nothing to return.
            _playbackService.Setup(p => p.PauseAtCurrentPositionAsync(roomId))
                .ReturnsAsync((PlaybackState?)null);

            await hub.OnDisconnectedAsync(null);

            Assert.Equal("ParticipantsUpdated", Assert.Single(Sent(_groupProxy)).Method);
        }

        [Fact]
        public async Task OnDisconnectedAsync_ConnectionNeverJoined_DoesNothing()
        {
            var hub = CreateHub();
            _roomService.Setup(r => r.SetParticipantStatusAsync(It.IsAny<string>(), It.IsAny<ParticipantStatus>()))
                .ReturnsAsync(ParticipantStatus.Online);
            _playbackService.Setup(p => p.PauseAtCurrentPositionAsync(It.IsAny<Guid>()))
                .ReturnsAsync(PlayingState(Guid.NewGuid()));

            await hub.OnDisconnectedAsync(null);

            Assert.Empty(Sent(_groupProxy));
            Assert.Empty(Sent(_callerProxy));
        }

        [Fact]
        public async Task OnDisconnectedAsync_StillMarksRoomOnlineForTheRemainingParticipant()
        {
            var hub = CreateHub();
            var roomId = Guid.NewGuid();
            _presence.Add(ConnectionId, roomId, "tok-1");
            _presence.Add("conn-2", roomId, "tok-2");
            _roomService.Setup(r => r.SetParticipantStatusAsync(It.IsAny<string>(), It.IsAny<ParticipantStatus>()))
                .ReturnsAsync(ParticipantStatus.Online);
            _roomService.Setup(r => r.GetDisplayNameAsync(It.IsAny<string>())).ReturnsAsync("Taiwo");
            _playbackService.Setup(p => p.PauseAtCurrentPositionAsync(roomId))
                .ReturnsAsync(PlayingState(roomId));

            await hub.OnDisconnectedAsync(null);

            // The pause goes to everyone still in the room, not just the caller.
            Assert.Contains(Sent(_groupProxy), s => s.Method == "PlaybackUpdated");
        }

        [Fact]
        public async Task Join_ParticipantWasOffline_AnnouncesTheyAreBack()
        {
            var hub = CreateHub();
            var roomId = Guid.NewGuid();
            _roomService.Setup(r => r.SetParticipantStatusAsync("tok-1", ParticipantStatus.Online))
                .ReturnsAsync(ParticipantStatus.Offline);
            _roomService.Setup(r => r.GetDisplayNameAsync("tok-1")).ReturnsAsync("Taiwo");
            _chatService.Setup(c => c.GetHistoryAsync(roomId)).ReturnsAsync(new List<ChatMessage>());
            _playbackService.Setup(p => p.GetCurrentStateAsync(roomId)).ReturnsAsync((PlaybackState?)null);

            await hub.Join(roomId, "tok-1");

            var announcement = Assert.Single(Sent(_groupProxy));
            Assert.Equal("ParticipantsUpdated", announcement.Method);

            var payload = Assert.Single(announcement.Args);
            Assert.Equal("Taiwo", Property(payload, "displayName"));
            Assert.Equal(ParticipantStatus.Online, Property(payload, "status"));
        }

        [Fact]
        public async Task Join_ParticipantWasAlreadyOnline_AnnouncesNothing()
        {
            var hub = CreateHub();
            var roomId = Guid.NewGuid();
            // Already Online: this is just a reconnect, not a return. Announcing it
            // would tell the room someone is back each time they refreshed a page
            // they never left.
            _roomService.Setup(r => r.SetParticipantStatusAsync("tok-1", ParticipantStatus.Online))
                .ReturnsAsync(ParticipantStatus.Online);
            _chatService.Setup(c => c.GetHistoryAsync(roomId)).ReturnsAsync(new List<ChatMessage>());
            _playbackService.Setup(p => p.GetCurrentStateAsync(roomId)).ReturnsAsync((PlaybackState?)null);

            await hub.Join(roomId, "tok-1");

            Assert.Empty(Sent(_groupProxy));
        }

        [Fact]
        public async Task OnDisconnectedAsync_ParticipantWasAlreadyOffline_AnnouncesNothingAndLeavesPlayback()
        {
            var hub = CreateHub();
            var roomId = Guid.NewGuid();
            _presence.Add(ConnectionId, roomId, "tok-1");
            // A connection that registered and dropped before ever being marked
            // Online. There is no roster change to report and no driving to stop.
            _roomService.Setup(r => r.SetParticipantStatusAsync(It.IsAny<string>(), It.IsAny<ParticipantStatus>()))
                .ReturnsAsync(ParticipantStatus.Offline);
            _roomService.Setup(r => r.GetDisplayNameAsync(It.IsAny<string>())).ReturnsAsync("Taiwo");
            _playbackService.Setup(p => p.PauseAtCurrentPositionAsync(It.IsAny<Guid>()))
                .ReturnsAsync(PlayingState(roomId));

            await hub.OnDisconnectedAsync(null);

            Assert.Empty(Sent(_groupProxy));
            _playbackService.Verify(p => p.PauseAtCurrentPositionAsync(It.IsAny<Guid>()), Times.Never);
        }

        [Fact]
        public async Task JoinRoomGroup_UsesRoomIdAsTheGroupName()
        {
            var hub = CreateHub();
            var roomId = Guid.NewGuid();

            await hub.JoinRoomGroup(roomId);

            _groups.Verify(g => g.AddToGroupAsync(ConnectionId, roomId.ToString(), It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [Fact]
        public async Task Join_SendsChatHistoryAndStateToTheCallerOnly()
        {
            var hub = CreateHub();
            var roomId = Guid.NewGuid();
            var state = PlayingState(roomId);
            // Arriving from Offline, so the roster notice is real news.
            _roomService.Setup(r => r.SetParticipantStatusAsync("tok-1", ParticipantStatus.Online))
                .ReturnsAsync(ParticipantStatus.Offline);
            _chatService.Setup(c => c.GetHistoryAsync(roomId)).ReturnsAsync(new List<ChatMessage>
            {
                new ChatMessage { RoomId = roomId, DisplayName = "Taiwo", Message = "hi" }
            });
            _playbackService.Setup(p => p.GetCurrentStateAsync(roomId)).ReturnsAsync(state);

            await hub.Join(roomId, "tok-1");

            // History and playback state are the caller's alone — replaying chat
            // to the room would duplicate it for anyone already watching.
            var sent = Sent(_callerProxy);
            Assert.Equal(new[] { "ChatHistory", "PlaybackUpdated" }, sent.Select(s => s.Method));

            var history = (IEnumerable)Assert.Single(sent[0].Args)!;
            Assert.Single(history);

            // The roster notice is a group broadcast, so the other participant sees
            // someone arrive.
            Assert.Equal("ParticipantsUpdated", Assert.Single(Sent(_groupProxy)).Method);
        }

        [Fact]
        public async Task Join_EmptyChatHistory_StillClearsTheClientsPanel()
        {
            var hub = CreateHub();
            var roomId = Guid.NewGuid();
            _roomService.Setup(r => r.SetParticipantStatusAsync("tok-1", ParticipantStatus.Online))
                .ReturnsAsync(ParticipantStatus.Online);
            _chatService.Setup(c => c.GetHistoryAsync(roomId)).ReturnsAsync(new List<ChatMessage>());
            _playbackService.Setup(p => p.GetCurrentStateAsync(roomId)).ReturnsAsync((PlaybackState?)null);

            await hub.Join(roomId, "tok-1");

            var sent = Sent(_callerProxy);

            // The payload is the history sequence itself, empty or not — sending it
            // either way is what makes the client clear a stale panel on join.
            var history = (IEnumerable)Assert.Single(sent.Single(s => s.Method == "ChatHistory").Args)!;
            Assert.Empty(history);

            // No state to hand back, so nothing is sent.
            Assert.DoesNotContain(sent, s => s.Method == "PlaybackUpdated");
        }

        [Fact]
        public async Task SendMessage_StoresThenBroadcastsToTheRoom()
        {
            var hub = CreateHub();
            var roomId = Guid.NewGuid();
            _chatService.Setup(c => c.SendAsync(roomId, "Taiwo", "  hello  "))
                .ReturnsAsync(new ChatMessage { RoomId = roomId, DisplayName = "Taiwo", Message = "hello" });

            await hub.SendMessage(roomId, "Taiwo", "  hello  ");

            var sent = Assert.Single(Sent(_groupProxy));
            Assert.Equal("ChatMessageReceived", sent.Method);

            var payload = Assert.Single(sent.Args);
            Assert.Equal("Taiwo", Property(payload, "displayName"));
            Assert.Equal("hello", Property(payload, "message"));
            Assert.NotNull(Property(payload, "id"));
        }

        [Fact]
        public async Task SendMessage_ServiceRejects_ThrowsHubException()
        {
            var hub = CreateHub();
            var roomId = Guid.NewGuid();
            _chatService.Setup(c => c.SendAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>()))
                .ThrowsAsync(new MessageTooLongException());

            await Assert.ThrowsAsync<HubException>(
                () => hub.SendMessage(roomId, "Taiwo", new string('x', 400)));
        }

        [Fact]
        public async Task SendMessage_BlankMessage_IsIgnoredWithoutTouchingTheService()
        {
            var hub = CreateHub();
            var roomId = Guid.NewGuid();

            await hub.SendMessage(roomId, "Taiwo", "   ");

            Assert.Empty(Sent(_groupProxy));
            _chatService.Verify(c => c.SendAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }
    }
}