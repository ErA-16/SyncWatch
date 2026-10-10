using Amazon.S3;
using Microsoft.Extensions.Configuration;
using Moq;
using SyncWatch.Data;
using SyncWatch.Dtos;
using SyncWatch.Models;
using SyncWatch.Services;
using SyncWatch.Services.Exception;
using System;
using System.Collections.Generic;
using System.Text;

namespace SyncWatch.Tests
{
    public class RoomServiceTests
    {
        private RoomService CreateService(out DatabaseContext db, int maxParticipants = 2)
        {
            db = TestHelper.CreateDbContext();
            var configData = new Dictionary<string, string?>
            {
                { "ParticipantsSettings:MaxParticipants", maxParticipants.ToString() }
            };
            var config = new ConfigurationBuilder().AddInMemoryCollection(configData).Build();
            var s3 = new Mock<IAmazonS3>();
            return new RoomService(db, s3.Object, config);
        }

        [Fact]
        public async Task CreateRoomAsync_NoHostName_ThrowsHostNameRequiredException()
        {
            var service = CreateService(out _);
            await Assert.ThrowsAsync<HostNameRequiredException>(
                () => service.CreateRoomAsync(new CreateRoomRequest { HostName = "" }));
        }

        [Fact]
        public async Task CreateRoomAsync_ValidRequest_CreatesRoomWithHostParticipant()
        {
            var service = CreateService(out var db);
            var response = await service.CreateRoomAsync(new CreateRoomRequest { HostName = "Taiwo" });

            Assert.NotEqual(Guid.Empty, response.RoomId);
            Assert.Equal(5, response.RoomCode.Length);
            Assert.NotEqual(Guid.Empty, response.ParticipantId);
            Assert.False(string.IsNullOrEmpty(response.Token));

            var participant = await db.Participants.FindAsync(response.ParticipantId);
            Assert.NotNull(participant);
            Assert.True(participant!.IsHost);
        }

        [Fact]
        public async Task JoinRoomAsync_RoomDoesNotExist_ThrowsRoomNotFoundException()
        {
            var service = CreateService(out _);
            await Assert.ThrowsAsync<RoomNotFoundException>(
                () => service.JoinRoomAsync(new JoinRoomRequest { RoomCode = "ZZZZZ" }));
        }

        [Fact]
        public async Task JoinRoomAsync_RoomFull_ThrowsMaximumRoomCapacityReachedException()
        {
            var service = CreateService(out var db, maxParticipants: 1);
            var room = new Room { Code = "FULL1" };
            room.Participants.Add(new Participant { RoomId = room.Id, Token = "t1", IsHost = true });
            db.Rooms.Add(room);
            await db.SaveChangesAsync();

            await Assert.ThrowsAsync<MaximumRoomCapacityReachedException>(
                () => service.JoinRoomAsync(new JoinRoomRequest { RoomCode = "FULL1" }));
        }

        [Fact]
        public async Task JoinRoomAsync_ValidRoom_AddsSecondParticipant()
        {
            var service = CreateService(out var db);
            var room = new Room { Code = "JOIN1" };
            db.Rooms.Add(room);
            await db.SaveChangesAsync();

            await service.JoinRoomAsync(new JoinRoomRequest { RoomCode = "JOIN1", DisplayName = "Her" });

            var participantCount = db.Participants.Count(p => p.RoomId == room.Id);
            Assert.Equal(1, participantCount);
        }

        [Fact]
        public async Task CloseRoomAsync_NotHost_ThrowsForbiddenException()
        {
            var service = CreateService(out var db);
            var room = new Room { Code = "CLOSE1" };
            room.Participants.Add(new Participant { RoomId = room.Id, Token = "guest-token", IsHost = false });
            db.Rooms.Add(room);
            await db.SaveChangesAsync();

            await Assert.ThrowsAsync<ForbiddenException>(
                () => service.CloseRoomAsync(room.Id, "guest-token"));
        }

        [Fact]
        public async Task CloseRoomAsync_IsHost_RemovesRoom()
        {
            var service = CreateService(out var db);
            var room = new Room { Code = "CLOSE2" };
            room.Participants.Add(new Participant { RoomId = room.Id, Token = "host-token", IsHost = true });
            db.Rooms.Add(room);
            await db.SaveChangesAsync();

            var result = await service.CloseRoomAsync(room.Id, "host-token");

            Assert.True(result);
            Assert.Null(await db.Rooms.FindAsync(room.Id));
        }

        [Fact]
        public async Task CloseRoomAsync_HasMoviesAndPlaybackState_DeletesEverything()
        {
            var service = CreateService(out var db);
            var room = new Room { Code = "CLOSE3" };
            room.Participants.Add(new Participant { RoomId = room.Id, Token = "host-token3", IsHost = true });
            var movie = new Movie { RoomId = room.Id, Title = "Ep1", EpisodeNumber = 1, FileName = "a.mp4", StorageLocation = "roomkey/a.mp4" };
            room.Movies.Add(movie);
            db.Rooms.Add(room);
            await db.SaveChangesAsync();
            db.PlaybackStates.Add(new PlaybackState { RoomId = room.Id, CurrentMovieId = movie.Id });
            await db.SaveChangesAsync();

            var result = await service.CloseRoomAsync(room.Id, "host-token3");

            Assert.True(result);
            Assert.Null(await db.Rooms.FindAsync(room.Id));
            Assert.Empty(db.Movies);
            Assert.Empty(db.PlaybackStates);
        }

        [Fact]
        public async Task LeaveRoomAsync_SetsParticipantOffline()
        {
            var service = CreateService(out var db);
            var participant = new Participant { RoomId = Guid.NewGuid(), Token = "tok", Status = ParticipantStatus.Online };
            db.Participants.Add(participant);
            await db.SaveChangesAsync();

            await service.LeaveRoomAsync("tok");

            var updated = await db.Participants.FindAsync(participant.Id);
            Assert.Equal(ParticipantStatus.Offline, updated!.Status);
        }

        [Fact]
        public async Task ReconnectRoomAsync_SetsParticipantOnline()
        {
            var service = CreateService(out var db);
            var participant = new Participant { RoomId = Guid.NewGuid(), Token = "tok2", Status = ParticipantStatus.Offline };
            db.Participants.Add(participant);
            await db.SaveChangesAsync();

            await service.ReconnectRoomAsync("tok2");

            var updated = await db.Participants.FindAsync(participant.Id);
            Assert.Equal(ParticipantStatus.Online, updated!.Status);
        }

        [Fact]
        public async Task GetRoomAsync_RoomDoesNotExist_ThrowsRoomNotFoundException()
        {
            var service = CreateService(out _);
            await Assert.ThrowsAsync<RoomNotFoundException>(() => service.GetRoomAsync("NOPE1"));
        }

        [Fact]
        public async Task SetParticipantStatusAsync_UnknownToken_ReturnsNullWithoutThrowing()
        {
            var service = CreateService(out _);

            var result = await service.SetParticipantStatusAsync("no-such-token", ParticipantStatus.Online);

            Assert.Null(result);
        }

        [Fact]
        public async Task SetParticipantStatusAsync_KnownToken_UpdatesStatusAndReportsThePreviousOne()
        {
            var service = CreateService(out var db);
            var participant = new Participant { RoomId = Guid.NewGuid(), Token = "tok3" };
            db.Participants.Add(participant);
            await db.SaveChangesAsync();

            var previous = await service.SetParticipantStatusAsync("tok3", ParticipantStatus.Online);

            Assert.Equal(ParticipantStatus.Offline, previous);
            Assert.Equal(ParticipantStatus.Online, (await db.Participants.FindAsync(participant.Id))!.Status);
        }

        [Fact]
        public async Task SetParticipantStatusAsync_StatusAlreadyMatches_ReportsItWithoutRewriting()
        {
            var service = CreateService(out var db);
            var participant = new Participant
            {
                RoomId = Guid.NewGuid(),
                Token = "tok5",
                Status = ParticipantStatus.Online
            };
            db.Participants.Add(participant);
            await db.SaveChangesAsync();

            var previous = await service.SetParticipantStatusAsync("tok5", ParticipantStatus.Online);

            // Reported back so the Hub can tell a redundant reconnect from someone
            // genuinely returning — only the second is worth announcing.
            Assert.Equal(ParticipantStatus.Online, previous);
            Assert.Equal(ParticipantStatus.Online, (await db.Participants.FindAsync(participant.Id))!.Status);
        }

        [Fact]
        public async Task GetDisplayNameAsync_KnownToken_ReturnsDisplayName()
        {
            var service = CreateService(out var db);
            var participant = new Participant { RoomId = Guid.NewGuid(), Token = "tok4", DisplayName = "Taiwo" };
            db.Participants.Add(participant);
            await db.SaveChangesAsync();

            var name = await service.GetDisplayNameAsync("tok4");

            Assert.Equal("Taiwo", name);
        }

        [Fact]
        public async Task GetDisplayNameAsync_UnknownToken_ReturnsNull()
        {
            var service = CreateService(out _);

            Assert.Null(await service.GetDisplayNameAsync("no-such-token"));
            Assert.Null(await service.GetDisplayNameAsync("  "));
        }

        [Fact]
        public async Task NewParticipants_StartOfflineUntilTheirConnectionRegisters()
        {
            var service = CreateService(out _);

            var created = await service.CreateRoomAsync(new CreateRoomRequest { HostName = "Taiwo" });
            var host = await service.GetRoomAsync(created.RoomCode);

            Assert.Equal(ParticipantStatus.Offline, Assert.Single(host.Participants).Status);
        }

        [Fact]
        public async Task GetRoomAsync_ReturnsCorrectSummary()
        {
            var service = CreateService(out var db);
            var room = new Room { Code = "GET001", Status = RoomStatus.Ready };
            room.Movies.Add(new Movie { RoomId = room.Id, Title = "Ep1", EpisodeNumber = 1, FileName = "a.mp4", StorageLocation = "x" });
            db.Rooms.Add(room);
            await db.SaveChangesAsync();

            var result = await service.GetRoomAsync("GET001");

            Assert.Equal(RoomStatus.Ready, result.Status);
            Assert.Single(result.Movies);
            Assert.Equal("Ep1", result.Movies[0].Title);
        }
    }
}
