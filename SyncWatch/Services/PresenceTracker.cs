using System.Collections.Concurrent;

namespace SyncWatch.Services
{
    public sealed record ParticipantPresence(Guid RoomId, string Token);

    public sealed class PresenceTracker
    {
        private readonly ConcurrentDictionary<string, ParticipantPresence> _byConnection = new();

        public void Add(string connectionId, Guid roomId, string token)
        {
            _byConnection[connectionId] = new ParticipantPresence(roomId, token);
        }

        public bool TryRemove(
            string connectionId,
            out Guid roomId,
            out string token,
            out bool participantStillConnected)
        {
            roomId = Guid.Empty;
            token = string.Empty;
            participantStillConnected = false;

            if (!_byConnection.TryRemove(connectionId, out var presence))
            {
                return false;
            }

            roomId = presence.RoomId;
            token = presence.Token;

            var leavingRoomId = presence.RoomId;
            var leavingToken = presence.Token;

            participantStillConnected = _byConnection.Values
                .Any(p => p.RoomId == leavingRoomId && p.Token == leavingToken);

            return true;
        }
    }
}
