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
        private readonly PresenceTracker _presence;
        private readonly ILogger<PlaybackHub> _logger;

        public PlaybackHub(
            IPlaybackService playbackService,
            IRoomService roomService,
            PresenceTracker presence,
            ILogger<PlaybackHub> logger)
        {
            _playbackService = playbackService;
            _roomService = roomService;
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

            await Clients.Group(roomId.ToString()).SendAsync("ChatMessageReceived", new
            {
                displayName = string.IsNullOrWhiteSpace(displayName) ? "Guest" : displayName,
                message = message.Trim(),
                sentAt = DateTime.UtcNow
            });
        }

        public async Task Reconnect(Guid roomId, string? token = null)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, roomId.ToString());
            await MarkPresentAsync(roomId, token);

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

            if (await _roomService.SetParticipantStatusAsync(token, ParticipantStatus.Online))
            {
                await Clients.Group(roomId.ToString()).SendAsync("ParticipantsUpdated");
            }
        }

        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            if (exception != null)
            {
                _logger.LogDebug(exception, "Playback connection {ConnectionId} dropped.", Context.ConnectionId);
            }

            if (_presence.TryRemove(Context.ConnectionId, out var roomId, out var token, out var participantStillConnected)
                && !participantStillConnected
                && await _roomService.SetParticipantStatusAsync(token, ParticipantStatus.Offline))
            {
                await Clients.Group(roomId.ToString()).SendAsync("ParticipantsUpdated");
            }

            await base.OnDisconnectedAsync(exception);
        }
    }
}
