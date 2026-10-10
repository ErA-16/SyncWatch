using SyncWatch.Services;
using Xunit;

namespace SyncWatch.Tests
{
    public class PresenceTrackerTests
    {
        private readonly PresenceTracker _tracker = new();

        [Fact]
        public void TryRemove_UnknownConnection_ReturnsFalse()
        {
            Assert.False(_tracker.TryRemove("nobody", out _, out _, out _));
        }

        [Fact]
        public void TryRemove_LastConnection_ReportsParticipantGone()
        {
            var roomId = Guid.NewGuid();
            _tracker.Add("conn-1", roomId, "tok");

            Assert.True(_tracker.TryRemove("conn-1", out var removedRoom, out var removedToken, out var stillConnected));

            Assert.Equal(roomId, removedRoom);
            Assert.Equal("tok", removedToken);
            Assert.False(stillConnected);
        }

        [Fact]
        public void TryRemove_SecondTabStillOpen_KeepsParticipantOnline()
        {
            var roomId = Guid.NewGuid();
            _tracker.Add("conn-1", roomId, "tok");
            _tracker.Add("conn-2", roomId, "tok");

            Assert.True(_tracker.TryRemove("conn-1", out _, out _, out var stillConnected));
            Assert.True(stillConnected);

            Assert.True(_tracker.TryRemove("conn-2", out _, out _, out var afterLastTab));
            Assert.False(afterLastTab);
        }

        [Fact]
        public void TryRemove_DifferentParticipant_SameRoom_NotCountedAsConnected()
        {
            var roomId = Guid.NewGuid();
            _tracker.Add("conn-1", roomId, "tok-a");
            _tracker.Add("conn-2", roomId, "tok-b");

            Assert.True(_tracker.TryRemove("conn-1", out _, out var token, out var stillConnected));

            Assert.Equal("tok-a", token);
            Assert.False(stillConnected); // tok-b is a different person
        }
    }
}
