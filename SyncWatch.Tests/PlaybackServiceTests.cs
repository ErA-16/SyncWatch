using Microsoft.Extensions.Logging;
using Moq;
using SyncWatch.Data;
using SyncWatch.Dtos;
using SyncWatch.Models;
using SyncWatch.Services;
using SyncWatch.Services.Exception;
using Xunit;

namespace SyncWatch.Tests
{
    public class PlaybackServiceTests
    {
        private PlaybackService CreateService(out DatabaseContext db)
        {
            db = TestHelper.CreateDbContext();
            var logger = Mock.Of<ILogger<PlaybackService>>();
            return new PlaybackService(db, logger);
        }

        [Fact]
        public async Task PlayAsync_NoExistingState_CreatesNewPlaybackState()
        {
            var service = CreateService(out _);
            var roomId = Guid.NewGuid();
            var movieId = Guid.NewGuid();

            var state = await service.PlayAsync(roomId, new PlaybackCommandRequest { MovieId = movieId, Position = 0 });

            Assert.Equal(PlaybackStatus.Playing, state.Status);
            Assert.Equal(1, state.Version);
        }

        [Fact]
        public async Task PlayAsync_ExistingState_UpdatesAndIncrementsVersion()
        {
            var service = CreateService(out var db);
            var roomId = Guid.NewGuid();
            db.PlaybackStates.Add(new PlaybackState { RoomId = roomId, Status = PlaybackStatus.Paused, Position = 50, Version = 3 });
            await db.SaveChangesAsync();

            var state = await service.PlayAsync(roomId, new PlaybackCommandRequest { MovieId = Guid.NewGuid(), Position = 55 });

            Assert.Equal(PlaybackStatus.Playing, state.Status);
            Assert.Equal(55, state.Position);
            Assert.Equal(4, state.Version);
        }

        [Fact]
        public async Task PauseAsync_NoExistingState_ThrowsPlaybackStateNotFoundException()
        {
            var service = CreateService(out _);
            await Assert.ThrowsAsync<PlaybackStateNotFoundException>(
                () => service.PauseAsync(Guid.NewGuid(), new PlaybackCommandRequest()));
        }

        [Fact]
        public async Task PauseAsync_ExistingState_SetsStatusAndIncrementsVersion()
        {
            var service = CreateService(out var db);
            var roomId = Guid.NewGuid();
            db.PlaybackStates.Add(new PlaybackState { RoomId = roomId, Status = PlaybackStatus.Playing, Version = 1 });
            await db.SaveChangesAsync();

            var state = await service.PauseAsync(roomId, new PlaybackCommandRequest { Position = 120 });

            Assert.Equal(PlaybackStatus.Paused, state.Status);
            Assert.Equal(120, state.Position);
            Assert.Equal(2, state.Version);
        }

        [Fact]
        public async Task SeekAsync_NoExistingState_ThrowsPlaybackStateNotFoundException()
        {
            var service = CreateService(out _);
            await Assert.ThrowsAsync<PlaybackStateNotFoundException>(
                () => service.SeekAsync(Guid.NewGuid(), new PlaybackCommandRequest()));
        }

        [Fact]
        public async Task SeekAsync_ExistingState_UpdatesPositionOnly()
        {
            var service = CreateService(out var db);
            var roomId = Guid.NewGuid();
            db.PlaybackStates.Add(new PlaybackState { RoomId = roomId, Status = PlaybackStatus.Playing, Position = 10, Version = 1 });
            await db.SaveChangesAsync();

            var state = await service.SeekAsync(roomId, new PlaybackCommandRequest { Position = 300 });

            Assert.Equal(300, state.Position);
            Assert.Equal(PlaybackStatus.Playing, state.Status);   // unchanged by Seek
            Assert.Equal(2, state.Version);
        }

        [Fact]
        public async Task SwitchEpisodeAsync_NoExistingState_ThrowsPlaybackStateNotFoundException()
        {
            var service = CreateService(out _);
            await Assert.ThrowsAsync<PlaybackStateNotFoundException>(
                () => service.SwitchEpisodeAsync(Guid.NewGuid(), new PlaybackCommandRequest { MovieId = Guid.NewGuid() }));
        }

        [Fact]
        public async Task SwitchEpisodeAsync_MovieBelongsToDifferentRoom_ThrowsMovieNotFoundException()
        {
            var service = CreateService(out var db);
            var roomId = Guid.NewGuid();
            var otherRoomId = Guid.NewGuid();

            db.PlaybackStates.Add(new PlaybackState { RoomId = roomId, Version = 1 });
            var movie = new Movie { RoomId = otherRoomId, Title = "x", FileName = "x.mp4", StorageLocation = "x" };
            db.Movies.Add(movie);
            await db.SaveChangesAsync();

            await Assert.ThrowsAsync<MovieNotFoundException>(
                () => service.SwitchEpisodeAsync(roomId, new PlaybackCommandRequest { MovieId = movie.Id }));
        }

        [Fact]
        public async Task SwitchEpisodeAsync_ValidMovie_UpdatesMovieAndResetsPositionToZero()
        {
            var service = CreateService(out var db);
            var roomId = Guid.NewGuid();

            db.PlaybackStates.Add(new PlaybackState { RoomId = roomId, Position = 500, Version = 1 });
            var movie = new Movie { RoomId = roomId, Title = "Ep2", EpisodeNumber = 2, FileName = "e2.mp4", StorageLocation = "x" };
            db.Movies.Add(movie);
            await db.SaveChangesAsync();

            var state = await service.SwitchEpisodeAsync(roomId, new PlaybackCommandRequest { MovieId = movie.Id });

            Assert.Equal(movie.Id, state.CurrentMovieId);
            Assert.Equal(0, state.Position);
            Assert.Equal(2, state.Version);
        }

        [Fact]
        public async Task AdvanceAsync_NoExistingState_ThrowsPlaybackStateNotFoundException()
        {
            var service = CreateService(out _);
            await Assert.ThrowsAsync<PlaybackStateNotFoundException>(
                () => service.AdvanceAsync(Guid.NewGuid(), new PlaybackCommandRequest()));
        }

        [Fact]
        public async Task AdvanceAsync_NextEpisodeExists_SwitchesToItAndKeepsPlaying()
        {
            var service = CreateService(out var db);
            var roomId = Guid.NewGuid();

            var first = new Movie { RoomId = roomId, Title = "Ep1", EpisodeNumber = 1, FileName = "a.mp4", StorageLocation = "x", Duration = 1200 };
            var second = new Movie { RoomId = roomId, Title = "Ep2", EpisodeNumber = 2, FileName = "b.mp4", StorageLocation = "y", Duration = 900 };
            db.Movies.AddRange(first, second);
            db.PlaybackStates.Add(new PlaybackState
            {
                RoomId = roomId,
                CurrentMovieId = first.Id,
                Status = PlaybackStatus.Playing,
                Position = 1200,
                Version = 4
            });
            await db.SaveChangesAsync();

            var state = await service.AdvanceAsync(roomId, new PlaybackCommandRequest { MovieId = first.Id, Position = 0 });

            Assert.Equal(second.Id, state.CurrentMovieId);
            Assert.Equal(0, state.Position);
            Assert.Equal(PlaybackStatus.Playing, state.Status);
            Assert.Equal(5, state.Version);
        }

        [Fact]
        public async Task AdvanceAsync_NoNextEpisode_PausesAtTheEnd()
        {
            var service = CreateService(out var db);
            var roomId = Guid.NewGuid();

            var only = new Movie { RoomId = roomId, Title = "Only", EpisodeNumber = 1, FileName = "a.mp4", StorageLocation = "x", Duration = 1200 };
            db.Movies.Add(only);
            db.PlaybackStates.Add(new PlaybackState
            {
                RoomId = roomId,
                CurrentMovieId = only.Id,
                Status = PlaybackStatus.Playing,
                Position = 1200,
                Version = 4
            });
            await db.SaveChangesAsync();

            var state = await service.AdvanceAsync(roomId, new PlaybackCommandRequest { MovieId = only.Id, Position = 0 });

            Assert.Equal(only.Id, state.CurrentMovieId);
            Assert.Equal(PlaybackStatus.Paused, state.Status);
            Assert.Equal(1200, state.Position);
            Assert.Equal(5, state.Version);
        }

        [Fact]
        public async Task AdvanceAsync_LastOfSeveral_PausesAtTheEnd()
        {
            var service = CreateService(out var db);
            var roomId = Guid.NewGuid();

            var first = new Movie { RoomId = roomId, Title = "Ep1", EpisodeNumber = 1, FileName = "a.mp4", StorageLocation = "x", Duration = 600 };
            var last = new Movie { RoomId = roomId, Title = "Ep2", EpisodeNumber = 2, FileName = "b.mp4", StorageLocation = "y", Duration = 800 };
            db.Movies.AddRange(first, last);
            db.PlaybackStates.Add(new PlaybackState
            {
                RoomId = roomId,
                CurrentMovieId = last.Id,
                Status = PlaybackStatus.Playing,
                Position = 800,
                Version = 9
            });
            await db.SaveChangesAsync();

            var state = await service.AdvanceAsync(roomId, new PlaybackCommandRequest { MovieId = last.Id, Position = 0 });

            Assert.Equal(last.Id, state.CurrentMovieId);
            Assert.Equal(PlaybackStatus.Paused, state.Status);
            Assert.Equal(800, state.Position);
        }

        [Fact]
        public async Task AdvanceAsync_SecondClientAlreadyAdvanced_IsANoOp()
        {
            var service = CreateService(out var db);
            var roomId = Guid.NewGuid();

            var first = new Movie { RoomId = roomId, Title = "Ep1", EpisodeNumber = 1, FileName = "a.mp4", StorageLocation = "x", Duration = 1200 };
            var second = new Movie { RoomId = roomId, Title = "Ep2", EpisodeNumber = 2, FileName = "b.mp4", StorageLocation = "y", Duration = 900 };
            db.Movies.AddRange(first, second);
            db.PlaybackStates.Add(new PlaybackState
            {
                RoomId = roomId,
                CurrentMovieId = second.Id,
                Status = PlaybackStatus.Playing,
                Position = 0.3,
                Version = 8
            });
            await db.SaveChangesAsync();

            var state = await service.AdvanceAsync(roomId, new PlaybackCommandRequest { MovieId = first.Id, Position = 0 });

            Assert.Equal(second.Id, state.CurrentMovieId);
            Assert.Equal(8, state.Version);
        }

        [Fact]
        public async Task AdvanceAsync_AlreadyPausedAtTheEnd_IsANoOp()
        {
            var service = CreateService(out var db);
            var roomId = Guid.NewGuid();

            var only = new Movie { RoomId = roomId, Title = "Only", EpisodeNumber = 1, FileName = "a.mp4", StorageLocation = "x", Duration = 1200 };
            db.Movies.Add(only);
            db.PlaybackStates.Add(new PlaybackState
            {
                RoomId = roomId,
                CurrentMovieId = only.Id,
                Status = PlaybackStatus.Paused,
                Position = 1200,
                Version = 6
            });
            await db.SaveChangesAsync();

            var state = await service.AdvanceAsync(roomId, new PlaybackCommandRequest { MovieId = only.Id, Position = 0 });

            Assert.Equal(6, state.Version);
        }

        [Fact]
        public async Task GetCurrentStateAsync_NoState_ReturnsNull()
        {
            var service = CreateService(out _);
            var result = await service.GetCurrentStateAsync(Guid.NewGuid());
            Assert.Null(result);
        }

        [Fact]
        public async Task PauseAtCurrentPositionAsync_NoState_ReturnsNull()
        {
            var service = CreateService(out _);

            var state = await service.PauseAtCurrentPositionAsync(Guid.NewGuid());

            Assert.Null(state);
        }

        [Fact]
        public async Task PauseAtCurrentPositionAsync_AlreadyPaused_ReturnsNull()
        {
            var service = CreateService(out var db);
            var roomId = Guid.NewGuid();
            db.PlaybackStates.Add(new PlaybackState { RoomId = roomId, Status = PlaybackStatus.Paused, Position = 50, Version = 2 });
            await db.SaveChangesAsync();

            var state = await service.PauseAtCurrentPositionAsync(roomId);

            Assert.Null(state);
        }

        [Fact]
        public async Task PauseAtCurrentPositionAsync_Playing_PausesAtWherePlaybackHasDriftedTo()
        {
            var service = CreateService(out var db);
            var roomId = Guid.NewGuid();
            var seededAt = DateTime.UtcNow.AddSeconds(-30);
            db.PlaybackStates.Add(new PlaybackState
            {
                RoomId = roomId,
                Status = PlaybackStatus.Playing,
                Position = 100,
                Version = 4,
                UpdatedAt = seededAt
            });
            await db.SaveChangesAsync();

            // Pausing has to freeze the position where the video really is, not
            // where the last command left it — so it advances by however long the
            // room has been drifting since. Measured against a captured window
            // rather than a fixed tolerance, which goes flaky on a slow machine.
            var before = DateTime.UtcNow;
            var state = await service.PauseAtCurrentPositionAsync(roomId);
            var after = DateTime.UtcNow;

            Assert.NotNull(state);
            Assert.Equal(PlaybackStatus.Paused, state!.Status);
            Assert.Equal(5, state.Version);
            Assert.InRange(state.Position,
                100 + (before - seededAt).TotalSeconds,
                100 + (after - seededAt).TotalSeconds);
        }
    }
}
