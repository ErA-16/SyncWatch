using SyncWatch.Dtos;
using SyncWatch.Models;

namespace SyncWatch.Services
{
    public interface IPlaybackService
    {
        Task<PlaybackState> PlayAsync(Guid roomId, PlaybackCommandRequest request);
        Task<PlaybackState> PauseAsync(Guid roomId, PlaybackCommandRequest request);
        Task<PlaybackState> SeekAsync(Guid roomId, PlaybackCommandRequest request);
        Task<PlaybackState> SwitchEpisodeAsync(Guid roomId, PlaybackCommandRequest request);

        Task<PlaybackState> AdvanceAsync(Guid roomId, PlaybackCommandRequest request);

        // Pauses the room from the server side, freezing the position where
        // playback actually is rather than where the last command left it.
        // Returns null when there is nothing to pause.
        Task<PlaybackState?> PauseAtCurrentPositionAsync(Guid roomId);

        Task<PlaybackState?> GetCurrentStateAsync(Guid roomId);
    }
}