using Microsoft.EntityFrameworkCore;
using SyncWatch.Data;
using SyncWatch.Dtos;
using SyncWatch.Models;
using SyncWatch.Services.Exception;

namespace SyncWatch.Services
{
    public class PlaybackService : IPlaybackService
    {
        private readonly DatabaseContext _db;
        private readonly ILogger<PlaybackService> _logger;

        public PlaybackService(DatabaseContext db, ILogger<PlaybackService> logger)
        {
            _db = db;
            _logger = logger;
        }

        public async Task<PlaybackState> PlayAsync(Guid roomId, PlaybackCommandRequest request)
        {
            var state = await _db.PlaybackStates.FindAsync(roomId);

            if (state == null)
            {
                _logger.LogInformation("Creating new PlaybackState for Room {RoomId}.", roomId);

                state = new PlaybackState
                {
                    RoomId = roomId,
                    CurrentMovieId = request.MovieId,
                    Status = PlaybackStatus.Playing,
                    Position = request.Position,
                    Version = 1,
                    UpdatedAt = DateTime.UtcNow
                };
                _db.PlaybackStates.Add(state);
            }
            else
            {
                state.Status = PlaybackStatus.Playing;
                state.Position = request.Position;
                state.Version += 1;
                state.UpdatedAt = DateTime.UtcNow;
            }

            await _db.SaveChangesAsync();

            return state;
        }

        public async Task<PlaybackState> PauseAsync(Guid roomId, PlaybackCommandRequest request)
        {
            var state = await _db.PlaybackStates.FindAsync(roomId);

            if (state == null)
            {
                _logger.LogWarning("Pause rejected: no PlaybackState exists yet for Room {RoomId}.", roomId);
                throw new PlaybackStateNotFoundException();
            }

            state.Status = PlaybackStatus.Paused;
            state.Position = request.Position;
            state.Version += 1;
            state.UpdatedAt = DateTime.UtcNow;

            await _db.SaveChangesAsync();

            return state;
        }

        public async Task<PlaybackState> SeekAsync(Guid roomId, PlaybackCommandRequest request)
        {
            var state = await _db.PlaybackStates.FindAsync(roomId);

            if (state == null)
            {
                _logger.LogWarning("Seek rejected: no PlaybackState exists yet for Room {RoomId}.", roomId);
                throw new PlaybackStateNotFoundException();
            }

            state.Position = request.Position;
            state.Version += 1;
            state.UpdatedAt = DateTime.UtcNow;

            await _db.SaveChangesAsync();

            return state;
        }

        public async Task<PlaybackState> SwitchEpisodeAsync(Guid roomId, PlaybackCommandRequest request)
        {
            var state = await _db.PlaybackStates.FindAsync(roomId);

            if (state == null)
            {
                _logger.LogWarning("SwitchEpisode rejected: no PlaybackState exists yet for Room {RoomId}.", roomId);
                throw new PlaybackStateNotFoundException();
            }

            var movie = await _db.Movies.FindAsync(request.MovieId);

            if (movie == null || movie.RoomId != roomId)
            {
                _logger.LogWarning("SwitchEpisode rejected: Movie {MovieId} does not belong to Room {RoomId}.", request.MovieId, roomId);
                throw new MovieNotFoundException();
            }

            state.CurrentMovieId = movie.Id;
            state.Position = 0;
            state.Version += 1;
            state.UpdatedAt = DateTime.UtcNow;

            await _db.SaveChangesAsync();

            return state;
        }

        public async Task<PlaybackState> AdvanceAsync(Guid roomId, PlaybackCommandRequest request)
        {
            var state = await _db.PlaybackStates.FindAsync(roomId);

            if (state == null)
            {
                _logger.LogWarning("Advance rejected: no PlaybackState exists yet for Room {RoomId}.", roomId);
                throw new PlaybackStateNotFoundException();
            }

            if (state.CurrentMovieId != request.MovieId || state.Status == PlaybackStatus.Paused)
            {
                return state;
            }

            var current = await _db.Movies.FindAsync(state.CurrentMovieId);

            if (current == null)
            {
                _logger.LogWarning("Advance rejected: Movie {MovieId} does not exist.", state.CurrentMovieId);
                throw new MovieNotFoundException();
            }

            var next = await _db.Movies
                .Where(m => m.RoomId == roomId && m.EpisodeNumber > current.EpisodeNumber)
                .OrderBy(m => m.EpisodeNumber)
                .FirstOrDefaultAsync();

            if (next != null)
            {
                state.CurrentMovieId = next.Id;
                state.Position = 0;
            }
            else
            {
                state.Status = PlaybackStatus.Paused;
                state.Position = current.Duration;
            }

            state.Version += 1;
            state.UpdatedAt = DateTime.UtcNow;

            await _db.SaveChangesAsync();

            return state;
        }


        public async Task<PlaybackState?> GetCurrentStateAsync(Guid roomId)
        {
            return await _db.PlaybackStates.FindAsync(roomId);
        }
    }
}