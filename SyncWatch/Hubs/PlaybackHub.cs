using Microsoft.AspNetCore.SignalR;
using SyncWatch.Dtos;
using SyncWatch.Models;
using SyncWatch.Services;
using SyncWatch.Services.Exception;

namespace SyncWatch.Hubs
{
    public class PlaybackHub : Hub
    {
        private readonly IPlaybackService _playbackService;
        private readonly IRoomService _roomService;
        private readonly IChatService _chatService;
        private readonly PresenceTracker _presence;
        private readonly ILogger<PlaybackHub> _logger;

        public PlaybackHub(
            IPlaybackService playbackService,
            IRoomService roomService,
            IChatService chatService,
            PresenceTracker presence,
            ILogger<PlaybackHub> logger)
        {
            _playbackService = playbackService;
            _roomService = roomService;
            _chatService = chatService;
            _presence = presence;
            _logger = logger;
        }

        public async Task JoinRoomGroup(Guid roomId)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, roomId.ToString());
        }

        public async Task Play(Guid roomId, PlaybackCommandRequest request)
        {
            var state = await _playbackService.PlayAsync(roomId, request);

            await Clients.Group(roomId.ToString()).SendAsync("PlaybackUpdated", state);

        }

        public async Task Pause(Guid roomId, PlaybackCommandRequest request)
        {
            try
            {
                var state = await _playbackService.PauseAsync(roomId, request);
                await Clients.Group(roomId.ToString()).SendAsync("PlaybackUpdated", state);
            }
            catch (PlaybackStateNotFoundException ex)
            {
                throw new HubException(ex.Message);
            }
        }

        public async Task Seek(Guid roomId, PlaybackCommandRequest request)
        {
            try
            {
                var state = await _playbackService.SeekAsync(roomId, request);
                await Clients.Group(roomId.ToString()).SendAsync("PlaybackUpdated", state);
            }
            catch (PlaybackStateNotFoundException ex)
            {
                throw new HubException(ex.Message);
            }
        }

        public async Task SwitchEpisode(Guid roomId, PlaybackCommandRequest request)
        {
            try
            {
                var state = await _playbackService.SwitchEpisodeAsync(roomId, request);
                await Clients.Group(roomId.ToString()).SendAsync("PlaybackUpdated", state);
            }
            catch (PlaybackStateNotFoundException ex)
            {
                throw new HubException(ex.Message);
            }
            catch (MovieNotFoundException ex)
            {
                throw new HubException(ex.Message);
            }
        }

        public async Task Join(Guid roomId, string? token = null)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, roomId.ToString());
            await MarkPresentAsync(roomId, token);
            await SendChatHistoryAsync(roomId);

            var state = await _playbackService.GetCurrentStateAsync(roomId);

            if (state != null)
            {
                await Clients.Caller.SendAsync("PlaybackUpdated", state);
            }
        }

        public async Task Advance(Guid roomId, PlaybackCommandRequest request)
        {
            try
            {
                var state = await _playbackService.AdvanceAsync(roomId, request);
                await Clients.Group(roomId.ToString()).SendAsync("PlaybackUpdated", state);
            }
            catch (PlaybackStateNotFoundException ex)
            {
                throw new HubException(ex.Message);
            }
            catch (MovieNotFoundException ex)
            {
                throw new HubException(ex.Message);
            }
        }

        public async Task SendMessage(Guid roomId, string displayName, string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return;

            try
            {
                var stored = await _chatService.SendAsync(roomId, displayName, message);

                await Clients.Group(roomId.ToString()).SendAsync("ChatMessageReceived", new
                {
                    id = stored.Id,
                    displayName = stored.DisplayName,
                    message = stored.Message,
                    sentAt = stored.SentAt
                });
            }
            catch (Exception ex) when (ex is EmptyMessageException
                                       || ex is MessageTooLongException
                                       || ex is RoomNotAcceptingMessagesException)
            {
                throw new HubException(ex.Message);
            }
        }

        // Replayed on join/reconnect so a refresh doesn't wipe the panel. Sent
        // only to the caller — everyone else already has these messages on screen.
        private async Task SendChatHistoryAsync(Guid roomId)
        {
            var history = await _chatService.GetHistoryAsync(roomId);

            await Clients.Caller.SendAsync("ChatHistory", history.Select(m => new
            {
                id = m.Id,
                displayName = m.DisplayName,
                message = m.Message,
                sentAt = m.SentAt
            }));
        }

        public async Task Reconnect(Guid roomId, string? token = null)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, roomId.ToString());
            await MarkPresentAsync(roomId, token);
            await SendChatHistoryAsync(roomId);

            var state = await _playbackService.GetCurrentStateAsync(roomId);

            if (state != null)
            {
                await Clients.Caller.SendAsync("PlaybackUpdated", state);
            }
        }

        private async Task MarkPresentAsync(Guid roomId, string? token)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                return;
            }

            _presence.Add(Context.ConnectionId, roomId, token);

            var previous = await _roomService.SetParticipantStatusAsync(token, ParticipantStatus.Online);

            // Only an Offline -> Online transition is worth announcing. Join runs on
            // every reconnect, so without this the room would be told someone is
            // back each time they refreshed a page they never left.
            if (previous != null && previous != ParticipantStatus.Online)
            {
                await AnnouncePresenceAsync(roomId, token, ParticipantStatus.Online);
            }
        }

        // Names the participant so the client can say who it was, and carries the
        // status so it can tell a drop from a return. Sent to the room, because
        // everyone in it needs to see the roster change.
        private async Task AnnouncePresenceAsync(Guid roomId, string token, ParticipantStatus status)
        {
            var displayName = await _roomService.GetDisplayNameAsync(token);

            await Clients.Group(roomId.ToString()).SendAsync("ParticipantsUpdated", new
            {
                displayName = string.IsNullOrWhiteSpace(displayName) ? "Guest" : displayName,
                status
            });
        }

        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            if (exception != null)
            {
                _logger.LogDebug(exception, "Playback connection {ConnectionId} dropped.", Context.ConnectionId);
            }

            if (_presence.TryRemove(Context.ConnectionId, out var roomId, out var token, out var participantStillConnected)
                && !participantStillConnected)
            {
                var previous = await _roomService.SetParticipantStatusAsync(token, ParticipantStatus.Offline);

                if (previous != null && previous != ParticipantStatus.Offline)
                {
                    await AnnouncePresenceAsync(roomId, token, ParticipantStatus.Offline);

                    // Whoever just left was driving playback. Freeze the room where it
                    // is instead of letting it run on with nothing watching — the
                    // position would otherwise keep climbing in the background for
                    // the rest of the room's life. Whoever is still here presses play
                    // to carry on, and the reconnecting participant joins at whatever
                    // position that got to.
                    var paused = await _playbackService.PauseAtCurrentPositionAsync(roomId);

                    if (paused != null)
                    {
                        await Clients.Group(roomId.ToString()).SendAsync("PlaybackUpdated", paused);
                    }
                }
            }

            await base.OnDisconnectedAsync(exception);
        }
    }
}
